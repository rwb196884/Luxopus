﻿using Microsoft.Extensions.Logging;
using Rwb.Luxopus.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Jobs
{

    /// <summary>
    /// <para>
    /// Check that plans are running. Simple version: look only at the current period.
    /// </para>
    /// </summary>
    public class PlanChecker : Job
    {
        private readonly ILuxopusPlanService _Plans;
        private readonly ILuxService _Lux;
        private readonly IInfluxQueryService _InfluxQuery;
        private readonly IEmailService _Email;
        private readonly IBatteryService _Batt;
        private readonly IBurstLogService _BurstLog;

        public PlanChecker(
            ILogger<LuxMonitor> logger,
            ILuxopusPlanService plans,
            ILuxService lux,
            IInfluxQueryService influxQuery,
            IEmailService email,
            IBatteryService batt,
            IBurstLogService burstLog
            )
            : base(logger)
        {
            _Plans = plans;
            _Lux = lux;
            _InfluxQuery = influxQuery;
            _Email = email;
            _Batt = batt;
            _BurstLog = burstLog;
        }

        //private const int _MedianHousePowerWatts = 240;

        //protected int PercentRequiredFromUntil(DateTime from, DateTime until)
        //{
        //    decimal hours = Convert.ToDecimal(until.Subtract(from).TotalHours);
        //    decimal percentPerHour = _Batt.PercentForAnHour(_MedianHousePowerWatts);
        //    return Convert.ToInt32(Math.Ceiling(hours * percentPerHour));
        //}

        protected override async Task WorkAsync(CancellationToken cancellationToken)
        {
            //DateTime t0 = new DateTime(2023, 05, 27, 03, 01, 00);
            DateTime t0 = DateTime.UtcNow;

            Plan? plan = _Plans.Load(t0);

            if (plan == null)
            {
                plan = _Plans.Load(t0.AddDays(-2));
                if (plan != null)
                {
                    Logger.LogWarning($"No plan at UTC {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")}. Using plan from {plan.Current.Start.ToString("yyyy-MM-dd HH:mm")}.");
                    foreach (HalfHourPlan p in plan.Plans)
                    {
                        p.Start = p.Start.AddDays(2);
                    }
                }
            }

            if (plan == null)
            {
                Logger.LogError($"No plan at UTC {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")}.");
                // If there is plan then default configuration will be set.
            }

            HalfHourPlan? currentPeriod = plan?.Current;

            if (currentPeriod == null || currentPeriod.Start < DateTime.Now.AddDays(-7))
            {
                Logger.LogError($"No current plan at UTC {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")}.");
                currentPeriod = new HalfHourPlan()
                {
                    Action = new PeriodAction() // Use the default values.
                };
            }

            StringBuilder actions = new StringBuilder();

            // Check that it's doing what it's supposed to be doing.
            // update settings and log warning in case of discrepancy.

            // Are we on target?
            // If not then what can we do about it?

            Dictionary<string, string> settings = await _Lux.GetSettingsAsync();
            while (settings.Any(z => z.Value == "DATAFRAME_TIMEOUT"))
            {
                settings = await _Lux.GetSettingsAsync();
            }
            if (settings.Any(z => z.Value == "DEVICE_OFFLINE")) { return; }
            int battChargeRate = _Lux.GetBatteryChargeRate(settings);
            bool chargeLast = _Lux.GetChargeLast(settings);
            bool chargeLastWanted = chargeLast;
            int battChargeRateWanted = battChargeRate; // No change.

            // Discharge to grid -- according to plan.
            (bool outEnabled, DateTime outStart, DateTime outStop, int outBatteryLimitPercent, int battDischargeToGridRate) = _Lux.GetDischargeToGrid(settings);
            bool outEnabledWanted = outEnabled;
            DateTime outStartWanted = outStart;
            DateTime outStopWanted = outStop;
            int outBatteryLimitPercentWanted = outBatteryLimitPercent;
            int battDischargeToGridRateWanted = battDischargeToGridRate;

            outEnabledWanted = plan.Plans.Any(z => Plan.DischargeToGridCondition(z));
            if (plan != null && outEnabledWanted)
            {
                HalfHourPlan runFirst = plan.Plans.OrderBy(z => z.Start).First(z => Plan.DischargeToGridCondition(z));
                outStartWanted = runFirst.Start;
                outBatteryLimitPercentWanted = runFirst.Action!.DischargeToGrid;

                (IEnumerable<HalfHourPlan> run, HalfHourPlan? next) = plan.GetNextRun(runFirst, Plan.DischargeToGridCondition);
                outStopWanted = next?.Start ?? run.Last().Start.AddMinutes(30);

                // If there's more than one run in plansToCheck then there must be a gap,
                // so in the first period in that gap the plan checker will set up for the next run.

                // If we're discharging now and started already then no change is needed.
                if (Plan.DischargeToGridCondition(currentPeriod) && outStart <= currentPeriod.Start && outStartWanted <= currentPeriod.Start)
                {
                    // No need to change it.
                    outStartWanted = outStart;
                }
            }

            if (outEnabledWanted && Plan.DischargeToGridCondition(currentPeriod))
            {
                try
                {
                    (DateTime lastOccupied, bool wasOccupied) = (await _InfluxQuery.QueryAsync(Query.LastOccupied, DateTime.UtcNow)).Single().FirstOrDefault<bool>();
                    if (wasOccupied && lastOccupied < DateTime.Now.AddHours(-3) && outBatteryLimitPercentWanted > 7)
                    {
                        actions.AppendLine($"DischargeToGridLevel overridden from plan of {outBatteryLimitPercentWanted}% to 7% because house not occupied since {lastOccupied.ToString("yyyy-MM-dd HH:mm")}.");
                        outBatteryLimitPercentWanted = 7;
                    }
                }
                catch (InvalidOperationException e)
                {
                    actions.AppendLine($"DischargeToGridLevel overridden from plan of {outBatteryLimitPercentWanted}% to 7% because house not occupied (query failed: ${e.Message}).");
                    outBatteryLimitPercentWanted = 7;
                }
                catch (Exception e)
                {
                    actions.AppendLine($"DischargeToGridLevel not overridden because house not occupied query failed: ${e.Message}.");
                }

                //goto Apply;
            }

            // Charge from grid -- according to plan.
            (bool inEnabled, DateTime inStart, DateTime inStop, int inBatteryLimitPercent, int battChargeFromGridRate) = _Lux.GetChargeFromGrid(settings);
            bool inEnabledWanted = inEnabled;
            DateTime inStartWanted = inStart;
            DateTime inStopWanted = inStop;
            int inBatteryLimitPercentWanted = inBatteryLimitPercent;
            int battChargeFromGridRateWanted = battChargeFromGridRate;

            inEnabledWanted = plan.Plans.Any(z => Plan.ChargeFromGridCondition(z));
            if (plan != null && inEnabledWanted)
            {
                HalfHourPlan runFirst = plan.Plans.OrderBy(z => z.Start).First(z => Plan.ChargeFromGridCondition(z));
                inStartWanted = runFirst.Start;
                inBatteryLimitPercentWanted = runFirst.Action!.ChargeFromGrid;

                (IEnumerable<HalfHourPlan> run, HalfHourPlan? next) = plan.GetNextRun(runFirst, Plan.ChargeFromGridCondition);
                inStopWanted = (next?.Start ?? run.Last().Start.AddMinutes(30));

                // If we're charging now and started already then no change is needed.
                if (Plan.ChargeFromGridCondition(currentPeriod) && inStart <= currentPeriod.Start && inStartWanted <= currentPeriod.Start)
                {
                    // No need to change it.
                    inStartWanted = inStart;
                }
            }

            int battLevel = await _InfluxQuery.GetBatteryLevelAsync(DateTime.UtcNow);
            int battLevelEnd = 100;
            if ((plan.Next?.Buy ?? 1) <= 0)
            {
                battLevelEnd -= _Batt.CapacityKiloWattHoursToPercent(plan.Plans.FutureFreeHoursBeforeNextDischarge(currentPeriod) * 3.2);
                battLevelEnd = battLevelEnd < battLevel ? battLevel : battLevelEnd;
            }

            // Batt charge. ///////////////////////////////////////////////////
            // DateTime sunrise = DateTime.Today.AddHours(9); // TODO: Move to configuration.
            //DateTime sunset = DateTime.Today.AddHours(16);
            DateTime gStart = DateTime.Today.AddHours(9); //sunrise;
            DateTime gEnd = DateTime.Today.AddHours(16); // sunset
            try
            {
                //(sunrise, _) = (await _InfluxQuery.QueryAsync(Query.Sunrise, currentPeriod.Start)).First().FirstOrDefault<long>();
                //(sunset, _) = (await _InfluxQuery.QueryAsync(Query.Sunset, currentPeriod.Start)).First().FirstOrDefault<long>();
                (gStart, _) = (await _InfluxQuery.QueryAsync(Query.StartOfGeneration, currentPeriod.Start)).First().FirstOrDefault<double>();
                (gEnd, _) = (await _InfluxQuery.QueryAsync(Query.EndOfGeneration, currentPeriod.Start)).First().FirstOrDefault<double>();
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Failed to query for sunrise and sunset / generation.");
            }
            string why = "no change";

            DateTime tNext = plan.Next?.Start ?? DateTime.UtcNow.AddHours(1);
            if (Plan.ChargeFromGridCondition(currentPeriod))
            {
                // Planned charge.
                inEnabledWanted = true;
                if (inStart > currentPeriod.Start) { inStartWanted = currentPeriod.Start; }
                if (inStop < tNext) { inStartWanted = tNext; }
                if (inBatteryLimitPercent != currentPeriod.Action.ChargeFromGrid) { inBatteryLimitPercentWanted = currentPeriod.Action.ChargeFromGrid; }
                if (battLevel < currentPeriod.Action.ChargeFromGrid)
                {
                    double powerRequiredKwh = _Batt.CapacityPercentToKiloWattHours(currentPeriod.Action.ChargeFromGrid - battLevel);
                    double hoursToCharge = (tNext - t0).TotalHours;
                    double kW = powerRequiredKwh / hoursToCharge;
                    battChargeFromGridRateWanted = _Batt.RoundPercent(_Batt.TransferKiloWattsToPercent(kW));
                    battChargeRateWanted = battChargeFromGridRateWanted > battChargeRateWanted ? battChargeFromGridRateWanted : battChargeRateWanted;
                    chargeLastWanted = false;
                    why = $"{powerRequiredKwh:0.0}kWh needed from grid to get from {battLevel}% to {currentPeriod.Action.ChargeFromGrid}% in {hoursToCharge:0.0} hours until {tNext:HH:mm} (mean rate {kW:0.0}kW -> {battChargeFromGridRateWanted}%).";
                }
            }
            else if (Plan.DischargeToGridCondition(currentPeriod))
            {
                // Planned discharge.
                outEnabledWanted = true;
                if (outStart > currentPeriod.Start) { outStartWanted = currentPeriod.Start; }
                if (outStop < tNext) { outStopWanted = tNext; }
                if (outBatteryLimitPercent != currentPeriod.Action.DischargeToGrid) { outBatteryLimitPercentWanted = currentPeriod.Action.DischargeToGrid; }
                if (battLevel > currentPeriod.Action.DischargeToGrid)
                {
                    double powerRequiredKwh = _Batt.CapacityPercentToKiloWattHours(battLevel - currentPeriod.Action!.DischargeToGrid);
                    double hoursToCharge = (tNext - t0).TotalHours;
                    double kW = powerRequiredKwh / hoursToCharge;
                    battDischargeToGridRateWanted = _Batt.RoundPercent(_Batt.TransferKiloWattsToPercent(kW));
                    battChargeRateWanted = 100; // Use FORCE_CHARGE_LAST
                    chargeLastWanted = true;
                    why = $"Discharge to grid: {powerRequiredKwh:0.0}kWh needed to grid to get from {battLevel}% to {currentPeriod.Action.DischargeToGrid}% in {hoursToCharge:0.0} hours until {tNext:HH:mm} (mean rate {kW:0.0}kW -> {battDischargeToGridRateWanted}%).";
                }
            }
            else
            {
                battChargeFromGridRateWanted = 71;
                if (t0.TimeOfDay <= gStart.TimeOfDay || t0.TimeOfDay >= gEnd.TimeOfDay)
                {
                    // No solar generation.
                    if (battChargeRateWanted < 80)
                    {
                        battChargeRateWanted = 50;
                    }
                    chargeLastWanted = false;
                    why = $"Default (time {t0:HH:mm} is outside of generation range {gStart:HH:mm} to {gEnd:HH:mm}).";
                }
                else
                {
                    // Throttling and discharge of over-generation is managed by the burst job.
                    // Just set the main strategy.
                    if (battLevel >= 100 - 2 /* It will still get about 60W. */)
                    {
                        // Battery is full.
                        (DateTime _, long generationMaxLastHour) = (await _InfluxQuery.QueryAsync(@$"
            from(bucket: ""solar"")
              |> range(start: -1h, stop: now())
              |> filter(fn: (r) => r[""_measurement""] == ""inverter"" and r[""_field""] == ""generation"")
              |> max()")).First().FirstOrDefault<long>();

                        if (generationMaxLastHour < 3600)
                        {
                            battChargeRateWanted = 100;
                            chargeLastWanted = true;
                            why = $"Battery is full ({battLevel}%) and max generation in last hour is {generationMaxLastHour}.";
                        }
                        else
                        {
                            // Set charge rate high and enable discharge to grid to absorb generation peaks then discharge them.
                            // Can cause generation to be limited, but since the battery is full this is the case anyway.
                            battChargeRateWanted = 72;
                            outEnabledWanted = true;
                            battDischargeToGridRateWanted = 72;
                            outBatteryLimitPercentWanted = 97;
                            outStartWanted = currentPeriod.Start; // Needs to be constant in order not to spam changes.
                            outStopWanted = plan?.Next?.Start ?? currentPeriod.Start.AddMinutes(30);
                            chargeLastWanted = true;
                            why = $"Battery is full ({battLevel}%) and max generation in last hour is {generationMaxLastHour}.";
                        }
                    }
                    else if (plan?.Next != null && Plan.DischargeToGridCondition(plan!.Next!))
                    {
                        (DateTime _, long generationMax) = //(DateTime.Now, 0);
                            (await _InfluxQuery.QueryAsync(@$"
from(bucket: ""solar"")
  |> range(start: {currentPeriod.Start.ToString("yyyy-MM-ddTHH:mm:00Z")}, stop: now())
  |> filter(fn: (r) => r[""_measurement""] == ""inverter"" and r[""_field""] == ""generation"")
  |> max()")).First().FirstOrDefault<long>();

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

                        double generationMeanDifference = (await _InfluxQuery.QueryAsync(@$"
from(bucket: ""solar"")
  |> range(start: -45m, stop: now())
  |> filter(fn: (r) => r[""_measurement""] == ""inverter"" and r[""_field""] == ""generation"")
  |> difference()
  |> mean()")
                           ).First().Records.First().GetValue<double>();

                        // Get fully charged before the discharge period.
                        DateTime tBattChargeFrom = gStart > currentPeriod.Start ? gStart : currentPeriod.Start;

                        int battLevelStart = await _InfluxQuery.GetBatteryLevelAsync(currentPeriod.Start);
                        DateTime nextPlanCheck = DateTime.UtcNow.Minute > 30  //Check if mins are greater than 30
                           ? DateTime.UtcNow.AddHours(1).AddMinutes(-DateTime.UtcNow.Minute) // After half past so go to the next hour.
                           : DateTime.UtcNow.AddMinutes(30 - DateTime.UtcNow.Minute); // Before half past so go to half past.

                        (_, double prediction) = (await _InfluxQuery.QueryAsync(Query.PredictionToday, currentPeriod.Start)).First().FirstOrDefault<double>();
                        prediction = prediction / 10;
                        int battLevelTargetF = Scale.Apply(tBattChargeFrom, (gEnd < plan.Next.Start ? gEnd : plan.Next.Start).AddHours(generationMax > 3700 && DateTime.UtcNow < plan.Next.Start.AddHours(-2) ? 0 : -1), nextPlanCheck, battLevelStart, battLevelEnd, ScaleMethod.Fast);
                        int battLevelTargetL = Scale.Apply(tBattChargeFrom, (gEnd < plan.Next.Start ? gEnd : plan.Next.Start).AddHours(generationMax > 3700 && DateTime.UtcNow < plan.Next.Start.AddHours(-2) ? 0 : -1), nextPlanCheck, battLevelStart, battLevelEnd, ScaleMethod.Linear);
                        int battLevelTargetS = Scale.Apply(tBattChargeFrom, (gEnd < plan.Next.Start ? gEnd : plan.Next.Start).AddHours(generationMax > 3700 && DateTime.UtcNow < plan.Next.Start.AddHours(-2) ? 0 : -1), nextPlanCheck, battLevelStart, battLevelEnd, ScaleMethod.Slow);

                        ScaleMethod sm = ScaleMethod.Linear;
                        if (battLevel < battLevelTargetS && generationRecentMean < 1500)
                        {
                            sm = ScaleMethod.Fast;
                        }
                        else if (prediction < _Batt.CapacityPercentToKiloWattHours(90))
                        {
                            sm = ScaleMethod.Fast;
                        }
                        else if (generationRecentMean < 2000)
                        {
                            sm = ScaleMethod.Linear;
                        }
                        else if (prediction > _Batt.CapacityPercentToKiloWattHours(200) && generationRecentMean > 2500)
                        {
                            // High prediction / good day: charge slowly.
                            sm = ScaleMethod.Slow;
                        }

                        int battLevelTarget = Scale.Apply(tBattChargeFrom, (gEnd < plan.Next.Start ? gEnd : plan.Next.Start).AddHours(generationMax > 3700 && DateTime.UtcNow < plan.Next.Start.AddHours(-2) ? 0 : -1), nextPlanCheck, battLevelStart, battLevelEnd, sm);

                        if( DateTime.Now.Hour < 9 && prediction > 20)
                        {
                            chargeLastWanted = true;
                            battChargeRateWanted = 90;
                            why = $"Predicted to be a good day (generation prediction {prediction:#0.0}kW therefore charge last before 9am..";
                        }
                        else if (DateTime.Now.Hour <= 9 && (sm == ScaleMethod.Slow || generationRecentMean > 800) && battLevel >= 9)
                        {
                            chargeLastWanted = true;
                            battChargeRateWanted = 90;

                            if (battLevel > battLevelTarget - 5 && generationRecentMean < 3000)
                            {
                                outEnabledWanted = true;
                                outStartWanted = currentPeriod.Start;
                                outStopWanted = plan.Next?.Start ?? DateTime.UtcNow.AddHours(12);
                                outBatteryLimitPercentWanted = battLevelTarget - 5;
                                battDischargeToGridRateWanted = 91;
                                why = $"Predicted to be a good day (generation prediction {prediction:#0.0}kW, recent mean {generationRecentMean / 1000:#0.0}kW) therefore charge last before 10am. Discharge to grid because battery level {battLevel}% is ahead of target target of {battLevelTarget} ({battLevelTargetS}% < {battLevelTargetL}% < {battLevelTargetF}%).";
                            }
                            else
                            {
                                why = $"Predicted to be a good day (generation prediction {prediction:#0.0}kW, generation mean {generationRecentMean / 1000:#0.0}kW) therefore charge last before 10am. Battery level {battLevel}% target of {battLevelTarget} ({battLevelTargetS}% < {battLevelTargetL}% < {battLevelTargetF}%).";
                            }
                            goto Apply;
                        }
                        else if (t0.Hour <= 9 /* up to 11AM BST */ && sm == ScaleMethod.Slow && generationMax > 2000 && battLevel > battLevelTarget - 13)
                        {
                            // At 9am median generation is 1500.
                            battChargeRateWanted = 90;
                            chargeLastWanted = true;
                            if (battLevel > battLevelTarget - 5)
                            {
                                outEnabledWanted = true;
                                outStartWanted = currentPeriod.Start;
                                outStopWanted = plan.Next?.Start ?? DateTime.UtcNow.AddHours(12);
                                outBatteryLimitPercentWanted = battLevelTarget - 5;
                                battDischargeToGridRateWanted = 91;
                                why = $"Predicted to be a good day (generation prediction {prediction:#0.0}kW, max {generationMax / 1000:#0.0}kW) therefore charge last before 9am. Discharge to grid because battery level {battLevel}% is ahead of target target of {battLevelTarget} ({battLevelTargetS}% < {battLevelTargetL}% < {battLevelTargetF}%).";
                            }
                            else
                            {
                                battDischargeToGridRateWanted = 71;
                                why = $"Predicted to be a good day (generation prediction {prediction:#0.0}kW, max {generationMax / 1000:#0.0}kW) therefore charge last before 9am. Battery level {battLevel}% target of {battLevelTarget} ({battLevelTargetS}% < {battLevelTargetL}% < {battLevelTargetF}%).";
                            }
                            goto Apply;
                        }

                        DateTime endOfCharge = gEnd < plan.Next.Start ? gEnd : plan.Next.Start;
                        double hoursToCharge = (endOfCharge - t0).TotalHours;
                        double powerRequiredKwh = _Batt.CapacityPercentToKiloWattHours(battLevelEnd - battLevel);
                        string s = battLevelTarget != battLevel ? $" (prediction {prediction:0.0}kWh so battery level should be {battLevelTarget}% ({battLevelTargetS}% < {battLevelTargetL}% < {battLevelTargetF}%))" : "";

                        // Are we behind schedule?
                        double extraPowerNeeded = 0.0;
                        if (battLevel < battLevelTarget)
                        {
                            extraPowerNeeded = _Batt.CapacityPercentToKiloWattHours(battLevelTarget - battLevel);
                            chargeLastWanted = false;
                            double kW = (powerRequiredKwh + extraPowerNeeded) / hoursToCharge;
                            int b = _Batt.TransferKiloWattsToPercent(kW);
                            battChargeRateWanted = _Batt.RoundPercent(b);
                            why = $"{powerRequiredKwh:0.0}kWh needed to get from {battLevel}%{s} (should be {battLevelTarget}%) to {battLevelEnd}% in {hoursToCharge:0.0} hours until {endOfCharge:HH:mm} (mean rate {kW:0.0}kW -> {battChargeRateWanted}%).";
                        }
                        else
                        {
                            chargeLastWanted = true;
                            battChargeRateWanted = 90;
                            why = $"{powerRequiredKwh:0.0}kWh needed to get from {battLevel}%{s} (should be {battLevelTarget}%) to {battLevelEnd}% in {hoursToCharge:0.0} hours until {endOfCharge:HH:mm} but ahead of target therefore charge last.";
                        }

                        // Set the rate.

                        if (generationRecentMax < 3000 && extraPowerNeeded > 0)
                        {
                            battChargeRateWanted = 90;
                            chargeLastWanted = false;
                            why += $" But we are behind by {extraPowerNeeded:0.0}kW therefore override to 90%.";
                        }

                        if ((powerRequiredKwh + extraPowerNeeded * 1.5 /* caution factor */ ) / hoursToCharge > generationRecentMean)
                        {
                            battChargeRateWanted = 90;
                            chargeLastWanted = false;
                            why += $" Need {(powerRequiredKwh + extraPowerNeeded):0.0}kWh in {hoursToCharge:0.0} hours but recent generation is {generationRecentMean / 1000:0.0}kW therefore override to 90%.";
                        }

                        if (generationRecentMax < 3600 && battLevel < battLevelTarget + 5 && generationMeanDifference < 0)
                        {
                            battChargeRateWanted = battChargeRateWanted > 40 ? 90 : battChargeRateWanted * 2;
                            chargeLastWanted = false;
                            why += $" Rate of generation is decreasing ({generationMeanDifference:0}W) therefore override to {battChargeRateWanted}%.";
                        }
                    }
                    else
                    {
                        // No plan. Set defaults.
                        battChargeRateWanted = 71;
                        chargeLastWanted = false;
                        why = $"No information.";
                    }
                }
            }
        // A P P L Y   S E T T I N G S
        Apply:

            // Charge from solar.
            if (battChargeRateWanted != battChargeRate)
            {
                await _Lux.SetBatteryChargeRateAsync(battChargeRateWanted);
                actions.AppendLine($"SetBatteryChargeRate({battChargeRateWanted}) was {battChargeRate}.");
            }

            if (battChargeFromGridRateWanted != battChargeFromGridRate)
            {
                await _Lux.SetBatteryChargeFromGridRateAsync(battChargeFromGridRateWanted);
                actions.AppendLine($"SetBatteryChargeFromGridRate({battChargeFromGridRateWanted}) was {battChargeFromGridRate}.");
            }

            if (battDischargeToGridRateWanted != battDischargeToGridRate)
            {
                await _Lux.SetBatteryDischargeToGridRateAsync(battDischargeToGridRateWanted);
                actions.AppendLine($"SetBatteryDischargeToGridRate({battDischargeToGridRateWanted}) was {battDischargeToGridRate}.");
            }

            // Charge last.
            if (chargeLast != chargeLastWanted)
            {
                await _Lux.SetChargeLastAsync(chargeLastWanted);
                actions.AppendLine($"SetChargeLastAsync({chargeLastWanted}) was {chargeLast}.");
            }

            // Charge from grid.
            if (inEnabledWanted)
            {
                if (inStart.TimeOfDay != inStartWanted.TimeOfDay)
                {
                    await _Lux.SetChargeFromGridStartAsync(inStartWanted);
                    actions.AppendLine($"SetChargeFromGridStartAsync({inStartWanted.ToString("dd MMM HH:mm")}) was {inStart.ToString("dd MMM HH:mm")}.");
                }

                if (inStop.TimeOfDay != inStopWanted.TimeOfDay)
                {
                    await _Lux.SetChargeFromGridStopAsync(inStopWanted);
                    actions.AppendLine($"SetChargeFromGridStopAsync({inStopWanted.ToString("dd MMM HH:mm")}) was {inStop.ToString("dd MMM HH:mm")}.");
                }

                if (!inEnabled || (inBatteryLimitPercentWanted > 0 && inBatteryLimitPercent != inBatteryLimitPercentWanted))
                {
                    await _Lux.SetChargeFromGridLevelAsync(inBatteryLimitPercentWanted);
                    actions.AppendLine($"SetChargeFromGridLevelAsync({inBatteryLimitPercentWanted}) was {inBatteryLimitPercent} (enabled: {inEnabledWanted} was {inEnabled}).");
                }
            }
            else
            {
                if (inEnabled)
                {
                    await _Lux.SetChargeFromGridLevelAsync(0);
                    actions.AppendLine($"SetChargeFromGridLevelAsync(0) to disable was {inBatteryLimitPercent} (enabled: {inEnabled}).");
                }
            }

            // Discharge to grid.
            if (outEnabledWanted)
            {
                // Lux serivce returns the first time in the future that the out will start.
                // But the start of the current plan period may be in the past.
                if (outStart.TimeOfDay != outStartWanted.TimeOfDay)
                {
                    await _Lux.SetDischargeToGridStartAsync(outStartWanted);
                    actions.AppendLine($"SetDischargeToGridStartAsync({outStartWanted.ToString("dd MMM HH:mm")}) was {outStart.ToString("dd MMM HH:mm")}.");
                }

                if (outStop.TimeOfDay != outStopWanted.TimeOfDay && outStopWanted <= plan.Next.Start && Plan.DischargeToGridCondition(plan.Next))
                {
                    if (outStop > plan.Next.Start)
                    {
                        outStopWanted = outStop; // It's already been set correctly.
                    }
                    else
                    {
                        outStopWanted = plan.Next.Start.AddMinutes(30);
                    }
                }
                if (outStop.TimeOfDay != outStopWanted.TimeOfDay)
                {
                    await _Lux.SetDischargeToGridStopAsync(outStopWanted);
                    actions.AppendLine($"SetDischargeToGridStopAsync({outStopWanted.ToString("dd MMM HH:mm")}) was {outStop.ToString("dd MMM HH:mm")}.");
                }

                if (!outEnabled || (outBatteryLimitPercentWanted < 100 && outBatteryLimitPercent != outBatteryLimitPercentWanted))
                {
                    await _Lux.SetDischargeToGridLevelAsync(outBatteryLimitPercentWanted);
                    actions.AppendLine($"SetDischargeToGridLevelAsync({outBatteryLimitPercentWanted}) was {outBatteryLimitPercent} (enabled: {outEnabledWanted} was {outEnabled}).");
                }
            }
            else
            {
                await _Lux.SetDischargeToGridLevelAsync(100);
                actions.AppendLine($"SetDischargeToGridLevelAsync(100) to disable was {outStart:HH:mm}) to {outStop:HH:mm} target {outBatteryLimitPercent}%.");
            }

            string burstLog = _BurstLog.Read();
            _BurstLog.Clear();

            // Report any changes.
            if (actions.Length > 0 || burstLog.Length > 0)
            {
                if (why != null)
                {
                    actions.AppendLine(why);
                }

                actions.AppendLine();
                actions.AppendLine($"  Battery: {battLevel}%");
                actions.AppendLine($"   Charge: {inStartWanted:HH:mm} to {inStopWanted:HH:mm} limit {inBatteryLimitPercentWanted} rate {battChargeFromGridRateWanted}");
                actions.AppendLine($"Discharge: {outStartWanted:HH:mm} to {outStopWanted:HH:mm} limit {outBatteryLimitPercentWanted} rate {battDischargeToGridRateWanted}");
                if (plan != null)
                {
                    actions.AppendLine();
                    HalfHourPlan? pp = currentPeriod;
                    while (pp != null)
                    {
                        actions.AppendLine(pp.ToString());
                        pp = plan.Plans.GetNext(pp);
                    }
                }

                if (burstLog.Length > 0)
                {
                    actions.AppendLine();
                    actions.AppendLine("Burst log");
                    actions.AppendLine(burstLog);
                }

                _Email.SendEmail($"PlanChecker at UTC {DateTime.UtcNow.ToString("dd MMM HH:mm")}", actions.ToString());
                Logger.LogInformation("PlanChecker made changes: " + Environment.NewLine + actions.ToString());
            }
        }
    }
}
