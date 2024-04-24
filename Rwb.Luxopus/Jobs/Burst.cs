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
    /// </para>
    /// </summary>
    public class Burst : Job
    {
        private readonly IBurstLogService _BurstLog;
        private readonly ILuxopusPlanService _Plans;
        private readonly ILuxService _Lux;
        private readonly IInfluxQueryService _InfluxQuery;
        private readonly IBatteryService _Batt;

        public Burst(
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
            IEnumerable<Plan> ps = _Plans.LoadAll(t0);

            Plan? plan = _Plans.Load(t0);
            if (plan == null || plan.Next == null)
            {
                Logger.LogError($"No plan at UTC {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")}.");
                // If there is plan then default configuration will be set.
                return;
            }

            HalfHourPlan? currentPeriod = plan?.Current;

            if (currentPeriod == null || currentPeriod.Action == null)
            {
                Logger.LogError($"No current plan at UTC {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")}.");
                return;
            }

            if (currentPeriod.Action.ChargeFromGrid > 0 || currentPeriod.Action.DischargeToGrid < 100)
            {
                return;
            }

            (DateTime sunrise, _) = (await _InfluxQuery.QueryAsync(Query.Sunrise, currentPeriod.Start)).First().FirstOrDefault<long>();
            (DateTime sunset, _) = (await _InfluxQuery.QueryAsync(Query.Sunset, currentPeriod.Start)).First().FirstOrDefault<long>();
            if (t0 < sunrise || t0 > sunset) { return; }

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

            // Generation start and end. Guess from yesterday.
            DateTime gStart = sunrise;
            DateTime gEnd = sunset;
            gStart = sunrise;
            gEnd = sunset;
            (gStart, _) = (await _InfluxQuery.QueryAsync(Query.StartOfGeneration, currentPeriod.Start)).First().FirstOrDefault<double>();
            (gEnd, _) = (await _InfluxQuery.QueryAsync(Query.EndOfGeneration, currentPeriod.Start)).First().FirstOrDefault<double>();
            if (t0 < gStart || t0 > gEnd) { return; }

            StringBuilder actions = new StringBuilder();
            StringBuilder actionInfo = new StringBuilder();

            Dictionary<string, string> settings = await _Lux.GetSettingsAsync();
            while (settings.Any(z => z.Value == "DATAFRAME_TIMEOUT"))
            {
                settings = await _Lux.GetSettingsAsync();
            }
            int battChargeRate = _Lux.GetBatteryChargeRate(settings);
            int battChargeRateWanted = battChargeRate; // No change.
            int battChargeRateNeeded = battChargeRate;

            (bool outEnabled, DateTime outStart, DateTime outStop, int outBatteryLimitPercent) = _Lux.GetDischargeToGrid(settings);
            bool outEnabledWanted = outEnabled;
            int outBatteryLimitPercentWanted = outBatteryLimitPercent;

            string runtimeInfo = await _Lux.GetInverterRuntimeAsync();

            DateTime tBattChargeFrom = plan.Current.Start < sunrise ? sunrise : plan.Current.Start;
            tBattChargeFrom = tBattChargeFrom < gStart ? gStart : tBattChargeFrom;

            int battLevelStart = await _InfluxQuery.GetBatteryLevelAsync(plan.Current.Start);
            DateTime nextPlanCheck = DateTime.UtcNow.Minute > 30  //Check if mins are greater than 30
               ? DateTime.UtcNow.AddHours(1).AddMinutes(-DateTime.UtcNow.Minute) // After half past so go to the next hour.
               : DateTime.UtcNow.AddMinutes(30 - DateTime.UtcNow.Minute); // Before half past so go to half past.
            int battLevelTarget = Scale.Apply(tBattChargeFrom, gEnd < plan.Next.Start ? gEnd : plan.Next.Start, nextPlanCheck, battLevelStart, _Batt.BatteryLimit, ScaleMethod.FastLinear);

            using (JsonDocument j = JsonDocument.Parse(runtimeInfo))
            {
                JsonElement.ObjectEnumerator r = j.RootElement.EnumerateObject();
                int generation = r.Single(z => z.Name == "ppv").Value.GetInt32();
                int export = r.Single(z => z.Name == "pToGrid").Value.GetInt32();
                int inverterOutput = r.Single(z => z.Name == "pinv").Value.GetInt32();
                int battLevel = r.Single(z => z.Name == "soc").Value.GetInt32();
                int battCharge = r.Single(z => z.Name == "pCharge").Value.GetInt32();
                //int battDisharge = r.Single(z => z.Name == "pDisharge").Value.GetInt32();

                actionInfo.AppendLine($"     Generation: {generation}W");
                actionInfo.AppendLine($"Inverter output: {inverterOutput}W");
                actionInfo.AppendLine($"  Battery level: {battLevel}%");
                actionInfo.AppendLine($" Battery target: {battLevelTarget}%");

                // Plan A
                double hoursToCharge = (gEnd - t0).TotalHours;
                double powerRequiredKwh = _Batt.CapacityPercentToKiloWattHours(_Batt.BatteryLimit - battLevel);

                // Are we behind schedule?
                double extraPowerNeeded = 0.0;
                if (battLevel < battLevelTarget)
                {
                    extraPowerNeeded = _Batt.CapacityPercentToKiloWattHours(battLevelTarget - battLevel);
                    actionInfo.AppendLine($"Behind by {extraPowerNeeded:#,##0.0}kWh.");
                }
                else if(battLevelTarget < battLevel)
                {
                    double a = _Batt.CapacityPercentToKiloWattHours(battLevel - battLevelTarget);
                    actionInfo.AppendLine($"Ahead by {a:#,##0.0}kWh.");
                }

                double kW = (powerRequiredKwh + extraPowerNeeded) / hoursToCharge;
                battChargeRateNeeded = _Batt.RoundPercent(_Batt.CapacityKiloWattHoursToPercent(kW));

                long generationRecentMax = (await _InfluxQuery.QueryAsync(@$"
from(bucket: ""solar"")
  |> range(start: -45m, stop: now())
  |> filter(fn: (r) => r[""_measurement""] == ""inverter"" and r[""_field""] == ""generation"")
  |> max()")
   ).First().Records.First().GetValue<long>();

                if (generation > 2700)
                {
                    outEnabledWanted = false;

                    if (inverterOutput > 3200)
                    {
                        // Generation could be limited therefore send more to battery.
                        battChargeRateWanted = _Batt.TransferKiloWattsToPercent(Convert.ToDouble(generation + 400 - 3200) / 1000.0);
                        if (battChargeRateWanted >= battChargeRate)
                        {
                            battChargeRateWanted = battChargeRate + 5;
                            actionInfo.AppendLine($"Generation {generation} > 2700 and 3200 < inverterOutput:{inverterOutput} therefore generation could be limited.");
                        }
                    }
                    else
                    {
                        // Generation probably not limited therefore send less to battery.
                        if(battLevel >= battLevelTarget)
                        {
                            battChargeRateWanted = battChargeRate - 5;
                            actionInfo.AppendLine($"Battery charge rate {battChargeRateWanted}% = {battChargeRate}% - 5% because ahead of target.");
                        }
                        else if(battLevel < battLevelTarget)
                        {
                            battChargeRateWanted = battChargeRate + 5;
                            actionInfo.AppendLine($"Battery charge rate {battChargeRateWanted}% = {battChargeRate}% + 5% because behind target.");
                        }
                    }
                }
                else
                {
                    // Low generation.
                    if (t0.Hour <= 9 && generationMax > 1000 && battLevel > battLevelTarget - 5)
                    {
                        // It's early and it looks like it's going to be a good day.
                        // So keep the battery empty to make space for later.
                        battChargeRateWanted = 8;
                        actionInfo.AppendLine($"Generation peak of {generationMax} before 10AM UTC suggests that it could be a good day. Battery level {battLevel}, target of {battLevelTarget} therefore keep some space.");
                    }
                    else if (generationMax > 4000 && generationRecentMax > 3000 && inverterOutput < 3000 && battLevel > battLevelTarget + 2)
                    {
                        // It's gone quiet but it might get busy again: try to discharge some over-charge.
                        outBatteryLimitPercentWanted = battLevelTarget - 2;
                        outEnabledWanted = true;
                        actionInfo.AppendLine($"Generation peak of {generationMax} recent {generationRecentMax} but currently {generation}. Battery level {battLevel}, target of {battLevelTarget} therefore take opportunity to discharge.");
                    }
                }

                if (battChargeRateWanted < battChargeRate && battLevel < battLevelTarget)
                {
                    string s = battLevelTarget != battLevel ? $" (should be {battLevelTarget}%)" : "";
                    actionInfo.AppendLine($"{kW:0.0}kWh needed to get from {battLevel}%{s} to {_Batt.BatteryLimit}% in {hoursToCharge:0.0} hours until {gEnd:HH:mm} (mean rate {kW:0.0}kW -> {battChargeRateWanted}%). But current setting is {battChargeRate}% therefore not changed.");
                    battChargeRateWanted = battChargeRate;
                }
            }

            // Apply any changes.

            if (outEnabled && !outEnabledWanted)
            {
                actionInfo.AppendLine($"Discharge to grid disabled because battery needed to store generation.");
                await _Lux.SetDischargeToGridLevelAsync(100);
            }
            
            if( outEnabledWanted && ( !outEnabled || outBatteryLimitPercentWanted != outBatteryLimitPercent ))
            {
                bool o = false;
                if (outStart != currentPeriod.Start) { await _Lux.SetDischargeToGridStartAsync(currentPeriod.Start); o = true; }
                if (outStop != DateTime.UtcNow.AddMinutes(30)) { await _Lux.SetDischargeToGridStopAsync(DateTime.UtcNow.AddMinutes(30)); o = true; }
                if (outBatteryLimitPercentWanted != outBatteryLimitPercent) { await _Lux.SetDischargeToGridLevelAsync(outBatteryLimitPercentWanted); o = true; }
                if (o) { 
                    await _Lux.SetBatteryDischargeToGridRateAsync(90);
                    actionInfo.AppendLine("Discharge to grid enabled.");
                }
            }

            if (battChargeRateWanted < battChargeRateNeeded)
            {
                actionInfo.AppendLine($"Battery charge rate wanted {battChargeRateWanted} increased to {battChargeRateNeeded}% needed.");
                battChargeRateWanted = battChargeRateNeeded;
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
