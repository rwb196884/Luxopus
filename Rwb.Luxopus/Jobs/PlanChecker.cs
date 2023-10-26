using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
        private readonly BatterySettings _BatterySettings;

        public PlanChecker(
            ILogger<LuxMonitor> logger,
            ILuxopusPlanService plans,
            ILuxService lux,
            IInfluxQueryService influxQuery,
            IEmailService email,
            IBatteryService batt,
            IBurstLogService burstLog,
            IOptions<BatterySettings> batterySettings
            )
            : base(logger)
        {
            _Plans = plans;
            _Lux = lux;
            _InfluxQuery = influxQuery;
            _Email = email;
            _Batt = batt;
            _BurstLog = burstLog;
            _BatterySettings = batterySettings.Value;
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
            IEnumerable<Plan> ps = _Plans.LoadAll(t0);

            Plan? plan = _Plans.Load(t0);
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
            while (settings.Any(z => z.Value == "DATAFRAME_TIMEOUT"))
            {
                settings = await _Lux.GetSettingsAsync();
            }
            int battChargeRate = _Lux.GetBatteryChargeRate(settings);
            int battChargeFromGridRate = _Lux.GetBatteryChargeFromGridRate(settings);
            int battDischargeToGridRate = _Lux.GetBatteryDischargeToGridRate(settings);

            // Discharge to grid.
            (bool outEnabled, DateTime outStart, DateTime outStop, int outBatteryLimitPercent) = _Lux.GetDischargeToGrid(settings);
            bool outEnabledWanted = outEnabled;
            DateTime outStartWanted = outStart;
            DateTime outStopWanted = outStop;
            int outBatteryLimitPercentWanted = outBatteryLimitPercent;
            int battDischargeToGridRateWanted = battDischargeToGridRate;

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

            if (outEnabledWanted)
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
            }

            // Charge from grid.
            (bool inEnabled, DateTime inStart, DateTime inStop, int inBatteryLimitPercent) = _Lux.GetChargeFromGrid(settings);
            bool inEnabledWanted = inEnabled;
            DateTime inStartWanted = inStart;
            DateTime inStopWanted = inStop;
            int inBatteryLimitPercentWanted = inBatteryLimitPercent;
            int battChargeFromGridRateWanted = battChargeFromGridRate;

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

            int battLevel = await _InfluxQuery.GetBatteryLevelAsync(DateTime.UtcNow);

            if (!inEnabledWanted)
            {
                if (inEnabled)
                {
                    await _Lux.SetChargeFromGridLevelAsync(0);
                    actions.AppendLine($"SetChargeFromGridLevelAsync(0) to disable was {inBatteryLimitPercent} (enabled: {inEnabled}).");
                }
            }
            else if (battLevel >= inBatteryLimitPercentWanted)
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
            int battChargeRateWanted = battChargeRate; // No change.
            DateTime sunrise = DateTime.Now.AddMinutes(-30);
            DateTime sunset = DateTime.Now.AddMinutes(30);
            try
            {
                long _;
                (sunrise, _) = (await _InfluxQuery.QueryAsync(Query.Sunrise, currentPeriod.Start)).First().FirstOrDefault<long>();
                (sunset, _) = (await _InfluxQuery.QueryAsync(Query.Sunset, currentPeriod.Start)).First().FirstOrDefault<long>();
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Failed to query for sunrise and sunset.");
            }
            string why = "no change";

            if (inEnabledWanted && inStartWanted <= currentPeriod.Start && inStopWanted > currentPeriod.Start && battLevel < inBatteryLimitPercentWanted)
            {
                double powerRequiredKwh = _Batt.CapacityPercentToKiloWattHours(inBatteryLimitPercentWanted - battLevel);
                double hoursToCharge = (inStopWanted - (t0 > inStartWanted ? t0 : inStartWanted)).TotalHours;
                double kW = powerRequiredKwh / hoursToCharge;
                int b = _Batt.TransferKiloWattsToPercent(kW);
                b = b < 0 ? 10 : b;
                battChargeFromGridRateWanted = _Batt.RoundPercent(b);
                battChargeRateWanted = battChargeFromGridRateWanted > battChargeRateWanted ? battChargeFromGridRateWanted : battChargeRateWanted;
                why = $"{powerRequiredKwh:0.0}kWh needed from grid to get from {battLevel}% to {inBatteryLimitPercentWanted}% in {hoursToCharge:0.0} hours until {inStopWanted:HH:mm} (mean rate {kW:0.0}kW {battChargeFromGridRateWanted}%).";
            }
            else if (outEnabledWanted && outStartWanted <= currentPeriod.Start && outStopWanted > currentPeriod.Start && battLevel > outBatteryLimitPercentWanted)
            {
                // Discharging to grid.
                double powerRequiredKwh = _Batt.CapacityPercentToKiloWattHours(battLevel - outBatteryLimitPercentWanted);
                double hoursToCharge = (outStopWanted - (t0 > outStartWanted ? t0 : outStartWanted)).TotalHours;
                double kW = powerRequiredKwh / hoursToCharge;
                int b = _Batt.TransferKiloWattsToPercent(kW);
                b = b < 0 ? 10 : b;
                battDischargeToGridRateWanted = _Batt.RoundPercent(b);
                battChargeRateWanted = 0;
                why = $"Discharge to grid: {powerRequiredKwh:0.0}kWh needed to grid to get from {battLevel}% to {outBatteryLimitPercentWanted}% in {hoursToCharge:0.0} hours until {outStopWanted:HH:mm} (mean rate {kW:0.0}kW -> {battDischargeToGridRateWanted}%).";
            }
            else if (t0.TimeOfDay <= sunrise.TimeOfDay || t0.TimeOfDay >= sunset.TimeOfDay)
            {
                if (battChargeFromGridRateWanted < 80)
                {
                    battChargeRateWanted = 50;
                }
                why = "Default (it's dark).";
            }
            else
            {
                // Default: charging from solar. Throttle it down.
                if (battLevel >= _Batt.BatteryLimit - 2 /* It will still get about 60W. */)
                {
                    // Battery is full.
                    // Set charge rate high and enable discharge to grid to absorb generation peaks then discharge them.
                    battChargeRateWanted = 72;
                    outEnabledWanted = true;
                    battDischargeToGridRateWanted = 72;
                    outBatteryLimitPercentWanted = _Batt.BatteryLimit;
                    outStartWanted = currentPeriod.Start; // Needs to be constant in order not to spam changes.
                    outStopWanted = plan?.Next?.Start ?? currentPeriod.Start.AddMinutes(30);
                    why = $"Battery is full ({battLevel}%).";
                }
                else
                {
                    if (plan?.Next != null && Plan.ChargeFromGridCondition(plan!.Next!))
                    {
                        // Keep what we need so that we don't have to buy.
                        HalfHourPlan? afterCharge = plan.Plans.GetNext(plan.Next);

                        double kwhForUse = 0.25 * ((afterCharge?.Start ?? DateTime.Now.AddHours(3)) - t0).TotalHours;
                        int percentForUse = _Batt.CapacityKiloWattHoursToPercent(kwhForUse);
                        int percentTarget = (plan.Next?.Action?.ChargeFromGrid ?? 5) + percentForUse;
                        if (battLevel >= percentTarget)
                        {
                            // Already got enough.
                            battChargeRateWanted = 0;
                            why = $"{kwhForUse}kWh needed ({percentForUse}%) and charge target is {plan!.Next!.Action!.ChargeFromGrid}% but battery level is {battLevel}% > {percentTarget}%.";
                        }
                        else
                        {
                            double powerRequiredKwh = _Batt.CapacityPercentToKiloWattHours(percentTarget - battLevel);
                            DateTime until = plan!.Next!.Start;
                            if (until > sunset) { until = sunset; }
                            double hoursToCharge = (until - t0).TotalHours;
                            double kW = powerRequiredKwh / hoursToCharge;
                            int b = _Batt.TransferKiloWattsToPercent(kW);
                            battChargeRateWanted = _Batt.RoundPercent(b);
                            why = $"{powerRequiredKwh:0.0}kWh needed from grid to get from {battLevel}% to {percentTarget}% ({plan!.Next!.Action!.ChargeFromGrid}% charge target plus {kwhForUse:0.0}kWh {percentForUse}% for consumption) in {hoursToCharge:0.0} hours until {until:HH:mm} (mean rate {kW:0.0}kW).";
                        }
                    }
                    else if (plan?.Next != null && Plan.DischargeToGridCondition(plan!.Next!))
                    {
                        // Get fully charged before the discharge period.
                        DateTime tBattChargeFrom = currentPeriod.Start < sunrise ? sunrise : currentPeriod.Start;

                        int battLevelStart = await _InfluxQuery.GetBatteryLevelAsync(currentPeriod.Start);
                        DateTime nextPlanCheck = DateTime.UtcNow.AddMinutes(21); // Just before.
                        int battLevelTarget = battLevelStart + Convert.ToInt32(
                            Convert.ToDouble(100 - battLevelStart)
                            * nextPlanCheck.Subtract(tBattChargeFrom).TotalMinutes
                          / plan.Next.Start.Subtract(tBattChargeFrom).TotalMinutes
                            );

                        // Override for high generation.
                        // This doesn't work: when the battery gets to the limit the inverter prevents generation again.
                        // Fucking chinese shit.
                        (DateTime _, long generationMax) = //(DateTime.Now, 0);
                            (await _InfluxQuery.QueryAsync(@$"
from(bucket: ""solar"")
  |> range(start: {currentPeriod.Start.ToString("yyyy-MM-ddTHH:mm:00Z")}, stop: now())
  |> filter(fn: (r) => r[""_measurement""] == ""inverter"" and r[""_field""] == ""generation"")
  |> max()")).First().FirstOrDefault<long>();

                        if (generationMax > 2000 && DateTime.UtcNow < (plan?.Next?.Start ?? currentPeriod.Start.AddMinutes(30)).AddHours(-1))
                        {
                            outEnabledWanted = true;
                            battDischargeToGridRateWanted = 70; // Allow extra to be discharged.
                            if (outStartWanted.TimeOfDay > currentPeriod.Start.TimeOfDay)
                            {
                                outStartWanted = currentPeriod.Start;
                            }
                            if (outStopWanted.TimeOfDay < (plan?.Next?.Start ?? currentPeriod.Start.AddMinutes(30)).TimeOfDay)
                            {
                                outStopWanted = (plan?.Next?.Start ?? currentPeriod.Start.AddMinutes(30));
                            }

                            if (battLevel >= _Batt.BatteryLimit)
                            {
                                outBatteryLimitPercentWanted = _Batt.BatteryLimit;
                            }
                            else if (battLevel > battLevelTarget)
                            {
                                outBatteryLimitPercentWanted = (battLevel + 3) > _Batt.BatteryLimit ? _Batt.BatteryLimit : (battLevelTarget + 3);
                            }
                            else
                            {
                                outBatteryLimitPercentWanted = (battLevelTarget + 3) > 95 ? 95 : (battLevelTarget + 3);
                            }

                            // Let the Burst job sort out the batt charge rate.

                            //battChargeRateWanted = generationMax > 5500 ? 71 : 41;// Special value signals this case. Yuck. Seems to translate to about 1640W.
                            // Max generation witnessed was 6.2kW on 2023-02-20 but can only invert 3.6 therefore at most 2.8kW to battery.
                            // Battery charge at 100% seems to be about 4kW.
                            // Therefore battery charge rate should be at most 70%.
                            why = $"Generation peak of {generationMax}. Allow export with battery target of {outBatteryLimitPercentWanted}% (expected {battLevelTarget}%).";
                        }
                        else
                        {
                            // TODO
                            // If it's early and it looks like it's going to be a good day
                            // then keep the battery empty.

                            if (t0.Hour <= 9 && generationMax > 1500 && battLevel > 20)
                            {
                                // At 9am median generation is 1500.
                                battChargeRateWanted = 8;
                                why = "Keep battery empty in anticipation of high generation later today.";
                            }
                            else
                            {
                                // Plan A
                                outEnabledWanted = false;
                                inEnabledWanted = false;
                                double hoursToCharge = ((plan?.Next?.Start ?? currentPeriod.Start.AddMinutes(30)) - t0).TotalHours;
                                double powerRequiredKwh = _Batt.CapacityPercentToKiloWattHours(_Batt.BatteryLimit - battLevel);

                                // Are we behind schedule?
                                double extraPowerNeeded = 0.0;
                                if (battLevel < battLevelTarget)
                                {
                                    extraPowerNeeded = 2 * _Batt.CapacityPercentToKiloWattHours(battLevelTarget - battLevel);
                                }

                                double kW = (powerRequiredKwh + extraPowerNeeded) / hoursToCharge;
                                int b = _Batt.TransferKiloWattsToPercent(kW);

                                // Set the rate.
                                battChargeRateWanted = _Batt.RoundPercent(b);
                                string s = battLevelTarget != battLevel ? $" (should be {battLevelTarget}%)" : "";
                                why = $"{powerRequiredKwh:0.0}kWh needed to get from {battLevel}%{s} to {_Batt.BatteryLimit}% in {hoursToCharge:0.0} hours until {plan.Next.Start:HH:mm} (mean rate {kW:0.0}kW).";
                            }
                        }
                    }
                    else
                    {
                        // No plan. Set defaults.
                        battChargeRateWanted = 50;
                        why = $"No information.";
                    }
                }
            }

            // A P P L Y   S E T T I N G S

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

            // Discharge to grid.
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
