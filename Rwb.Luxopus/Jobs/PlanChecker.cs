using Microsoft.Extensions.Logging;
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

        public PlanChecker(ILogger<LuxMonitor> logger, ILuxopusPlanService plans, ILuxService lux, IInfluxQueryService influxQuery, IEmailService email) : base(logger)
        {
            _Plans = plans;
            _Lux = lux;
            _InfluxQuery = influxQuery;
            _Email = email;
        }

        protected static int PercentPerHour(int batteryAmpHours, int batteryVoltage, int watts)
        {
            int battWattHours = batteryAmpHours * batteryVoltage;
            decimal hours = Convert.ToDecimal(battWattHours) / Convert.ToDecimal(watts);
            return Convert.ToInt32(Math.Ceiling(100M / hours));
        }
        protected static int PercentPerHour(int watts)
        {
            return PercentPerHour(_BatteryCapacityAmpHours, _BatteryVoltage, watts);
        }

        protected static int MaxPercentPerHalfHour { get { return PercentPerHour(_BatteryCapacityAmpHours, _BatteryVoltage, _BatteryMaxPowerWatts) / 2; } }

        private const int _MedianHousePowerWatts = 240;
        private const int _BatteryCapacityAmpHours = 189;
        private const int _BatteryVoltage = 55;
        private const int _BatteryMaxPowerWatts = 3000;

        protected static int PercentRequiredFromUntil(DateTime from, DateTime until)
        {
            decimal hours = Convert.ToDecimal(until.Subtract(from).TotalHours);
            decimal percentPerHour = PercentPerHour(_MedianHousePowerWatts);
            return Convert.ToInt32(Math.Ceiling(hours * percentPerHour));
        }

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
            IEnumerable<HalfHourPlan> plansToCheck = plan?.Plans.Where(z => z.Start >= p.Start && z.Start < p.Start.AddHours(8)) ?? new List<HalfHourPlan>() { p};

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

            outEnabledWanted = plansToCheck.Any(z => (z.Action?.DischargeToGrid ?? 100) < 100);
            if (plan != null && outEnabledWanted)
            {
                HalfHourPlan runFirst = plansToCheck.OrderBy(z => z.Start).First(z => (z.Action?.DischargeToGrid ?? 100) < 100);
                outStartWanted = runFirst.Start;
                outBatteryLimitPercentWanted = runFirst.Action!.DischargeToGrid;

                (IEnumerable<HalfHourPlan> run, HalfHourPlan? next) = plan.GetNextRun(runFirst, Plan.DischargeToGridCondition);
                outStopWanted = (next?.Start ?? run.Last().Start.AddMinutes(30));
                // If there's more than one run in plansToCheck then there must be a gap,
                // so in the first period in that gap the plan checker will set up for the next run.

                // If we're discharging now and started already then no change is needed.
                if ( (p.Action?.DischargeToGrid ?? 100) < 100 && outStart <= p.Start && outStartWanted <= p.Start)
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
                if (outStart != outStartWanted)
                {
                    await _Lux.SetDischargeToGridStartAsync(outStartWanted);
                    actions.AppendLine($"SetDischargeToGridStartAsync({outStartWanted.ToString("HH:mm")}) was {outStart.ToString("HH:mm")}.");
                }

                if (outStop != outStopWanted)
                {
                    await _Lux.SetDischargeToGridStopAsync(outStopWanted);
                    actions.AppendLine($"SetDischargeToGridStopAsync({outStopWanted.ToString("HH:mm")}) was {outStop.ToString("HH:mm")}.");
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

            inEnabledWanted = plansToCheck.Any(z => (z.Action?.ChargeFromGrid ?? 0) > 0);
            if (plan != null && inEnabledWanted)
            {
                HalfHourPlan runFirst = plansToCheck.OrderBy(z => z.Start).First(z => (z.Action?.ChargeFromGrid ?? 0) > 0);
                inStartWanted = runFirst.Start;
                inBatteryLimitPercentWanted = runFirst.Action!.ChargeFromGrid;

                (IEnumerable<HalfHourPlan> run, HalfHourPlan? next) = plan.GetNextRun(runFirst, Plan.ChargeFromGridCondition);
                inStopWanted = (next?.Start ?? run.Last().Start.AddMinutes(30));

                // If we're charging now and started already then no change is needed.
                if ((p.Action?.ChargeFromGrid ?? 0) > 0 && inStart <= p.Start && inStartWanted <= p.Start)
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
                if (inStart != inStartWanted)
                {
                    await _Lux.SetChargeFromGridStartAsync(inStartWanted);
                    actions.AppendLine($"SetChargeFromGridStartAsync({inStartWanted.ToString("HH:mm")}) was {inStart.ToString("HH:mm")}.");
                }

                if (inStop != inStopWanted)
                {
                    await _Lux.SetChargeFromGridStopAsync(inStopWanted);
                    actions.AppendLine($"SetChargeFromGridStopAsync({inStopWanted.ToString("HH:mm")}0) was {inStop.ToString("HH:mm")}.");
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
            if (inEnabledWanted)
            {
                requiredBattChargeRate = 97;
                why = "charge from grid";
            }
            else
            {
                if (outEnabledWanted)
                {
                    requiredBattChargeRate = 0;
                    why = "discharge to grid";
                }
                else
                {
                    // Charging from solar.
                    // Therefore don't over-do it.
                    int battLevel = await _InfluxQuery.GetBatteryLevelAsync();
                    if (battLevel > 95)
                    {
                        requiredBattChargeRate = 0;
                        why = "discharging to grid";
                    }
                    else
                    {
                        if (DateTime.Now.Hour < 14)
                        {
                            if (battLevel > 75)
                            {
                                requiredBattChargeRate = 33;
                                why = "batttery has space but it's only the morning";
                            }
                            else
                            {
                                requiredBattChargeRate = 66;
                                why = "batttery has space bit is low, but it's only the morning";
                            }
                        }
                        else
                        {
                            requiredBattChargeRate = 33;
                            why = "batttery has space but it's the afternoon";
                        }
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
                if (plan != null)
                {
                    actions.AppendLine();
                    HalfHourPlan? pp = plan.Current;
                    while (pp != null)
                    {
                        actions.AppendLine(pp.ToString());
                        pp = plan.GetNext(pp);
                    }
                }
                _Email.SendEmail($"PlanChecker {DateTime.UtcNow.ToString("dd MMM HH:mm")}", actions.ToString());
                Logger.LogInformation("PlanChecker made changes: " + Environment.NewLine + actions.ToString());
            }
        }
    }
}
