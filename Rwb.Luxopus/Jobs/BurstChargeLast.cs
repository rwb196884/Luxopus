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
        private readonly BatteryUsageProfileService _BatteryUsageProfileService;

        public BurstChargeLast(
            ILogger<Burst> logger,
            IBurstLogService burstLog,
            ILuxopusPlanService plans,
            ILuxService lux,
            IInfluxQueryService influxQuery,
            IBatteryService batt,
            BatteryTargetService batteryTargetService,
            BatteryUsageProfileService batteryUsageProfileService)
            : base(logger)
        {
            _BurstLog = burstLog;
            _Plans = plans;
            _Lux = lux;
            _InfluxQuery = influxQuery;
            _Batt = batt;
            _BatteryTargetService = batteryTargetService;
            _BatteryUsageProfileService = batteryUsageProfileService;
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

            (DateTime _, long generationMax) = (await _InfluxQuery.QueryAsync(@$"
            from(bucket: ""solar"")
              |> range(start: {plan.Current.Start.ToString("yyyy-MM-ddTHH:mm:00Z")}, stop: now())
              |> filter(fn: (r) => r[""_measurement""] == ""inverter"" and r[""_field""] == ""generation"")
              |> max()")).First().FirstOrDefault<long>();

            if (generationMax < 2500)
            {
                return;
            }

            StringBuilder actionInfo = new StringBuilder();

            Dictionary<string, string> settings = await _Lux.GetSettingsAsync();
            while (settings.Any(z => z.Value == "DATAFRAME_TIMEOUT"))
            {
                settings = await _Lux.GetSettingsAsync();
            }
            if (settings.Any(z => z.Value == "DEVICE_OFFLINE")) { return; }

            // Plan A.
            int battLevelEnd = _Batt.BatteryMinimumLimit + _Batt.MaxDischarge * 3; // TODO: work out from plan.

            // Plan B.
            //(int battStart, _) = await _InfluxQuery.GetBatteryStartLevelAsync();
            //int battOffset = battStart > _Batt.BatteryMinimumLimit ? battStart : _Batt.BatteryMinimumLimit;
            //int battLevelEnd = battOffset + _Batt.MaxDischarge * 3; // TODO: work out from plan.

            // Plan C.
            //List<FluxTable> bupH = await _InfluxQuery.QueryAsync(Query.HourlyBatteryUse, t0);
            //BatteryUsageProfile bup = new BatteryUsageProfile(bupH);
            DateTime startOfGeneration = plan.Current.Start.Date.AddHours(10);
            DateTime endOfGeneration = plan.Current.Start.Date.AddHours(15);
            try
            {
                (startOfGeneration, _) = (await _InfluxQuery.QueryAsync(Query.StartOfGeneration, plan.Current.Start)).First().FirstOrDefault<double>();
                (endOfGeneration, _) = (await _InfluxQuery.QueryAsync(Query.EndOfGeneration, plan.Current.Start)).First().FirstOrDefault<double>();
            }
            catch { }

            int battUse = _Batt.CapacityKiloWattHoursToPercent(0.15 * (
                24 - endOfGeneration.Hour /* End of generation to midnight */
                + startOfGeneration.Hour /* Midnight to start of generation. */
                ));

            int battUseP = _Batt.CapacityKiloWattHoursToPercent(await _BatteryUsageProfileService.GetKwkhAsync(endOfGeneration.DayOfWeek, endOfGeneration.Hour, startOfGeneration.Hour));
            
            battLevelEnd = Math.Min(100, battLevelEnd + battUse);

            // TODO: this assumes flux; solar charge target should be set by the plan.

            (_, int bcSince, int bcPeriod) = _Lux.GetBatteryCalibration(settings);
            if (bcSince > bcPeriod - 5)
            {
                battLevelEnd = 100;
            }
            actionInfo.AppendLine($"Target is mimimum {_Batt.BatteryMinimumLimit}% plus use {battUse}% plus maximum dischargeable {_Batt.MaxDischarge * 3}% = {battLevelEnd}%.");


            int battLevel = await _InfluxQuery.GetBatteryLevelAsync(DateTime.UtcNow);
            if ((plan.Next?.Buy ?? 1) <= 0)
            {
                battLevelEnd -= _Batt.CapacityKiloWattHoursToPercent(plan.Plans.FutureFreeHoursBeforeNextDischarge(plan.Current!) * 3.2);
                battLevelEnd = battLevelEnd < battLevel ? battLevel : battLevelEnd;
            }

            BatteryTargetInfo bti = await _BatteryTargetService.Compute(plan, battLevelEnd);

            if (t0 < bti.GenerationStart || t0 > bti.GenerationEnd) { return; }


            // We're good to go...

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



            long generationRecentMax = (await _InfluxQuery.QueryAsync(@$"
from(bucket: ""solar"")
  |> range(start: -10m, stop: now())
  |> filter(fn: (r) => r[""_measurement""] == ""inverter"" and r[""_field""] == ""generation"")
  |> max()")
).First().Records.First().GetValue<long>();
            double kwMaxForBattAfterCL = (Convert.ToDouble(generationRecentMax) - 3600) / 1000;
            if (kwMaxForBattAfterCL < 0)
            {
                kwMaxForBattAfterCL = 0;
            }
            int pcMaxForBattAfterCL = kwMaxForBattAfterCL == 0 ? 0 : _Batt.RoundPercent(_Batt.TransferKiloWattsToPercent(kwMaxForBattAfterCL));

            double generationRecentMean = (await _InfluxQuery.QueryAsync(@$"
from(bucket: ""solar"")
  |> range(start: -10m, stop: now())
  |> filter(fn: (r) => r[""_measurement""] == ""inverter"" and r[""_field""] == ""generation"")
  |> mean()")
).First().Records.First().GetValue<double>();
            double kwMeanForBattAfterCL = (Convert.ToDouble(generationRecentMean) - 3600) / 1000;
            if (kwMeanForBattAfterCL < 0)
            {
                kwMeanForBattAfterCL = 0;
            }
            int pcMeanForBattAfterCL = kwMeanForBattAfterCL == 0 ? 0 : _Batt.RoundPercent(_Batt.TransferKiloWattsToPercent(kwMeanForBattAfterCL));

            string runtimeInfo = await _Lux.GetInverterRuntimeAsync();
            using (JsonDocument j = JsonDocument.Parse(runtimeInfo))
            {
                JsonElement.ObjectEnumerator r = j.RootElement.EnumerateObject();
                int generation = r.Single(z => z.Name == "ppv").Value.GetInt32();
                //int export = r.Single(z => z.Name == "pToGrid").Value.GetInt32();
                int inverterOutput = r.Single(z => z.Name == "pinv").Value.GetInt32();
                battLevel = r.Single(z => z.Name == "soc").Value.GetInt32();
                //int battCharge = r.Single(z => z.Name == "pCharge").Value.GetInt32();
                //int battDisharge = r.Single(z => z.Name == "pDisharge").Value.GetInt32();

                double kwCurrentForBattAfterCL = (Convert.ToDouble(generation) - 3600) / 1000;
                if (kwCurrentForBattAfterCL < 0)
                {
                    kwCurrentForBattAfterCL = 0;
                }
                int pcCurrentForBattAfterCL = _Batt.RoundPercent(_Batt.TransferKiloWattsToPercent(kwCurrentForBattAfterCL));

                actionInfo.AppendLine($"          Generation: {generation}W leaves {kwCurrentForBattAfterCL:0.0}kW ({pcCurrentForBattAfterCL}%) for battery after charge last.");
                actionInfo.AppendLine($"      Generation max: {generationRecentMax:0}W leaves {kwMaxForBattAfterCL:0.0}kW ({pcMaxForBattAfterCL}%) for battery after charge last.");
                actionInfo.AppendLine($"     Generation mean: {generationRecentMean:0}W leaves {kwMeanForBattAfterCL:0.0}kW ({pcMeanForBattAfterCL}%) for battery after charge last.");
                actionInfo.AppendLine($"     Inverter output: {inverterOutput}W");
                actionInfo.AppendLine($"         Charge rate: {battChargeRate}%");
                actionInfo.AppendLine($"   Charging required: {bti.ChargeDescription}");
                actionInfo.AppendLine($"       Battery level: {battLevel}%");
                actionInfo.AppendLine($"      Battery target: {bti.TargetDescription}");
                actionInfo.AppendLine($"    Battery headroom: {bti.HeadroomScaled}% scaled of total {100 - bti.BatteryLevelEnd}%");

                actionInfo.AppendLine($"       Charge last: {(chargeLast ? "on" : "off")}");
                //actionInfo.AppendLine($" Discharge to grid: {dischargeToGridCurrent}");
                //actionInfo.AppendLine($"  Charge from grid: {chargeFromGridCurrent}");

                //// Are we behind schedule?
                //if (battLevel < bti.BatteryTarget + bti.HeadroomScaled)
                //{
                //    double b = _Batt.CapacityPercentToKiloWattHours(bti.BatteryTarget + bti.HeadroomScaled - battLevel);
                //    actionInfo.AppendLine($"Battery level {battLevel}% is less than target {bti.BatteryTarget}% plus headroom {bti.HeadroomScaled}%; behind by {b:#,##0.0}kWh.");
                //}
                //else if (battLevel > bti.BatteryTarget + bti.HeadroomScaled)
                //{
                //    double a = _Batt.CapacityPercentToKiloWattHours(battLevel - bti.BatteryTarget - bti.HeadroomScaled);
                //    actionInfo.AppendLine($"Battery level {battLevel}% is greater than target {bti.BatteryTarget}% plus headroom {bti.HeadroomScaled}%; ahead by {a:#,##0.0}kWh.");
                //}

                // Are we behind schedule?
                double extraPowerNeeded = 0.0;
                int extraChargeRateNeeded = 0;
                if (battLevel < bti.BatteryTarget)
                {
                    extraPowerNeeded = _Batt.CapacityPercentToKiloWattHours(bti.BatteryTarget + bti.HeadroomScaled - battLevel);
                    extraChargeRateNeeded = _Batt.TransferKiloWattsToPercent(extraPowerNeeded * 2 /* Get it in th next half hour. */);
                }

                if (battLevel + bti.PredictionBatteryPercent >= 200 && DateTime.Now.Hour <= 9 && t0.Month >= 3 && t0.Month <= 8)
                {
                    chargeLastWanted = true;
                    battChargeRateWanted = 100;
                    actionInfo.AppendLine($"Batt level {battLevel}% plus prediction {bti.PredictionBatteryPercent}% is greater than 200%: charge last before 10am (local) March to August.");
                }
                else if (generationRecentMean / 1000 > bti.ChargeRateNeededHkW + (extraPowerNeeded * 2) && pcCurrentForBattAfterCL > bti.ChargeRateNeededHPercent + extraPowerNeeded)
                {
                    chargeLastWanted = true;
                    battChargeRateWanted = 100;
                    actionInfo.AppendLine($"Enable charge last because charge rate needed {bti.ChargeRateNeededHPercent}% (including headroom) is less than power available for battery after charge last {pcCurrentForBattAfterCL}% minus 5%.");
                }
                else if (generationRecentMean / 1000 < bti.ChargeRateNeededHkW)
                {
                    chargeLastWanted = false;
                    battChargeRateWanted = battLevel < bti.BatteryTarget + bti.HeadroomScaled ? 100 : bti.ChargeRateNeededHPercent;
                    actionInfo.AppendLine($"Recent generation {generationRecentMean / 1000:0.0}kW is less than charge rate required {bti.ChargeRateNeededkW:0.0}kW.");
                }
                else if (generation > 3200)
                {
                    // Forced discharge causes clipping.

                    // So does charge from grid. (E.g., when electricity is free.)
                    if (chargeFromGridCurrent.Enable && chargeFromGridCurrent.Start < DateTime.UtcNow && chargeFromGridCurrent.End > DateTime.UtcNow)
                    {
                        chargeFromGridWanted.Enable = false;
                    }

                    if (battLevel < bti.BatteryTarget)
                    {
                        chargeLastWanted = false;
                        battChargeRateWanted = Math.Min(100, bti.ChargeRateNeededHPercent + extraChargeRateNeeded);
                        actionInfo.AppendLine($"Battery charge rate increased to {battChargeRateWanted}% (need extra {extraChargeRateNeeded}%) to get extra {extraPowerNeeded:0.0}kW in the next half hour.");
                    }
                    else if (battLevel > bti.BatteryTarget + bti.HeadroomScaled)
                    {
                        chargeLastWanted = true;
                        battChargeRateWanted = 99;
                        actionInfo.AppendLine($"Enable charge last because battery level {bti.BatteryLevelCurrent}% is ahead of target {bti.BatteryTarget}% plus headroom {bti.HeadroomScaled}%.");
                    }
                    else if (bti.ChargeRateNeededPercent < pcCurrentForBattAfterCL - 5)
                    {
                        chargeLastWanted = true;
                        battChargeRateWanted = 98;
                        actionInfo.AppendLine($"Enable charge last because charge rate needed {bti.ChargeRateNeededPercent}% is less than power available for battery after charge last {pcCurrentForBattAfterCL}% minus 5%.");
                    }
                    else
                    {
                        chargeLastWanted = false;
                        battChargeRateWanted = Math.Min(100, bti.ChargeRateNeededPercent + 5);
                        actionInfo.AppendLine($"Disable charge last because charge rate needed {bti.ChargeRateNeededPercent}% is more than power available for battery after charge last {pcCurrentForBattAfterCL}% minus 5%.");
                    }

                    /*
                // Generation probably not limited therefore send less to battery.
                if (bti.BatteryLevelCurrent < bti.BatteryTarget)
                {
                    if (bti.ChargeRateNeededHPercent > pcCurrentForBattAfterCL - 8)
                    {
                        chargeLastWanted = false;
                        battChargeRateWanted = bti.ChargeRateNeededHPercent + 8;
                        actionInfo.AppendLine($"Disable charge last because battery level {bti.BatteryLevelCurrent}% is behind target {bti.BatteryTarget}% and charge rate needed {bti.ChargeRateNeededHPercent}% is more than power available for battery after charge last {pcCurrentForBattAfterCL}% minus 8%.");
                    }
                    else
                    {
                        chargeLastWanted = true;
                        battChargeRateWanted = 99;
                        actionInfo.AppendLine($"Enable charge last because battery level {bti.BatteryLevelCurrent}% is behind target {bti.BatteryTarget}% but charge rate needed {bti.ChargeRateNeededHPercent}% is less than power available for battery after charge last {pcCurrentForBattAfterCL}% minus 8%.");
                    }
                }
                else if (battLevel < bti.BatteryTarget + bti.HeadroomScaled)
                {
                    // Increase the batt charge rate to avoid clipping.
                    chargeLastWanted = true;
                    battChargeRateWanted = 98;
                    actionInfo.AppendLine($"Charge last enabled because battery level {bti.BatteryLevelCurrent}% is ahead of target {bti.BatteryTarget}% plus scaled headroom {bti.HeadroomScaled}%.");
                }
                else
                {
                    if (bti.ChargeRateNeededPercent > pcCurrentForBattAfterCL)
                    {
                        chargeLastWanted = false;
                        battChargeRateWanted = bti.ChargeRateNeededHPercent;
                        actionInfo.AppendLine($"Disable charge last because battery level {bti.BatteryLevelCurrent}% is ahead of target {bti.BatteryTarget}% and charge rate needed {bti.ChargeRateNeededPercent}% is more than power available for battery after charge last {pcCurrentForBattAfterCL}%.");
                    }
                    else
                    {
                        chargeLastWanted = true;
                        battChargeRateWanted = 97;
                        actionInfo.AppendLine($"Enable charge last because battery level {bti.BatteryLevelCurrent}% is ahead of target {bti.BatteryTarget}% and charge rate needed {bti.ChargeRateNeededPercent}% is less than power available for battery after charge last {pcCurrentForBattAfterCL}%.");
                    }
                }
                    */
                }
                else
                {
                    // Low generation.
                    if (battLevel > bti.BatteryTarget + bti.HeadroomScaled && generationMax > 4000 && generationRecentMax > 3000 && generation /* inverterOutput includes batt discharge */ < 3100)
                    {
                        // It's gone quiet but it might get busy again: try to discharge some over-charge.
                        dischargeToGridWanted = new LuxAction()
                        {
                            Enable = true,
                            Start = currentPeriod.Start,
                            End = dischargeToGridCurrent.End >= plan.Next.Start ? dischargeToGridCurrent.End : plan.Next.Start,
                            Limit = bti.BatteryTarget - (bti.HeadroomTotal > 0 ? 2 : 0),
                            Rate = 91
                        };
                        battChargeRateWanted = 96;
                        chargeLastWanted = true;
                        actionInfo.AppendLine($"Generation peak of {generationMax:0}W recent {generationRecentMax:0}W but currently {generation:0}W. Battery level {battLevel}%, target of {bti.BatteryTarget}% therefore take opportunity to discharge.");
                    }
                    else if (battLevel < bti.BatteryTarget
                        && bti.PredictionBatteryPercent < 120
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
                    else if(generationRecentMean / 1000 > bti.ChargeRateNeededkW)
                    {
                        actionInfo.AppendLine($"Generation {generationRecentMean / 1000:0.0}kW is greater than charge rate needed {bti.ChargeRateNeededkW:0.0}kW ({bti.ChargeRateNeededHkW:0.0}kW with headroom)");
                        chargeLastWanted = false;
                        battChargeRateWanted = bti.ChargeRateNeededHPercent;
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
                            Limit = bti.BatteryLevelEnd,
                            Rate = 100
                        };
                    }
                }

                //if (battChargeRateWanted < bti.ChargeRateNeededHPercent && battLevel < bti.BatteryTarget + bti.HeadroomScaled)
                //{
                //    battChargeRateWanted = bti.ChargeRateNeededHPercent;
                //}

                if (chargeLastWanted)
                {
                    int minBattChargeRateWhenNotCL = _Batt.RoundPercent(_Batt.TransferKiloWattsToPercent((Convert.ToDouble(generation) - 3400) / 1000));
                    if (battChargeRateWanted < minBattChargeRateWhenNotCL)
                    {
                        actionInfo.AppendLine($"Charge increased from {battChargeRateWanted}% to {minBattChargeRateWhenNotCL}% because that is what's left for charge last after export.");
                        battChargeRateWanted = minBattChargeRateWhenNotCL;
                    }
                }
            }

            // Apply any changes.
            StringBuilder actions = new StringBuilder();
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

            if (battChargeRateWanted > battChargeRate || battChargeRateWanted < battChargeRate - 2) // Don't be too spammy.
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
