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
        //private readonly IBatteryService _Batt;

        public PlanChecker(ILogger<LuxMonitor> logger, ILuxopusPlanService plans, ILuxService lux, IInfluxQueryService influxQuery, IEmailService email/*, IBatteryService batt*/) 
            : base(logger)
        {
            _Plans = plans;
            _Lux = lux;
            _InfluxQuery = influxQuery;
            _Email = email;
            //_Batt = batt;
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

            HalfHourPlan? p = plan?.Current;

            if (p == null)
            {
                Logger.LogError($"No current plan at UTC {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")}.");
                p = new HalfHourPlan()
                {
                    Action = new PeriodAction() // Use the default values.
                };
            }

            // Look 8 hours ahead.
            IEnumerable<HalfHourPlan> plansToCheck = plan?.Plans.Where(z => z.Start >= p.Start && z.Start < p.Start.AddHours(8)) ?? new List<HalfHourPlan>() { p };

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
                if (Plan.DischargeToGridCondition(p) && outStart <= p.Start && outStartWanted <= p.Start)
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
                if (Plan.ChargeFromGridCondition(p) && inStart <= p.Start && inStartWanted <= p.Start)
                {
                    // No need to change it.
                    inStartWanted = inStart;
                }
            }

            if (!inEnabledWanted)
            {
                if (inEnabled)
                {
                    await _Lux.SetChargeFromGridLevelAsync(0);
                    actions.AppendLine($"SetChargeFromGridLevelAsync(0) to disable was {inBatteryLimitPercent} (enabled: {inEnabled}).");
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
            int requiredBattChargeRate = 97; // Correct for charge from grid.
            string why = "default";
            if (inEnabledWanted && inStartWanted <= p.Start && inStopWanted > p.Start)
            {
                // Charging from grid.
                requiredBattChargeRate = 97;
                why = "charge from grid";
            }
            else if (outEnabledWanted && outStartWanted <= p.Start && outStopWanted > p.Start)
            {
                // Discharging to grid.
                requiredBattChargeRate = 0;
                why = "discharge to grid";
            }
            else
            {
                // Default: charging from solar. Throttle it down.
                int battLevel = await _InfluxQuery.GetBatteryLevelAsync();
                if (battLevel > 95)
                {
                    // High.
                    requiredBattChargeRate = 0;
                    why = $"battery is full ({battLevel}%)";
                }
                else
                {
                    HalfHourPlan? q = plan?.Plans.GetNext(p, Plan.DischargeToGridCondition);
                    int predicted = 0;
                    //if (q != null)
                    //{
                    //    (DateTime _, double g) = (await Generation(t0)); // Generation is in W.
                    //    predicted = Convert.ToInt32(Math.Ceiling(g * (t0.Hour < 13 ? 1.2 : 0.8)) * (q.Start - t0).TotalHours / 1000.0);
                    //}

                    if (predicted == 0)
                    {
                        // Default case.
                        if (battLevel > 80)
                        {
                            requiredBattChargeRate = 17; // ~500W.
                            why = $"battery is getting full ({battLevel}%)";
                        }
                        else
                        {
                            if (t0.Hour < 14)
                            {
                                requiredBattChargeRate = 33; // ~1000W.
                                why = $"it's early but battery is low ({battLevel}%)";
                            }
                            else
                            {
                                requiredBattChargeRate = 67; // ~2000W.
                                why = $"it's late and battery is low ({battLevel}%)";
                            }
                        }
                    }
                    else
                    {
                        //// !
                        //int required = _Lux.BattToKwh(95 - battLevel);
                        //if (predicted < required)
                        //{
                        //    requiredBattChargeRate = 75;
                        //}
                        //else
                        //{
                        //    requiredBattChargeRate = (100 * required) / predicted;
                        //}
                        //why = $"predicted: {predicted} kWh but {required} kWh required to get from {battLevel}% to 95%";
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
    }
}
