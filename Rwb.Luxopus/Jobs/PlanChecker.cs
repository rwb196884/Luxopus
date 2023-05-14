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

        public PlanChecker(ILogger<LuxMonitor> logger, ILuxopusPlanService plans, ILuxService lux, IInfluxQueryService influxQuery, IEmailService email, IBatteryService batt) 
            : base(logger)
        {
            _Plans = plans;
            _Lux = lux;
            _InfluxQuery = influxQuery;
            _Email = email;
            _Batt = batt;
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
            //DateTime t0 = new DateTime(2023, 03, 31, 18, 00, 00);
            DateTime t0 = DateTime.UtcNow;
            IEnumerable<Plan> ps = _Plans.LoadAll(t0);

            Plan? plan = _Plans.Load(DateTime.UtcNow);
            if (plan == null)
            {
                Logger.LogError($"No plan at UTC {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")}.");
                // If there is plan then default configuration will be set.
            }

            HalfHourPlan? currentPeriod = plan?.Current;

            if (currentPeriod == null)
            {
                Logger.LogError($"No current plan at UTC {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")}.");
                currentPeriod = new HalfHourPlan()
                {
                    Action = new PeriodAction() // Use the default values.
                };
            }

            // Look 8 hours ahead.
            IEnumerable<HalfHourPlan> plansToCheck = plan?.Plans.Where(z => z.Start >= currentPeriod.Start && z.Start < currentPeriod.Start.AddHours(8)) ?? new List<HalfHourPlan>() { currentPeriod };

            StringBuilder actions = new StringBuilder();

            // Check that it's doing what it's supposed to be doing.
            // update settings and log warning in case of discrepancy.

            // Are we on target?
            // If not then what can we do about it?

            Dictionary<string, string> settings = await _Lux.GetSettingsAsync();
            int battChargeRate = _Lux.GetBatteryChargeRate(settings);
            //int battGridDischargeRate = _Lux.GetBatteryGridDischargeRate(settings);

            // Discharge to grid.
            (bool outEnabled, DateTime outStart, DateTime outStop, int outBatteryLimitPercent) = _Lux.GetDischargeToGrid(settings);
            bool outEnabledWanted = outEnabled;
            DateTime outStartWanted = outStart;
            DateTime outStopWanted = outStop;
            int outBatteryLimitPercentWanted = outBatteryLimitPercent;

            outEnabledWanted = plansToCheck.Any(z => Plan.DischargeToGridCondition(z));
            if (plan != null && outEnabledWanted)
            {
                HalfHourPlan runFirst = plansToCheck.OrderBy(z => z.Start).First(z => Plan.DischargeToGridCondition(z));
                outStartWanted = runFirst.Start;
                outBatteryLimitPercentWanted = runFirst.Action!.DischargeToGrid;

                (IEnumerable<HalfHourPlan> run, HalfHourPlan? next) = plan.GetNextRun(runFirst, Plan.DischargeToGridCondition);
                outStopWanted = (next?.Start ?? run.Last().Start.AddMinutes(30));
                // If there's more than one run in plansToCheck then there must be a gap,
                // so in the first period in that gap the plan checker will set up for the next run.

                // If we're discharging now and started already then no change is needed.
                if (Plan.DischargeToGridCondition(currentPeriod) && outStart <= currentPeriod.Start && outStartWanted <= currentPeriod.Start)
                {
                    // No need to change it.
                    outStartWanted = outStart;
                }
            }

            if (!outEnabledWanted)
            {
                if (outEnabled)
                {
                    await _Lux.SetDischargeToGridLevelAsync(100);
                    actions.AppendLine($"SetDischargeToGridLevelAsync(100) to disable was {outBatteryLimitPercent} (enabled: {outEnabled}).");
                }
            }
            else
            {
                // Lux serivce returns the first time in the future that the out will start.
                // But the start of the current plan period may be in the past.
                if (outStart.TimeOfDay != outStartWanted.TimeOfDay)
                {
                    await _Lux.SetDischargeToGridStartAsync(outStartWanted);
                    actions.AppendLine($"SetDischargeToGridStartAsync({outStartWanted.ToString("dd MMM HH:mm")}) was {outStart.ToString("dd MMM HH:mm")}.");
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

            // Charge from grid.
            (bool inEnabled, DateTime inStart, DateTime inStop, int inBatteryLimitPercent) = _Lux.GetChargeFromGrid(settings);
            bool inEnabledWanted = inEnabled;
            DateTime inStartWanted = inStart;
            DateTime inStopWanted = inStop;
            int inBatteryLimitPercentWanted = inBatteryLimitPercent;

            inEnabledWanted = plansToCheck.Any(z => Plan.ChargeFromGridCondition(z));
            if (plan != null && inEnabledWanted)
            {
                HalfHourPlan runFirst = plansToCheck.OrderBy(z => z.Start).First(z => Plan.ChargeFromGridCondition(z));
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

            int battLevel = await _InfluxQuery.GetBatteryLevelAsync();

            if (!inEnabledWanted)
            {
                if (inEnabled)
                {
                    await _Lux.SetChargeFromGridLevelAsync(0);
                    actions.AppendLine($"SetChargeFromGridLevelAsync(0) to disable was {inBatteryLimitPercent} (enabled: {inEnabled}).");
                }
            }
            else if(battLevel >= inBatteryLimitPercentWanted)
            {
                // Run from the battery if possible.
                // If inEnabled then the inverter will power the house from the grid instead of from the battery
                // even if the battery level is greater than the cutoff.
                if (inEnabled)
                {
                    await _Lux.SetChargeFromGridLevelAsync(0);
                    actions.AppendLine($"SetChargeFromGridLevelAsync(0) to disable was {inBatteryLimitPercent} (enabled: {inEnabled}). The battery level is {battLevel}% and the charge limit is {inBatteryLimitPercentWanted}%.");
                }
            }
            else
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

            // Batt charge.
            int requiredBattChargeRate = battChargeRate; // No change.
            (DateTime sunrise, long _) = (await _InfluxQuery.QueryAsync(Query.Sunrise, currentPeriod.Start)).First().FirstOrDefault<long>();
            (DateTime sunset, long _) = (await _InfluxQuery.QueryAsync(Query.Sunset, currentPeriod.Start)).First().FirstOrDefault<long>();
            string why = "no change";
            if (inEnabledWanted && inStartWanted <= currentPeriod.Start && inStopWanted > currentPeriod.Start)
            {
                // Charging from grid.
                double powerRequiredKwh = _Batt.CapacityPercentToKiloWattHours(inBatteryLimitPercentWanted - battLevel);
                double hoursToCharge = (inStopWanted - (t0 > inStartWanted ? t0 : inStartWanted)).TotalHours;
                double kW = powerRequiredKwh / hoursToCharge;
                int b = _Batt.TransferKiloWattsToPercent(kW);
                requiredBattChargeRate = _Batt.RoundPercent(b);
                why = $"{powerRequiredKwh:0.0}kWh needed from grid to get from {battLevel}% to {inBatteryLimitPercentWanted}% in {hoursToCharge:0.0} hours until {inStopWanted:HH:mm} (mean rate {kW:0.0}kW)";
            }
            else if (outEnabledWanted && outStartWanted <= currentPeriod.Start && outStopWanted > currentPeriod.Start)
            {
                // Discharging to grid.
                requiredBattChargeRate = 0;

                double powerRequiredKwh = _Batt.CapacityPercentToKiloWattHours(battLevel - outBatteryLimitPercentWanted);
                double hoursToCharge = (outStopWanted - (t0 > outStartWanted ? t0 : outStartWanted)).TotalHours;
                int b = _Batt.TransferKiloWattsToPercent(powerRequiredKwh / hoursToCharge);
                requiredBattChargeRate = _Batt.RoundPercent(b); // Actually discharge in this case.

                why = $"discharge to grid (rate suggested is {requiredBattChargeRate}%)";
            }
            else if (t0.TimeOfDay <= sunrise.TimeOfDay || t0.TimeOfDay >= sunset.TimeOfDay)
            {
                why = "default (it's dark)";
            }
            else
            {
                // Default: charging from solar. Throttle it down.
                if (battLevel > 95)
                {
                    // High.
                    requiredBattChargeRate = 0;
                    why = $"battery is full ({battLevel}%)";
                }
                else
                {
                    double powerRequiredKwh = _Batt.CapacityPercentToKiloWattHours(95 - battLevel);
                    HalfHourPlan? nextDischargePeriod = plan?.Plans.GetNext(currentPeriod, Plan.DischargeToGridCondition);
                    if (nextDischargePeriod != null)
                    {
                        double hoursToCharge = (nextDischargePeriod.Start - t0).TotalHours;
                        double kW = powerRequiredKwh / hoursToCharge;
                        int b = _Batt.TransferKiloWattsToPercent(kW);
                        requiredBattChargeRate = _Batt.RoundPercent(b + 13 /* Add a bit in case it gets cloudy. */); 
                        why = $"{powerRequiredKwh:0.0}kWh needed to get from {battLevel}% to 95% in {hoursToCharge:0.0} hours until {nextDischargePeriod.Start:HH:mm} (mean rate {kW:0.0}kW)";
                    }
                    else
                    {
                        requiredBattChargeRate = 50;
                        why = $"no information";
                    }
                }
            }


            if (requiredBattChargeRate != battChargeRate)
            {
                await _Lux.SetBatteryChargeRateAsync(requiredBattChargeRate);
                actions.AppendLine($"SetBatteryChargeRate({requiredBattChargeRate}) was {battChargeRate}. Why: {why}.");
            }

            // Batt discharge.
            //if (battGridDischargeRate != (p?.Action?.BatteryGridDischargeRate ?? 97))
            //{
            //    await _Lux.SetBatteryGridDischargeRateAsync(p.Action?.BatteryGridDischargeRate ?? 97);
            //    actions.AppendLine($"SetBatteryDischargeRate({p.Action?.BatteryGridDischargeRate ?? 97}) was {battGridDischargeRate}.");
            //}

            // Report any changes.
            if (actions.Length > 0)
            {
                actions.AppendLine();
                actions.AppendLine($"  Battery: {battLevel}%");
                actions.AppendLine($"   Charge: {inStartWanted:HH:mm} to {inStopWanted:HH:mm} limit {inBatteryLimitPercentWanted} rate {requiredBattChargeRate}");
                actions.AppendLine($"Discharge: {outStartWanted:HH:mm} to {outStopWanted:HH:mm} limit {outBatteryLimitPercentWanted}");
                if (plan != null)
                {
                    actions.AppendLine();
                    HalfHourPlan? pp = plan.Current;
                    while (pp != null)
                    {
                        actions.AppendLine(pp.ToString());
                        pp = plan.Plans.GetNext(pp);
                    }
                }
                _Email.SendEmail($"PlanChecker {DateTime.UtcNow.ToString("dd MMM HH:mm")}", actions.ToString());
                Logger.LogInformation("PlanChecker made changes: " + Environment.NewLine + actions.ToString());
            }
        }

        private async Task<double> PredictAsync(DateTime now, DateTime until)
        {
            (DateTime sunrise, long _) = (await _InfluxQuery.QueryAsync(Query.Sunrise, now)).First().FirstOrDefault<long>();
            (DateTime sunset, long _) = (await _InfluxQuery.QueryAsync(Query.Sunset, now)).First().FirstOrDefault<long>();

            double dayLength = (sunset - sunrise).TotalHours;
            double generationToNow = 0;

            // Generation profile.
            // At k% of the way through the day v% of the generation has occurred.
            Dictionary<int, int> generationProfile = new Dictionary<int, int>();


            return await Task.FromResult<double>(0);
        }
    }
}
