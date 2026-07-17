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
        private readonly BatteryUsageProfileService _BatteryUsageProfileService;

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
,
            BatteryUsageProfileService batteryUsageProfileService)
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
            _BatteryUsageProfileService = batteryUsageProfileService;
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
            if (plan == null || plan.Plans?.Count == 0 || plan.Current == null)
            {
                Logger.LogWarning($"No plan at UTC {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")}.");
                //await _Planner.RunAsync(cancellationToken);
                //_Plans.Load(t0);
            }

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
            LuxAction dischargeToGridWanted = LuxAction.NextDisharge(plan, dischargeToGridCurrent, false) ?? dischargeToGridCurrent.Clone();

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

            int battLevel = await _InfluxQuery.GetBatteryLevelAsync(DateTime.UtcNow);

            // Charge from grid -- according to plan.
            LuxAction chargeFromGridCurrent = _Lux.GetChargeFromGrid(settings);
            LuxAction chargeFromGridWanted = LuxAction.NextCharge(plan, chargeFromGridCurrent, false) ?? chargeFromGridCurrent.Clone();

            StringBuilder actionInfo = new StringBuilder();
            DateTime tNext = plan.Next?.Start ?? DateTime.UtcNow.AddHours(1);
            if (Plan.ChargeFromGridCondition(plan.Current!))
            {
                // Planned charge.
                chargeFromGridWanted.Enable = battLevel < plan.Current!.Action.ChargeFromGrid + 1;
                if (chargeFromGridWanted.Start > plan.Current!.Start) { chargeFromGridWanted.Start = plan.Current!.Start; }

                // Looking at a run can mess up the charge rate in the current period which may need, e.g., to me maximal when the price is low.
                //PeriodPlan? next = plan.Plans.GetNext(plan.Current!);
                //while (next != null && Plan.ChargeFromGridCondition(next) && next.Action.ChargeFromGrid == plan.Current.Action.ChargeFromGrid)
                //{
                //    next = plan.Plans.GetNext(next);
                //}
                //if (next != null)
                //{
                //    PeriodPlan endOfRun = plan.Plans.GetPrevious(next);
                //    chargeFromGridWanted.Limit = endOfRun.Action.ChargeFromGrid;
                //    chargeFromGridWanted.End = next.Start;
                //}
                //else
                //{
                chargeFromGridWanted.Limit = plan.Current!.Action.ChargeFromGrid;
                if (chargeFromGridCurrent.End < tNext) { chargeFromGridWanted.End = tNext; }
                //}

                double powerRequiredKwh = _Batt.CapacityPercentToKiloWattHours(chargeFromGridWanted.Limit - battLevel);
                double hoursToCharge = (chargeFromGridWanted.End - t0).TotalHours;
                double kW = powerRequiredKwh / hoursToCharge;
                chargeFromGridWanted.Rate = _Batt.RoundPercent(_Batt.TransferKiloWattsToPercent(kW));
                battChargeRateWanted = chargeFromGridWanted.Rate > battChargeRateWanted ? chargeFromGridWanted.Rate : battChargeRateWanted;
                chargeLastWanted = false;
                actionInfo.AppendLine($"{powerRequiredKwh:0.0}kWh needed from grid to get from {battLevel}% to {plan.Current!.Action.ChargeFromGrid}% in {hoursToCharge:0.0} hours until {tNext:HH:mm} (mean rate {kW:0.0}kW -> {chargeFromGridWanted.Rate}%).");
            }
            else if (Plan.DischargeToGridCondition(plan.Current!) && battLevel > plan.Current!.Action.DischargeToGrid)
            {
                // Planned discharge.
                dischargeToGridWanted.Enable = true;
                if (dischargeToGridWanted.Start > plan.Current!.Start) { dischargeToGridWanted.Start = plan.Current!.Start; }

                // Looking at a run can mess up the discharge rate in the current period which may need, e.g., to me maximal when the price is high.
                //PeriodPlan? next = plan.Plans.GetNext(plan.Current!);
                //while (next != null && Plan.DischargeToGridCondition(next) && next.Action.DischargeToGrid == plan.Current.Action.DischargeToGrid)
                //{
                //    next = plan.Plans.GetNext(next);
                //}
                //if (next != null)
                //{
                //    PeriodPlan endOfRun = plan.Plans.GetPrevious(next);
                //    dischargeToGridWanted.Limit = endOfRun.Action.DischargeToGrid;
                //    dischargeToGridWanted.End = next.Start;
                //}
                //else
                //{
                dischargeToGridWanted.Limit = plan.Current!.Action.DischargeToGrid;
                dischargeToGridWanted.End = tNext;
                //}

                double powerRequiredKwh = _Batt.CapacityPercentToKiloWattHours(battLevel - dischargeToGridWanted.Limit);
                double hoursToCharge = (dischargeToGridWanted.End - t0).TotalHours;
                double kW = powerRequiredKwh / hoursToCharge;
                dischargeToGridWanted.Rate = _Batt.RoundPercent(_Batt.TransferKiloWattsToPercent(kW));
                battChargeRateWanted = 100;
                chargeLastWanted = true;
                actionInfo.AppendLine($"Discharge to grid: {powerRequiredKwh:0.0}kWh needed to grid to get from {battLevel}% to {plan.Current!.Action.DischargeToGrid}% in {hoursToCharge:0.0} hours until {tNext:HH:mm} (mean rate {kW:0.0}kW -> {dischargeToGridWanted.Rate}%).");
            }
            else
            {
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

                actionInfo.AppendLine($"           Target: mimimum {_Batt.BatteryMinimumLimit}% plus use {battUse}% plus maximum dischargeable {_Batt.MaxDischarge * 3}% = {battLevelEnd}%.");
                // TODO: this assumes flux; solar charge target should be set by the plan.

                (_, int bcSince, int bcPeriod) = _Lux.GetBatteryCalibration(settings);
                if (bcSince > bcPeriod - 5)
                {
                    battLevelEnd = 100;
                }

                if ((plan.Next?.Buy ?? 1) <= 0)
                {
                    battLevelEnd -= _Batt.CapacityKiloWattHoursToPercent(plan.Plans.FutureFreeHoursBeforeNextDischarge(plan.Current!) * 3.2);
                    battLevelEnd = battLevelEnd < battLevel ? battLevel : battLevelEnd;
                }

                BatteryTargetInfo bti = await _BatteryTargetService.Compute(plan, battLevelEnd);
                actionInfo.AppendLine($"    Battery level: {battLevel}%");
                actionInfo.AppendLine($"   Battery target: {bti.TargetDescription}");
                actionInfo.AppendLine($" Battery headroom: {bti.HeadroomScaled}% scaled of total {100 - bti.BatteryLevelEnd}%");
                actionInfo.AppendLine($"Charging required: {bti.ChargeDescription}");
                actionInfo.AppendLine($"      Charge last: {(chargeLast ? "on" : "off")}");
                actionInfo.AppendLine($" Batt charge rate: {battChargeRate}%");

                if (t0.TimeOfDay <= bti.GenerationStart.TimeOfDay)
                {
                    actionInfo.AppendLine($"Plan check at {t0:HH:mm} is before start of generation at {bti.GenerationStart:HH:mm}.");
                    chargeLastWanted = false;
                    battChargeRateWanted = 100;
                }
                else if (t0.TimeOfDay >= bti.GenerationEnd.TimeOfDay)
                {
                    actionInfo.AppendLine($"Plan check at {t0:HH:mm} is after end of generation at {bti.GenerationEnd:HH:mm}.");
                    chargeLastWanted = false;
                    battChargeRateWanted = 100;
                    // TODO: discharge to make room for tomorrow?
                }
                else if (battLevel + bti.PredictionBatteryPercent >= 200 && DateTime.Now.Hour <= 9 && t0.Month >= 3 && t0.Month <= 8)
                {
                    chargeLastWanted = true;
                    battChargeRateWanted = 100;
                    actionInfo.AppendLine($"Batt level {battLevel}% plus prediction {bti.PredictionBatteryPercent}% is greater than 200%: charge last before 10am (local) March to August.");
                }
                else if (bti.BatteryLevelCurrent >= bti.BatteryLevelEnd)
                {
                    battChargeRateWanted = 100;
                    chargeLastWanted = true;
                    actionInfo.AppendLine($"Battery level has reached target ({bti.BatteryLevelEnd}%).");
                }
                else
                {
                    // Get ~~fully~~ to target charge before the discharge period.
                    // If the battery is small then the target is always 100%,

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
                            chargeLastWanted = false;
                            actionInfo.AppendLine($"Battery is full ({battLevel}%) and max generation in last hour is {generationMaxLastHour}.");
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
                            chargeLastWanted = false;
                            actionInfo.AppendLine($"Battery is full ({battLevel}%) and max generation in last hour is {generationMaxLastHour}.");
                        }
                    }
                    else if (plan?.Next != null && Plan.DischargeToGridCondition(plan!.Next!))
                    {
                        long generationRecentMax = (await _InfluxQuery.QueryAsync(@$"
from(bucket: ""solar"")
  |> range(start: -25m, stop: now())
  |> filter(fn: (r) => r[""_measurement""] == ""inverter"" and r[""_field""] == ""generation"")
  |> max()")
                           ).First().Records.First().GetValue<long>();
                        double kwMaxForBattAfterCL = (Convert.ToDouble(generationRecentMax) - 3600) / 1000;
                        if (kwMaxForBattAfterCL < 0)
                        {
                            kwMaxForBattAfterCL = 0;
                        }
                        int pcMaxForBattAfterCL = kwMaxForBattAfterCL == 0 ? 0 : _Batt.RoundPercent(_Batt.TransferKiloWattsToPercent(kwMaxForBattAfterCL));
                        actionInfo.AppendLine($"   Generation max: {generationRecentMax:0}W leaves {kwMaxForBattAfterCL:0.0}kW ({pcMaxForBattAfterCL}%) for battery after charge last.");

                        double generationRecentMean = (await _InfluxQuery.QueryAsync(@$"
from(bucket: ""solar"")
  |> range(start: -25m, stop: now())
  |> filter(fn: (r) => r[""_measurement""] == ""inverter"" and r[""_field""] == ""generation"")
  |> mean()")
                           ).First().Records.First().GetValue<double>();
                        double kwMeanForBattAfterCL = (Convert.ToDouble(generationRecentMean) - 3600) / 1000;
                        if (kwMeanForBattAfterCL < 0)
                        {
                            kwMeanForBattAfterCL = 0;
                        }
                        int pcMeanForBattAfterCL = kwMeanForBattAfterCL == 0 ? 0 : _Batt.RoundPercent(_Batt.TransferKiloWattsToPercent(kwMeanForBattAfterCL));
                        actionInfo.AppendLine($"  Generation mean: {generationRecentMean:0}W leaves {kwMeanForBattAfterCL:0.0}kW ({pcMeanForBattAfterCL}%) for battery after charge last.");

                        double generationMeanDifference = (await _InfluxQuery.QueryAsync(@$"
from(bucket: ""solar"")
  |> range(start: -45m, stop: now())
  |> filter(fn: (r) => r[""_measurement""] == ""inverter"" and r[""_field""] == ""generation"")
  |> difference()
  |> mean()")
                           ).First().Records.First().GetValue<double>();

                        // Are we behind schedule?
                        double extraPowerNeeded = 0.0;
                        int extraChargeRateNeeded = 0;
                        if (battLevel < bti.BatteryTarget)
                        {
                            extraPowerNeeded = _Batt.CapacityPercentToKiloWattHours(bti.BatteryTarget + bti.HeadroomScaled - battLevel);
                            extraChargeRateNeeded = _Batt.TransferKiloWattsToPercent(extraPowerNeeded * 2 /* Get it in th next half hour. */);
                        }

                        if (pcMeanForBattAfterCL >= bti.ChargeRateNeededHPercent + extraChargeRateNeeded)
                        {
                            chargeLastWanted = true;
                            battChargeRateWanted = 100;
                            actionInfo.AppendLine($"Enable charge last because charge rate needed {bti.ChargeRateNeededHPercent}% (including headroom) is less than power available for battery after charge last {pcMeanForBattAfterCL}%.");
                        }
                        else
                        {
                            chargeLastWanted = false;
                            battChargeRateWanted = Math.Min(100, bti.ChargeRateNeededHPercent + extraChargeRateNeeded);
                            battChargeRateWanted = battChargeRateWanted > 100 ? 100 : battChargeRateWanted;
                            actionInfo.AppendLine($"Battery charge rate increased by {extraChargeRateNeeded}% to {battChargeRateWanted}% to get extra {extraPowerNeeded}kW in the next half hour.");
                        }

                        if (battLevel < bti.BatteryTarget - 3
                            && generationRecentMean < 3200
                            && plan.Current.Buy * 1.1M < plan.Next.Sell
                            && DateTime.UtcNow > plan.Next.Start.AddHours(-2))
                        {
                            chargeFromGridWanted = chargeFromGridCurrent.Clone();
                            double kWh = _Batt.CapacityPercentToKiloWattHours(bti.BatteryTarget - battLevel);
                            double dt = (plan.Next.Start - DateTime.UtcNow).TotalHours;
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
                            actionInfo.AppendLine($"{Environment.NewLine}Next sell {plan.Next.Sell:#,##0.000} > current buy {plan.Current.Buy:#,##0.000} therefore top up from {battLevel}% to target {bti.BatteryLevelEnd}%.");
                            battChargeRateWanted = 95;
                        }
                        else
                        {
                            if (chargeFromGridWanted.Start.TimeOfDay <= DateTime.UtcNow.TimeOfDay && chargeFromGridWanted.End.TimeOfDay >= DateTime.UtcNow.TimeOfDay)
                            {
                                chargeFromGridWanted.Enable = false;
                            }
                        }

                        // Set the rate.

                        if (!chargeLastWanted)
                        {
                            if (bti.PredictionBatteryPercent < 200)
                            {
                                actionInfo.AppendLine($" Generation prediction to battery is {bti.PredictionBatteryPercent}% < 200% therefore override battery charge rate from {battChargeRateWanted}% to 99%.");
                                battChargeRateWanted = 99;
                            }
                            else if (generationRecentMax < 2500 && extraPowerNeeded > 0)
                            {
                                actionInfo.AppendLine($" Behind by {extraPowerNeeded:0.0}kW and recent generation max is {generationRecentMax / 1000:0.0}kW therefore override battery charge rate from {battChargeRateWanted}% to 98%.");
                                battChargeRateWanted = 98;
                            }

                            if (bti.ChargeRateNeededHkW > generationRecentMean / 1000 && battChargeRateWanted < bti.ChargeRateNeededHPercent)
                            {
                                actionInfo.AppendLine($" Recent generation is {generationRecentMean / 1000:0.0}kW therefore override battery charge rate from {battChargeRateWanted}% to 97%.");
                                battChargeRateWanted = 97;
                            }

                            if (generationRecentMax < 3600 && battLevel < bti.BatteryTarget && generationMeanDifference < 0)
                            {
                                actionInfo.AppendLine($" Rate of generation is decreasing ({generationMeanDifference:0}W) therefore override battery charge rate from {battChargeRateWanted}% to {(battChargeRateWanted > 40 ? 96 : battChargeRateWanted * 2)}%.");
                                battChargeRateWanted = battChargeRateWanted > 40 ? 96 : battChargeRateWanted * 2;
                            }
                        }
                    }
                    else
                    {
                        // No plan. Set defaults.
                        if (bti.BatteryLevelCurrent >= bti.BatteryLevelEnd)
                        {
                            actionInfo.AppendLine($"No information. Battery level {bti.BatteryLevelCurrent}% is above {bti.BatteryLevelEnd}%. (Current target of {bti.TargetDescription}. )");
                            chargeLastWanted = false;
                            battChargeRateWanted = 71;
                        }
                        else
                        {
                            battChargeRateWanted = 71;
                            chargeLastWanted = Plan.DischargeToGridCondition(plan.Current!);
                            actionInfo.AppendLine($"No information.");

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
                    actions.AppendLine($"  Buy @ {plan.Current.Buy:#,##0.000}.");
                }
            }

            string burstLog = _BurstLog.Read();
            _BurstLog.Clear();

            // Report any changes.
            if (actions.Length > 0 || burstLog.Length > 0)
            {
                if (actionInfo.Length > 0)
                {
                    actions.AppendLine(actionInfo.ToString());
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
