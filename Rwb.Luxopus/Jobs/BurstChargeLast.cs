﻿using Microsoft.Extensions.Logging;
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

        public BurstChargeLast(
            ILogger<Burst> logger,
            IBurstLogService burstLog,
            ILuxopusPlanService plans,
            ILuxService lux,
            IInfluxQueryService influxQuery,
            IBatteryService batt)
            : base(logger)
        {
            _BurstLog = burstLog;
            _Plans = plans;
            _Lux = lux;
            _InfluxQuery = influxQuery;
            _Batt = batt;
        }

        protected override async Task WorkAsync(CancellationToken cancellationToken)
        {
            // Suggested cron: * 9-15 * * *

            DateTime t0 = DateTime.UtcNow;

            Plan? plan = _Plans.Load(t0);

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

            DateTime gStart = DateTime.Today.AddHours(9); //sunrise;
            DateTime gEnd = DateTime.Today.AddHours(16); // sunset
            (gStart, _) = (await _InfluxQuery.QueryAsync(Query.StartOfGeneration, currentPeriod.Start)).First().FirstOrDefault<double>();
            (gEnd, _) = (await _InfluxQuery.QueryAsync(Query.EndOfGeneration, currentPeriod.Start)).First().FirstOrDefault<double>();
            if (t0 < gStart || t0 > gEnd) { return; }

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
            int battChargeRateNeeded = battChargeRate;

            // Get the planned discharge settings -- we may override them.
            LuxAction dischargeToGridCurrent = _Lux.GetDischargeToGrid(settings);
            LuxAction dischargeToGridWanted = LuxAction.NextDisharge(plan, dischargeToGridCurrent);

            bool chargeLast = _Lux.GetChargeLast(settings);
            bool chargeLastWanted = chargeLast;

            string runtimeInfo = await _Lux.GetInverterRuntimeAsync();

            DateTime tBattChargeFrom = gStart > currentPeriod.Start ? gStart : currentPeriod.Start;

            int battLevelStart = await _InfluxQuery.GetBatteryLevelAsync(tBattChargeFrom);
            DateTime nextPlanCheck = DateTime.UtcNow.StartOfHalfHour().AddMinutes(30);

            (_, double prediction) = (await _InfluxQuery.QueryAsync(Query.PredictionToday, currentPeriod.Start)).First().FirstOrDefault<double>();
            prediction = prediction / 10;

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

            ScaleMethod sm = ScaleMethod.Linear;
            if (prediction > _Batt.CapacityPercentToKiloWattHours(200) && (generationRecentMean > 2500 || t0.Hour <= 9))
            {
                // High prediction / good day: charge slowly.
                sm = ScaleMethod.Slow;
            }
            else if (prediction < _Batt.CapacityPercentToKiloWattHours(90))
            {
                sm = ScaleMethod.Fast;
            }
            else if (generationRecentMean < 2000)
            {
                sm = ScaleMethod.Linear;
            }
            else if (prediction < _Batt.CapacityPercentToKiloWattHours(90))
            {
                sm = ScaleMethod.Fast;
            }

            using (JsonDocument j = JsonDocument.Parse(runtimeInfo))
            {
                JsonElement.ObjectEnumerator r = j.RootElement.EnumerateObject();
                int generation = r.Single(z => z.Name == "ppv").Value.GetInt32();
                int export = r.Single(z => z.Name == "pToGrid").Value.GetInt32();
                int inverterOutput = r.Single(z => z.Name == "pinv").Value.GetInt32();
                int battLevel = r.Single(z => z.Name == "soc").Value.GetInt32();
                int battCharge = r.Single(z => z.Name == "pCharge").Value.GetInt32();
                //int battDisharge = r.Single(z => z.Name == "pDisharge").Value.GetInt32();

                int battLevelEnd = 100;
                if (plan.Next.Buy <= 0)
                {
                    battLevelEnd -= _Batt.CapacityKiloWattHoursToPercent(plan.Plans.FutureFreeHoursBeforeNextDischarge(currentPeriod) * 3.2);
                    battLevelEnd = battLevelEnd < battLevel ? battLevel : battLevelEnd;
                }

                //int battLevelTarget = Scale.Apply(tBattChargeFrom, gEnd < plan.Next.Start ? gEnd : plan.Next.Start, nextPlanCheck, battLevelStart, 100, ScaleMethod.FastLinear);
                int battLevelTargetF = Scale.Apply(tBattChargeFrom, (gEnd < plan.Next.Start ? gEnd : plan.Next.Start).AddHours(generationMax > 3700 && DateTime.UtcNow < plan.Next.Start.AddHours(-2) ? 0 : -1), nextPlanCheck, battLevelStart, battLevelEnd, ScaleMethod.Fast);
                int battLevelTargetL = Scale.Apply(tBattChargeFrom, (gEnd < plan.Next.Start ? gEnd : plan.Next.Start).AddHours(generationMax > 3700 && DateTime.UtcNow < plan.Next.Start.AddHours(-2) ? 0 : -1), nextPlanCheck, battLevelStart, battLevelEnd, ScaleMethod.Linear);
                int battLevelTargetS = Scale.Apply(tBattChargeFrom, (gEnd < plan.Next.Start ? gEnd : plan.Next.Start).AddHours(generationMax > 3700 && DateTime.UtcNow < plan.Next.Start.AddHours(-2) ? 0 : -1), nextPlanCheck, battLevelStart, battLevelEnd, ScaleMethod.Slow);
                int battLevelTarget = Scale.Apply(tBattChargeFrom, (gEnd < plan.Next.Start ? gEnd : plan.Next.Start).AddHours(generationMax > 3700 && DateTime.UtcNow < plan.Next.Start.AddHours(-2) ? 0 : -1), nextPlanCheck, battLevelStart, battLevelEnd, sm);

                if (battLevel < battLevelTargetS && generationRecentMean < 1500)
                {
                    battLevelStart = battLevelTargetF;
                }

                actionInfo.AppendLine($"       Generation: {generation}W");
                actionInfo.AppendLine($"  Inverter output: {inverterOutput}W");
                actionInfo.AppendLine($"    Battery level: {battLevel}%");
                actionInfo.AppendLine($"   Battery target: {battLevelTarget}% ({battLevelTargetS}% < {battLevelTargetL}% < {battLevelTargetF}%))");
                actionInfo.AppendLine($"      Charge last: {(chargeLast ? "yes" : "no")}");
                actionInfo.AppendLine($"Discharge to grid: {dischargeToGridCurrent})");

                // Plan A
                double hoursToCharge = ((gEnd < plan.Next.Start ? gEnd : plan.Next.Start) - t0).TotalHours;
                double powerRequiredKwh = _Batt.CapacityPercentToKiloWattHours(battLevelEnd - battLevel);

                // Are we behind schedule?
                double extraPowerNeeded = 0.0;
                if (battLevel < battLevelTarget)
                {
                    extraPowerNeeded = _Batt.CapacityPercentToKiloWattHours(battLevelTarget - battLevel);
                    actionInfo.AppendLine($"Behind by {extraPowerNeeded:#,##0.0}kWh.");
                }
                else if (battLevelTarget < battLevel)
                {
                    double a = _Batt.CapacityPercentToKiloWattHours(battLevel - battLevelTarget);
                    actionInfo.AppendLine($"Ahead by {a:#,##0.0}kWh.");
                }

                double kW = (powerRequiredKwh + extraPowerNeeded) / hoursToCharge;
                battChargeRateNeeded = _Batt.RoundPercent(_Batt.CapacityKiloWattHoursToPercent(kW));

                if (DateTime.Now.Hour <= 9 && (sm == ScaleMethod.Slow || generationRecentMean > 800 || prediction >= 34) && battLevel >= battLevelTarget - 5)
                {
                    chargeLastWanted = true;
                    battChargeRateWanted = 90;
                    actionInfo.AppendLine($"Predicted to be a good day ({prediction:0.0}kWh) or high recent generation ({generationRecentMean}kW) therefore charge last before 9am.");
                }
                else if (generation > 3200)
                {
                    // Forced discharge causes clipping.

                    // So does charge from grid.
                    LuxAction chargeFromGridCurrent = _Lux.GetChargeFromGrid(settings);
                    LuxAction chargeFromGridWanted = LuxAction.NextCharge(plan, chargeFromGridCurrent);
                    if (chargeFromGridWanted != null && chargeFromGridCurrent.Enable && chargeFromGridCurrent.Start < DateTime.UtcNow && chargeFromGridCurrent.End > DateTime.UtcNow)
                    {
                        // Could be plan or because of zero or negative price; it's not important why. We just want to prevent clipping.
                        chargeFromGridWanted.Enable = false;
                        // TODO: revert to plan.
                        bool changedCharge = await _Lux.SetChargeFromGrid(chargeFromGridCurrent, chargeFromGridWanted);
                        if (changedCharge)
                        {
                            actionInfo.AppendLine($"Charge from grid enabled: true -> false.");
                            actions.AppendLine($"Charge from grid was: {chargeFromGridCurrent}");
                            actions.AppendLine($"Charge from grid is : {chargeFromGridWanted}");
                        }
                    }

                    // Generation probably not limited therefore send less to battery.
                    if (battLevel >= battLevelTarget)
                    {
                        battChargeRateWanted = 91;
                        chargeLastWanted = true;
                        actionInfo.AppendLine($"Charge last enabled because ahead of target.");
                    }
                    else if (battLevel < battLevelTarget)
                    {
                        chargeLastWanted = false;
                        battChargeRateWanted = battChargeRateNeeded;

                        // Increase the batt charge rate to avoid clipping.
                        int minToBatt = _Batt.TransferKiloWattsToPercent((Convert.ToDouble(generation) - 3000.0) / 1000.0);
                        if (battChargeRateWanted < minToBatt)
                        {
                            actionInfo.AppendLine($"Charge last disabled because behind target; required charge rate is {battChargeRateNeeded}% overidden to {minToBatt}% because generation {generation}kW.");
                            battChargeRateWanted = _Batt.RoundPercent(minToBatt);
                        }
                        else
                        {
                            actionInfo.AppendLine($"Charge last disabled because behind target; required charge rate is {battChargeRateNeeded}% which is below generation of {generation}kW.");
                        }
                    }
                }
                else
                {
                    // Low generation.
                    if (t0.Hour <= 9 /* up to 11AM BST && sm == ScaleMethod.Slow */ && generationMax > 1000 && battLevel > battLevelTarget - 8)
                    {
                        // It's early and it looks like it's going to be a good day.
                        // So keep the battery empty to make space for later.
                        battChargeRateWanted = 91;
                        chargeLastWanted = true;
                        if (battLevel > battLevelTarget - 5)
                        {
                            dischargeToGridWanted = new LuxAction()
                            {
                                Enable = true,
                                Start = currentPeriod.Start,
                                End = dischargeToGridCurrent.End >= plan.Next.Start ? dischargeToGridCurrent.End : plan.Next.Start,
                                Limit = battLevelTarget - 5,
                                Rate = 91
                            };
                        }
                        actionInfo.AppendLine($"Looks like it could be a good day. Battery level {battLevel}%, target of {battLevelTarget}% ({battLevelTargetS}% < {battLevelTargetL}% < {battLevelTargetF}%) therefore keep some space.");
                    }
                    else if (generationMax > 4000 && generationRecentMax > 3000 && generation /* inverterOutput includes batt discharge */ < 3100 && battLevel > battLevelTarget + 2)
                    {
                        // It's gone quiet but it might get busy again: try to discharge some over-charge.
                        dischargeToGridWanted = new LuxAction()
                        {
                            Enable = true,
                            Start = currentPeriod.Start,
                            End = dischargeToGridCurrent.End >= plan.Next.Start ? dischargeToGridCurrent.End : plan.Next.Start,
                            Limit = battLevelTarget - 2,
                            Rate = 91
                        };
                        battChargeRateWanted = 91;
                        chargeLastWanted = true;
                        actionInfo.AppendLine($"Generation peak of {generationMax} recent {generationRecentMax} but currently {generation}. Battery level {battLevel}%, target of {battLevelTarget}% therefore take opportunity to discharge.");
                    }
                    else
                    {
                        chargeLastWanted = false;
                    }

                    if (plan.Current.Buy <= 0)
                    {
                        // Fill your boots.
                        LuxAction chargeFromGridCurrent = _Lux.GetChargeFromGrid(settings);
                        LuxAction chargeFromGridWanted = new LuxAction()
                        {
                            Enable = true,
                            Start = plan.Current.Start,
                            End = plan.Next.Start,
                            Limit = 100,
                            Rate = 100
                        };

                        bool changedCharge = await _Lux.SetChargeFromGrid(chargeFromGridCurrent, chargeFromGridWanted);
                        if (changedCharge)
                        {
                            actionInfo.AppendLine($"Charge from grid because buy price is {plan.Current.Buy:#,##0.000}.");
                            actions.AppendLine($"Charge from grid was: {chargeFromGridCurrent}");
                            actions.AppendLine($"Charge from grid is : {chargeFromGridWanted}");
                        }
                    }
                }

                if (battChargeRateWanted < battChargeRate && battLevel < battLevelTarget)
                {
                    actionInfo.AppendLine($"{kW:0.0}kWh needed to get from {battLevel}% (should be {battLevelTarget}% ({battLevelTargetS}% < {battLevelTargetL}% < {battLevelTargetF}%)) to {100}% in {hoursToCharge:0.0} hours until {gEnd:HH:mm} (mean rate {kW:0.0}kW -> {battChargeRateWanted}%). But current setting is {battChargeRate}% therefore not changed.");
                    battChargeRateWanted = battChargeRate;
                }
            }

            // Apply any changes.
            if (chargeLast != chargeLastWanted)
            {
                await _Lux.SetChargeLastAsync(chargeLastWanted);
                actions.AppendLine($"SetChargeLastAsync({chargeLastWanted}) was {chargeLast}.");
            }

            if (dischargeToGridWanted != null)
            {
                bool changedDischarge = await _Lux.SetDischargeToGrid(dischargeToGridCurrent, dischargeToGridWanted);
                if (changedDischarge)
                {
                    actions.AppendLine($"Discharge to grid was: {dischargeToGridCurrent}");
                    actions.AppendLine($" Discharge to grid is: {dischargeToGridWanted}");
                }
            }

            if (battChargeRateWanted < battChargeRateNeeded)
            {
                battChargeRateWanted = battChargeRateNeeded;
                actionInfo.AppendLine($"Battery charge rate wanted {battChargeRateWanted} increased to {battChargeRateNeeded}% needed.");
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
