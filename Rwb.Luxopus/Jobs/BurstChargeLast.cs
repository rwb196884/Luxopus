using Microsoft.Extensions.Logging;
using Rwb.Luxopus.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Jobs
{

    /// <summary>
    /// <para>
    /// Absorb bursts of production.
    /// Simpler version that uses the new FUNC_CHARGE_LAST setting instead of fucking about with the battery charge rate.
    /// </para>
    /// </summary>
    public class BurstChargeLast : BurstManager
    {
        private readonly IBurstLogService _BurstLog;
        private readonly ILuxopusPlanService _Plans;
        private readonly ILuxService _Lux;
        private readonly IInfluxQueryService _InfluxQuery;
        private readonly IBatteryService _Batt;
        private readonly BatteryTargetService _BatteryTargetService;

        public BurstChargeLast(
            ILogger<Burst> logger,
            IBurstLogService burstLog,
            ILuxopusPlanService plans,
            ILuxService lux,
            IInfluxQueryService influxQuery,
            IBatteryService batt,
            BatteryTargetService batteryTargetService)
            : base(logger)
        {
            _BurstLog = burstLog;
            _Plans = plans;
            _Lux = lux;
            _InfluxQuery = influxQuery;
            _Batt = batt;
            _BatteryTargetService = batteryTargetService;
        }

        protected override async Task WorkAsync(CancellationToken cancellationToken)
        {
            // Suggested cron: * 9-15 * * *

            DateTime t0 = DateTime.UtcNow;

            Plan? plan = null;
            try
            {
                plan = _Plans.Load(t0);

                if (plan == null)
                {
                    plan = _Plans.Load(t0.AddDays(-2));
                    if (plan != null)
                    {
                        Logger.LogWarning($"No plan at UTC {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")}. Using plan from {plan.Current.Start.ToString("yyyy-MM-dd HH:mm")}.");
                        foreach (PeriodPlan p in plan.Plans)
                        {
                            p.Start = p.Start.AddDays(2);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Error loading plan.");
                await _Lux.SetBatteryChargeRateAsync(100);
                await _Lux.SetChargeLastAsync(false);
            }

            if (plan == null || plan.Next == null)
            {
                Logger.LogError($"No plan at UTC {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")}.");
                // If there is plan then default configuration will be set.
                return;
            }

            PeriodPlan? currentPeriod = plan?.Current;

            if (currentPeriod == null || currentPeriod.Action == null)
            {
                Logger.LogError($"No current plan at UTC {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")}.");
                return;
            }

            if (currentPeriod.Action.ChargeFromGrid > 0 || currentPeriod.Action.DischargeToGrid < 100)
            {
                return;
            }

            int battLevelEnd = _BatteryTargetService.DefaultBatteryLevelEnd;
            if (plan.Next.Buy <= 0)
            {
                battLevelEnd -= _Batt.CapacityKiloWattHoursToPercent(plan.Plans.FutureFreeHoursBeforeNextDischarge(currentPeriod) * 3.2);
                int battLevelZ = await _InfluxQuery.GetBatteryLevelAsync(DateTime.UtcNow);

                battLevelEnd = battLevelEnd < battLevelZ ? battLevelZ : battLevelEnd;
            }

            BatteryTargetInfo bti = await _BatteryTargetService.Compute(plan, battLevelEnd);

            if (t0 < bti.GenerationStart || t0 > bti.GenerationEnd) { return; }

            (DateTime _, long generationMax) = (await _InfluxQuery.QueryAsync(@$"
            from(bucket: ""solar"")
              |> range(start: {plan.Current.Start.ToString("yyyy-MM-ddTHH:mm:00Z")}, stop: now())
              |> filter(fn: (r) => r[""_measurement""] == ""inverter"" and r[""_field""] == ""generation"")
              |> max()")).First().FirstOrDefault<long>();

            if (generationMax < 2500)
            {
                return;
            }

            // We're good to go...
            StringBuilder actions = new StringBuilder();
            StringBuilder actionInfo = new StringBuilder();

            Dictionary<string, string> settings = await _Lux.GetSettingsAsync();
            while (settings.Any(z => z.Value == "DATAFRAME_TIMEOUT"))
            {
                settings = await _Lux.GetSettingsAsync();
            }
            if (settings.Any(z => z.Value == "DEVICE_OFFLINE")) { return; }
            int battChargeRate = _Lux.GetBatteryChargeRate(settings);
            int battChargeRateWanted = battChargeRate; // No change.

            // Get the planned discharge settings -- we may override them.
            LuxAction dischargeToGridCurrent = _Lux.GetDischargeToGrid(settings);
            LuxAction dischargeToGridWanted = LuxAction.NextDisharge(plan, dischargeToGridCurrent, false) ?? dischargeToGridCurrent.Clone();
            if (dischargeToGridWanted.Start < DateTime.UtcNow && dischargeToGridWanted.End > DateTime.UtcNow)
            {
                dischargeToGridWanted.Enable = false;
            }

            LuxAction chargeFromGridCurrent = _Lux.GetChargeFromGrid(settings);
            LuxAction chargeFromGridWanted = LuxAction.NextCharge(plan, chargeFromGridCurrent, false) ?? chargeFromGridCurrent.Clone();
            if (chargeFromGridWanted.Start.TimeOfDay < DateTime.UtcNow.TimeOfDay && chargeFromGridWanted.End.TimeOfDay > DateTime.UtcNow.TimeOfDay)
            {
                chargeFromGridWanted.Enable = false;
            }

            bool chargeLast = _Lux.GetChargeLast(settings);
            bool chargeLastWanted = chargeLast;

            string runtimeInfo = await _Lux.GetInverterRuntimeAsync();

            DateTime tBattChargeFrom = bti.GenerationStart > currentPeriod.Start ? bti.GenerationStart : currentPeriod.Start;

            int battLevelStart = await _InfluxQuery.GetBatteryLevelAsync(tBattChargeFrom);
            DateTime nextPlanCheck = DateTime.UtcNow.StartOfHalfHour().AddMinutes(30);

            long generationRecentMax = (await _InfluxQuery.QueryAsync(@$"
from(bucket: ""solar"")
  |> range(start: -45m, stop: now())
  |> filter(fn: (r) => r[""_measurement""] == ""inverter"" and r[""_field""] == ""generation"")
  |> max()")
).First().Records.First().GetValue<long>();

            double generationRecentMean = (await _InfluxQuery.QueryAsync(@$"
from(bucket: ""solar"")
  |> range(start: -45m, stop: now())
  |> filter(fn: (r) => r[""_measurement""] == ""inverter"" and r[""_field""] == ""generation"")
  |> mean()")
).First().Records.First().GetValue<double>();

            using (JsonDocument j = JsonDocument.Parse(runtimeInfo))
            {
                JsonElement.ObjectEnumerator r = j.RootElement.EnumerateObject();
                int generation = r.Single(z => z.Name == "ppv").Value.GetInt32();
                //int export = r.Single(z => z.Name == "pToGrid").Value.GetInt32();
                int inverterOutput = r.Single(z => z.Name == "pinv").Value.GetInt32();
                int battLevel = r.Single(z => z.Name == "soc").Value.GetInt32();
                //int battCharge = r.Single(z => z.Name == "pCharge").Value.GetInt32();
                //int battDisharge = r.Single(z => z.Name == "pDisharge").Value.GetInt32();

                actionInfo.AppendLine($"       Generation: {generation}W");
                actionInfo.AppendLine($"  Inverter output: {inverterOutput}W");
                actionInfo.AppendLine($"    Battery level: {battLevel}%");
                actionInfo.AppendLine($"   Battery target: {bti.TargetDescription})");
                actionInfo.AppendLine($" Battery headroom: {bti.HeadroomScaled}% scaled of total {100 - bti.BatteryLevelEnd}%)");
                actionInfo.AppendLine($"      Charge last: {(chargeLast ? "yes" : "no")}");
                actionInfo.AppendLine($"Discharge to grid: {dischargeToGridCurrent})");
                actionInfo.AppendLine($" Charge from grid: {chargeFromGridCurrent})");

                // Are we behind schedule?
                if (battLevel < bti.BatteryTarget + bti.HeadroomScaled)
                {
                    double b = _Batt.CapacityPercentToKiloWattHours(bti.BatteryTarget + bti.HeadroomScaled - battLevel);
                    actionInfo.AppendLine($"Battery level {battLevel}% is less than target {bti.BatteryTarget}% plus headroom {bti.HeadroomScaled}%; behind by {b:#,##0.0}kWh.");
                }
                else if (battLevel > bti.BatteryTarget + bti.HeadroomScaled)
                {
                    double a = _Batt.CapacityPercentToKiloWattHours(battLevel - bti.BatteryTarget - bti.HeadroomScaled);
                    actionInfo.AppendLine($"Battery level {battLevel}% is greater than target {bti.BatteryTarget}% plus headroom {bti.HeadroomScaled}%; ahead by {a:#,##0.0}kWh.");
                }

                if (t0.Month >= 4 && t0.Month <= 8 && t0.Hour <= 9 && (bti.ScaleMethod == ScaleMethod.Slow || generationRecentMean > 800 || bti.PredictionBatteryPercent > 150) && battLevel >= bti.BatteryTarget - 5)
                {
                    chargeLastWanted = true;
                    battChargeRateWanted = 100;
                    actionInfo.AppendLine($"Predicted to be a good day ({bti.PredictionKWh:0.0}kWh, {bti.PredictionBatteryPercent}%) or high recent generation ({generationRecentMean / 1000:#0.0}kW) therefore charge last before 9am.");
                }
                else if(generationRecentMean < bti.ChargeRateNeededHkW)
                {
                    chargeLastWanted = false;
                    battChargeRateWanted = 100;
                    actionInfo.AppendLine($"Recent generation {generationRecentMean:0.0}kW is less than charge rate required {bti.ChargeRateNeededHkW:0.0}kW.");
                }
                else if (generation > 3200)
                {
                    // Forced discharge causes clipping.

                    // So does charge from grid. (E.g., when electricity is free.)
                    if (chargeFromGridCurrent.Enable && chargeFromGridCurrent.Start < DateTime.UtcNow && chargeFromGridCurrent.End > DateTime.UtcNow)
                    {
                        chargeFromGridWanted.Enable = false;
                    }

                    // Generation probably not limited therefore send less to battery.
                    if (battLevel < bti.BatteryTarget)
                    {
                        chargeLastWanted = false;
                        battChargeRateWanted = 99;
                        actionInfo.AppendLine($"Charge last disabled because behind target {bti.BatteryTarget}%. Required charge rate (including headroom of {bti.HeadroomScaled}%) is {bti.ChargeRateNeededHPercent}%. Overidden to {92}% to catch up.");

                    }
                    else if (battLevel < bti.BatteryTarget + bti.HeadroomScaled)
                    {
                         actionInfo.AppendLine($"Behind target {bti.BatteryTarget}% plus headroom {bti.HeadroomScaled}%; required charge rate is {bti.ChargeRateNeededHPercent}%.");
                        // Increase the batt charge rate to avoid clipping.
                        double kwForBattAfterCL = (Convert.ToDouble(generation) - 3600.0) / 1000.0;
                        int pcForBattAfterCL = _Batt.RoundPercent(_Batt.TransferKiloWattsToPercent(kwForBattAfterCL));
                        if (battChargeRateWanted < pcForBattAfterCL)
                        {
                            actionInfo.AppendLine($"  Generation {generation:0.0}kW leaves {kwForBattAfterCL:0.0}kW ({pcForBattAfterCL}%) to battery after charge last but charge rate needed is {bti.ChargeRateNeededHkW:0.0}kW ({bti.ChargeRateNeededHPercent}%) therefore charge last.");
                            battChargeRateWanted = 98;
                            chargeLastWanted = true;
                        }
                        else
                        {
                            actionInfo.AppendLine($"  Generation {generation:0.0}kW leaves {kwForBattAfterCL:0.0}kW ({pcForBattAfterCL}%) to battery after charge last but charge rate needed is {bti.ChargeRateNeededHkW:0.0}kW ({bti.ChargeRateNeededHPercent}%) therefore do not charge last.");
                            battChargeRateWanted = bti.ChargeRateNeededHPercent;
                            chargeLastWanted = false;
                        }
                    }
                    else
                    {
                        battChargeRateWanted = 97;
                        chargeLastWanted = true;
                        actionInfo.AppendLine($"Charge last enabled because ahead of target.");
                    }
                }
                else
                {
                    // Low generation.
                    if (generationMax > 4000 && generationRecentMax > 3000 && generation /* inverterOutput includes batt discharge */ < 3100 && battLevel > bti.BatteryTarget + bti.HeadroomScaled)
                    {
                        // It's gone quiet but it might get busy again: try to discharge some over-charge.
                        dischargeToGridWanted = new LuxAction()
                        {
                            Enable = true,
                            Start = currentPeriod.Start,
                            End = dischargeToGridCurrent.End >= plan.Next.Start ? dischargeToGridCurrent.End : plan.Next.Start,
                            Limit = bti.BatteryTarget - 2,
                            Rate = 91
                        };
                        battChargeRateWanted = 96;
                        chargeLastWanted = true;
                        actionInfo.AppendLine($"Generation peak of {generationMax} recent {generationRecentMax} but currently {generation}. Battery level {battLevel}%, target of {bti.BatteryTarget}% therefore take opportunity to discharge.");
                    }
                    else if (battLevel < bti.BatteryTarget 
                        //&& battLevel < _Batt.BatteryMinimumLimit + _Batt.MaxDischarge * 3 
                        && plan.Next != null && Plan.DischargeToGridCondition(plan.Next) 
                        && t0 > plan.Next.Start.AddHours(-2) 
                        && plan.Current.Buy * 1.1M < plan.Next.Sell && generationRecentMax < 3000)
                    {
                        // If buy is lower then next sell then we can buy to catch up.
                        double kWh = _Batt.CapacityPercentToKiloWattHours(bti.BatteryTarget - battLevel);
                        double dt = (plan.Next.Start - t0).TotalHours;
                        int rate = _Batt.TransferKiloWattsToPercent(kWh / dt);
                        if (rate < 13) { rate = 13; }
                        if (rate > 100) { rate = 100; }
                        chargeFromGridWanted = new LuxAction()
                        {
                            Enable = true,
                            Start = plan.Current.Start,
                            End = plan.Next.Start,
                            Limit = bti.BatteryTarget,
                            Rate = rate
                        };
                        actionInfo.AppendLine($"Next sell {plan.Next.Sell:#,##0.000} > current buy {plan.Current.Buy:#,##0.000} therfore top up from {battLevel}% to target {bti.BatteryTarget}%.");
                        chargeLastWanted = false;
                        battChargeRateWanted = 95;
                    }
                    else
                    {
                        chargeLastWanted = false;
                        battChargeRateWanted = 94;
                    }

                    if (plan.Current.Buy <= 0)
                    {
                        // Fill your boots.
                        chargeFromGridWanted = new LuxAction()
                        {
                            Enable = true,
                            Start = plan.Current.Start,
                            End = plan.Next.Start,
                            Limit = 100,
                            Rate = 100
                        };

                    }
                }

                if (battChargeRateWanted < bti.ChargeRateNeededHPercent && battLevel < bti.BatteryTarget + bti.HeadroomScaled)
                {
                    actionInfo.AppendLine($"{bti.ChargeRateNeededHkW:0.0}kWh needed to get from {battLevel}% +  (should be {bti.TargetDescription}) to {battLevelEnd}% + {bti.HeadroomTotal}% headroom by {bti.GenerationEnd:HH:mm} (mean rate {bti.ChargeRateNeededHPercent:0.0}kW -> {bti.ChargeRateNeededHPercent}%). But current setting is {battChargeRate}%.");
                    battChargeRateWanted = bti.ChargeRateNeededHPercent;
                }
            }

            // Apply any changes.
            if (chargeLast != chargeLastWanted)
            {
                await _Lux.SetChargeLastAsync(chargeLastWanted);
                actions.AppendLine($"SetChargeLastAsync({chargeLastWanted}) was {chargeLast}.");
            }

            bool changedDischarge = await _Lux.SetDischargeToGrid(dischargeToGridCurrent, dischargeToGridWanted);
            if (changedDischarge)
            {
                actions.AppendLine($"Discharge to grid was: {dischargeToGridCurrent}");
                actions.AppendLine($" Discharge to grid is: {dischargeToGridWanted}");
            }

            bool changedCharge = await _Lux.SetChargeFromGrid(chargeFromGridCurrent, chargeFromGridWanted);
            if (changedCharge)
            {
                actions.AppendLine($"Charge from grid was: {chargeFromGridCurrent}");
                actions.AppendLine($"Charge from grid is : {chargeFromGridWanted}");
                if (chargeFromGridWanted.Enable)
                {
                    actionInfo.AppendLine($"  Buy @ {plan.Current.Buy:#,##0.000}.");
                }
            }

            if (battChargeRateWanted != battChargeRate)
            {
                await _Lux.SetBatteryChargeRateAsync(battChargeRateWanted);
                actions.AppendLine($"SetBatteryChargeRate({battChargeRateWanted}) was {battChargeRate}.");
            }

            // Report any changes.
            if (actions.Length > 0)
            {
                _BurstLog.Write(actions.ToString() + Environment.NewLine + actionInfo.ToString());
                // spammy _Email.SendEmail($"Burst at UTC {DateTime.UtcNow.ToString("dd MMM HH:mm")}", actions.ToString() + Environment.NewLine + actionInfo.ToString());
                Logger.LogInformation("Burst made changes: " + Environment.NewLine + actions.ToString());
            }
        }
    }
}
