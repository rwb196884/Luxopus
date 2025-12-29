using Accord;
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
        //private readonly Planner _Planner;
        private readonly ILuxopusPlanService _Plans;
        private readonly ILuxService _Lux;
        private readonly IInfluxQueryService _InfluxQuery;
        private readonly IEmailService _Email;
        private readonly IBatteryService _Batt;
        private readonly IBurstLogService _BurstLog;
        private readonly BatteryTargetService _BatteryTargetService;

        public PlanChecker(
            ILogger<LuxMonitor> logger,
            //Planner planner,
            ILuxopusPlanService plans,
            ILuxService lux,
            IInfluxQueryService influxQuery,
            IEmailService email,
            IBatteryService batt,
            IBurstLogService burstLog,
            BatteryTargetService batteryTargetService
            )
            : base(logger)
        {
            //_Planner = planner;
            _Plans = plans;
            _Lux = lux;
            _InfluxQuery = influxQuery;
            _Email = email;
            _Batt = batt;
            _BurstLog = burstLog;
            _BatteryTargetService = batteryTargetService;
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
            //DateTime t0 = new DateTime(2025, 12, 24, 01, 31, 00);
            DateTime t0 = DateTime.UtcNow;

            Plan? plan = _Plans.Load(t0);
            //if (plan == null || plan.Plans?.Count == 0 || plan.Current == null)
            //{
            //    Logger.LogWarning($"No plan at UTC {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")}. Trying to make a new one.");
            //    await _Planner.RunAsync(cancellationToken);
            //    _Plans.Load(t0);
            //}

            if (plan == null || plan.Plans?.Count == 0 || plan.Current == null)
            {
                Logger.LogWarning($"No plan at UTC {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")}. Trying to load past plan.");
                plan = _Plans.Load(t0.AddDays(-2));
                if (plan != null)
                {
                    Logger.LogWarning($"No plan at UTC {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")}. Using plan from {plan.Current.Start.ToString("yyyy-MM-dd HH:mm")}.");
                    foreach (PeriodPlan p in plan.Plans)
                    {
                        p.Start = p.Start.AddDays(2);
                    }
                }
                else
                {
                    Logger.LogError($"No plan at UTC {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")}. Could not load past plan.");
                    return;
                }
            }

            if (plan?.Current == null || plan!.Current.Start < DateTime.Now.AddDays(-7))
            {
                Logger.LogError($"No current plan at UTC {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")}.");
                return;
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
            LuxAction dischargeToGridCurrent = _Lux.GetDischargeToGrid(settings);
            LuxAction dischargeToGridWanted = LuxAction.NextDisharge(plan, dischargeToGridCurrent);

            if (Plan.DischargeToGridCondition(plan.Current!) && dischargeToGridWanted.Enable)
            {
                try
                {
                    (DateTime lastOccupied, bool wasOccupied) = (await _InfluxQuery.QueryAsync(Query.LastOccupied, DateTime.UtcNow)).Single().FirstOrDefault<bool>();
                    if (wasOccupied && lastOccupied < DateTime.Now.AddHours(-3) && dischargeToGridWanted.Limit > _Batt.BatteryMinimumLimit)
                    {
                        actions.AppendLine($"DischargeToGridLevel overridden from plan of {dischargeToGridWanted.Limit}% to {_Batt.BatteryMinimumLimit}% because house not occupied since {lastOccupied.ToString("yyyy-MM-dd HH:mm")}.");
                        dischargeToGridWanted.Limit = _Batt.BatteryMinimumLimit;
                    }
                }
                catch (InvalidOperationException e)
                {
                    actions.AppendLine($"DischargeToGridLevel overridden from plan of {dischargeToGridWanted.Limit}% to {_Batt.BatteryMinimumLimit}% because house not occupied (query failed: ${e.Message}).");
                    dischargeToGridWanted.Limit = _Batt.BatteryMinimumLimit;
                }
                catch (Exception e)
                {
                    actions.AppendLine($"DischargeToGridLevel not overridden because house not occupied query failed: ${e.Message}.");
                }

                //goto Apply;
            }

            // Charge from grid -- according to plan.
            LuxAction chargeFromGridCurrent = _Lux.GetChargeFromGrid(settings);
            LuxAction? chargeFromGridWanted = LuxAction.NextCharge(plan, chargeFromGridCurrent);

            int battLevel = await _InfluxQuery.GetBatteryLevelAsync(DateTime.UtcNow);
            int battLevelEnd = _BatteryTargetService.DefaultBatteryLevelEnd;
            if ((plan.Next?.Buy ?? 1) <= 0)
            {
                battLevelEnd -= _Batt.CapacityKiloWattHoursToPercent(plan.Plans.FutureFreeHoursBeforeNextDischarge(plan.Current!) * 3.2);
                battLevelEnd = battLevelEnd < battLevel ? battLevel : battLevelEnd;
            }
            BatteryTargetInfo bti = await _BatteryTargetService.Compute(plan, battLevelEnd);

            string why = "no change";

            DateTime tNext = plan.Next?.Start ?? DateTime.UtcNow.AddHours(1);
            if (Plan.ChargeFromGridCondition(plan.Current!) && battLevel < plan.Current!.Action.ChargeFromGrid)
            {
                // Planned charge.
                chargeFromGridWanted.Enable = true;
                if (chargeFromGridWanted.Start > plan.Current!.Start) { chargeFromGridWanted.Start = plan.Current!.Start; }
                
                PeriodPlan? next = plan.Plans.GetNext(plan.Current!);
                while(next != null && Plan.ChargeFromGridCondition(next) && next.Action.ChargeFromGrid == plan.Current.Action.ChargeFromGrid)
                {
                    next = plan.Plans.GetNext(next);
                }
                if (next != null)
                {
                    PeriodPlan endOfRun = plan.Plans.GetPrevious(next);
                    chargeFromGridWanted.Limit = endOfRun.Action.ChargeFromGrid;
                    chargeFromGridWanted.End = next.Start;
                }
                else
                {
                    chargeFromGridWanted.Limit = plan.Current!.Action.ChargeFromGrid;
                    if (chargeFromGridCurrent.End < tNext) { chargeFromGridWanted.End = tNext; }
                }

                double powerRequiredKwh = _Batt.CapacityPercentToKiloWattHours(chargeFromGridWanted.Limit - battLevel);
                double hoursToCharge = (chargeFromGridWanted.End - t0).TotalHours;
                double kW = powerRequiredKwh / hoursToCharge;
                chargeFromGridWanted.Rate = _Batt.RoundPercent(_Batt.TransferKiloWattsToPercent(kW));
                battChargeRateWanted = chargeFromGridWanted.Rate > battChargeRateWanted ? chargeFromGridWanted.Rate : battChargeRateWanted;
                chargeLastWanted = false;
                why = $"{powerRequiredKwh:0.0}kWh needed from grid to get from {battLevel}% to {plan.Current!.Action.ChargeFromGrid}% in {hoursToCharge:0.0} hours until {tNext:HH:mm} (mean rate {kW:0.0}kW -> {chargeFromGridWanted.Rate}%).";
            }
            else if (Plan.DischargeToGridCondition(plan.Current!) && battLevel > plan.Current!.Action.DischargeToGrid)
            {
                // Planned discharge.
                dischargeToGridWanted.Enable = true;
                if (dischargeToGridWanted.Start > plan.Current!.Start) { dischargeToGridWanted.Start = plan.Current!.Start; }

                PeriodPlan? next = plan.Plans.GetNext(plan.Current!);
                while (next != null && Plan.DischargeToGridCondition(next) && next.Action.DischargeToGrid == plan.Current.Action.DischargeToGrid)
                {
                    next = plan.Plans.GetNext(next);
                }
                if (next != null)
                {
                    PeriodPlan endOfRun = plan.Plans.GetPrevious(next);
                    dischargeToGridWanted.Limit = endOfRun.Action.DischargeToGrid;
                    dischargeToGridWanted.End = next.Start;
                }
                else
                {
                    dischargeToGridWanted.Limit = plan.Current!.Action.DischargeToGrid;
                    if (dischargeToGridWanted.End < tNext) { dischargeToGridWanted.End = tNext; }
                }

                double powerRequiredKwh = _Batt.CapacityPercentToKiloWattHours(battLevel - dischargeToGridWanted.Limit);
                double hoursToCharge = (dischargeToGridWanted.End - t0).TotalHours;
                double kW = powerRequiredKwh / hoursToCharge;
                dischargeToGridWanted.Rate = _Batt.RoundPercent(_Batt.TransferKiloWattsToPercent(kW));
                battChargeRateWanted = 100;
                chargeLastWanted = true;
                why = $"Discharge to grid: {powerRequiredKwh:0.0}kWh needed to grid to get from {battLevel}% to {plan.Current!.Action.DischargeToGrid}% in {hoursToCharge:0.0} hours until {tNext:HH:mm} (mean rate {kW:0.0}kW -> {dischargeToGridWanted.Rate}%).";
            }
            else
            {
                if (battLevel + bti.PredictionBatteryPercent >= 100 && t0.Hour <= 9)
                {
                    chargeLastWanted = true;
                    why = $"Batt level {battLevel}% plus prediction {bti.PredictionBatteryPercent}% is greater than 100% and it's before 10am UTC.";
                }
                else if (t0.TimeOfDay <= bti.GenerationStart.TimeOfDay || t0.TimeOfDay >= bti.GenerationEnd.TimeOfDay)
                {
                    // No solar generation.
                    if (battChargeRateWanted < 80)
                    {
                        battChargeRateWanted = 50;
                    }
                    chargeLastWanted = t0.TimeOfDay <= bti.GenerationStart.TimeOfDay && bti.PredictionBatteryPercent >= 200;
                    why = $"Default (time {t0:HH:mm} is outside of generation range {bti.GenerationStart:HH:mm} to {bti.GenerationEnd:HH:mm} and generation prediction is {bti.PredictionBatteryPercent}%).";
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
                            dischargeToGridWanted = new LuxAction()
                            {
                                Enable = true,
                                Rate = 72,
                                Limit = 97,
                                Start = plan.Current!.Start, // Needs to be constant in order not to spam changes.
                                End = plan?.Next?.Start ?? t0.StartOfHalfHour().AddHours(1)
                            };
                            chargeLastWanted = true;
                            why = $"Battery is full ({battLevel}%) and max generation in last hour is {generationMaxLastHour}.";
                        }
                    }
                    else if (plan?.Next != null && Plan.DischargeToGridCondition(plan!.Next!))
                    {
                        (DateTime _, long generationMax) = //(DateTime.Now, 0);
                            (await _InfluxQuery.QueryAsync(@$"
from(bucket: ""solar"")
  |> range(start: {plan.Current!.Start.ToString("yyyy-MM-ddTHH:mm:00Z")}, stop: now())
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
                        DateTime tBattChargeFrom = bti.GenerationStart > plan.Current!.Start ? bti.GenerationStart : plan.Current!.Start;

                        int battLevelStart = await _InfluxQuery.GetBatteryLevelAsync(plan.Current!.Start);
                        DateTime nextPlanCheck = DateTime.UtcNow.StartOfHalfHour().AddMinutes(30);


                        if (DateTime.Now.Hour < 9 && bti.PredictionBatteryPercent > _BatteryTargetService.DefaultBatteryLevelEnd)
                        {
                            chargeLastWanted = true;
                            battChargeRateWanted = 91;
                            why = $"Predicted to be a good day (generation prediction {bti.PredictionKWh:#0.0}kW therefore charge last before 9am.";
                            dischargeToGridWanted = new LuxAction()
                            {
                                Enable = true,
                                Start = plan.Current!.Start,
                                End = plan.Next?.Start ?? DateTime.UtcNow.AddHours(12),
                                Limit = bti.BatteryTarget - 5,
                                Rate = 91
                            };
                        }
                        else if (t0.Month >= 4 && t0.Month <= 8 && DateTime.Now.Hour <= 9 && (bti.ScaleMethod == ScaleMethod.Slow || generationRecentMean > 800) && battLevel >= 9)
                        {
                            chargeLastWanted = true;
                            battChargeRateWanted = 92;

                            if (battLevel > bti.BatteryTarget - 5 && generationRecentMean < 3000)
                            {
                                dischargeToGridWanted = new LuxAction()
                                {
                                    Enable = true,
                                    Start = plan.Current!.Start,
                                    End = plan.Next?.Start ?? DateTime.UtcNow.AddHours(12),
                                    Limit = bti.BatteryTarget - 5,
                                    Rate = 91
                                };
                                why = $"Predicted to be a good day (generation prediction {bti.PredictionKWh:#0.0}kW, recent mean {generationRecentMean / 1000:#0.0}kW) therefore charge last before 10am. Discharge to grid because battery level {battLevel}% is ahead of target target of {bti.TargetDescription}).";
                            }
                            else
                            {
                                why = $"Predicted to be a good day (generation prediction {bti.PredictionKWh:#0.0}kW, generation mean {generationRecentMean / 1000:#0.0}kW) therefore charge last before 10am. Battery level {battLevel}% target of {bti.TargetDescription}.";
                            }
                            goto Apply;
                        }
                        else if (t0.Month >= 4 && t0.Month <= 8 && t0.Hour <= 10 /* up to 11AM BST */ && bti.ScaleMethod == ScaleMethod.Slow && generationMax > 2000 && battLevel > bti.BatteryTarget - 8)
                        {
                            // At 9am median generation is 1500.
                            battChargeRateWanted = 93;
                            chargeLastWanted = true;
                            if (battLevel > bti.BatteryTarget - 5)
                            {
                                dischargeToGridWanted = new LuxAction()
                                {
                                    Enable = true,
                                    Start = plan.Current!.Start,
                                    End = plan.Next?.Start ?? DateTime.UtcNow.AddHours(12),
                                    Limit = bti.BatteryTarget - 5,
                                    Rate = 91
                                };
                                why = $"Predicted to be a good day (generation prediction {bti.PredictionKWh:#0.0}kW, max {generationMax / 1000:#0.0}kW) therefore charge last before 9am. Discharge to grid because battery level {battLevel}% is ahead of target target of {bti.TargetDescription}.";
                            }
                            else
                            {
                                why = $"Predicted to be a good day (generation prediction {bti.PredictionKWh:#0.0}kW, max {generationMax / 1000:#0.0}kW) therefore charge last before 9am. Battery level {battLevel}% target of {bti.TargetDescription}.";
                                chargeLastWanted = false;

                            }
                            goto Apply;
                        }

                        DateTime endOfCharge = bti.GenerationEnd < plan.Next.Start ? bti.GenerationEnd : plan.Next.Start;
                        double hoursToCharge = (endOfCharge - t0).TotalHours;
                        double powerRequiredKwh = _Batt.CapacityPercentToKiloWattHours(battLevelEnd - battLevel);
                        string s = bti.BatteryTarget != battLevel ? $" (prediction {bti.PredictionKWh:0.0}kWh so battery level should be {bti.BatteryTarget}% ({bti.BatteryTargetS}% < {bti.BatteryTargetL}% < {bti.BatteryTargetF}%))" : "";

                        // Are we behind schedule?
                        double extraPowerNeeded = 0.0;
                        if (battLevel < bti.BatteryTarget)
                        {
                            extraPowerNeeded = _Batt.CapacityPercentToKiloWattHours(bti.BatteryTarget - battLevel);
                            chargeLastWanted = false;
                            double kW = (powerRequiredKwh + extraPowerNeeded) / hoursToCharge;
                            int b = _Batt.TransferKiloWattsToPercent(kW);
                            battChargeRateWanted = _Batt.RoundPercent(b);
                            why = $"{powerRequiredKwh:0.0}kWh needed to get from {battLevel}%{s} to {battLevelEnd}% in {hoursToCharge:0.0} hours until {endOfCharge:HH:mm} (mean rate {kW:0.0}kW -> {battChargeRateWanted}%).";
                        }
                        else
                        {
                            chargeLastWanted = true;
                            battChargeRateWanted = 94;
                            why = $"{powerRequiredKwh:0.0}kWh needed to get from {battLevel}%{s} to {battLevelEnd}% in {hoursToCharge:0.0} hours until {endOfCharge:HH:mm} but ahead of target therefore charge last.";
                        }

                        // Set the rate.

                        if (bti.PredictionBatteryPercent < 200)
                        {
                            battChargeRateWanted = 95;
                            chargeLastWanted = false;

                        }
                        else if (generationRecentMax < 3000 && extraPowerNeeded > 0)
                        {
                            battChargeRateWanted = 95;
                            chargeLastWanted = false;
                            why += $" But we are behind by {extraPowerNeeded:0.0}kW therefore override to 95%.";
                        }

                        if ((powerRequiredKwh + extraPowerNeeded * 1.5 /* caution factor */ ) / hoursToCharge > generationRecentMean)
                        {
                            battChargeRateWanted = 96;
                            chargeLastWanted = false;
                            why += $" Need {(powerRequiredKwh + extraPowerNeeded):0.0}kWh in {hoursToCharge:0.0} hours but recent generation is {generationRecentMean / 1000:0.0}kW therefore override to 96%.";
                        }

                        if (generationRecentMax < 3600 && battLevel < bti.BatteryTarget + 5 && generationMeanDifference < 0)
                        {
                            battChargeRateWanted = battChargeRateWanted > 40 ? 97 : battChargeRateWanted * 2;
                            chargeLastWanted = false;
                            why += $" Rate of generation is decreasing ({generationMeanDifference:0}W) therefore override to {battChargeRateWanted}%.";
                        }
                    }
                    else
                    {
                        // No plan. Set defaults.
                        if (battLevel > battLevelEnd)
                        {
                            why = $"No information. Battery level {battLevel}% is above end level of {battLevelEnd}. (Current target of {bti.TargetDescription}.)";
                            chargeLastWanted = true;
                            battChargeRateWanted = 71;
                        }
                        else
                        {
                            battChargeRateWanted = 71;
                            chargeLastWanted = Plan.DischargeToGridCondition(plan.Current!);
                            why = $"No information.";

                        }
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

            // Charge last.
            if (chargeLast != chargeLastWanted)
            {
                await _Lux.SetChargeLastAsync(chargeLastWanted);
                actions.AppendLine($"SetChargeLastAsync({chargeLastWanted}) was {chargeLast}.");
            }

            // Charge from grid.
            if (chargeFromGridWanted != null)
            {
                bool changedCharge = await _Lux.SetChargeFromGrid(chargeFromGridCurrent, chargeFromGridWanted);
                if (changedCharge)
                {
                    actions.AppendLine($"Charge from grid was: {chargeFromGridCurrent}");
                    actions.AppendLine($"Charge from grid is : {chargeFromGridWanted}");
                }
            }

            // Discharge to grid.
            if (dischargeToGridWanted != null)
            {
                bool changedDischarge = await _Lux.SetDischargeToGrid(dischargeToGridCurrent, dischargeToGridWanted);
                if (changedDischarge)
                {
                    actions.AppendLine($"Discharge to grid was: {dischargeToGridCurrent}");
                    actions.AppendLine($"Discharge to grid is : {dischargeToGridWanted}");
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

                if (plan != null)
                {
                    actions.AppendLine();
                    PeriodPlan? pp = plan.Current!;
                    while (pp != null)
                    {
                        actions.AppendLine(pp.ToString());
                        pp = plan.Plans?.GetNext(pp);
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
