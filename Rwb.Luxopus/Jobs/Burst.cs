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
        private readonly IEmailService _Email;
        private readonly IBatteryService _Batt;

        public Burst(
            ILogger<Burst> logger,
            IBurstLogService burstLog,
            ILuxopusPlanService plans,
            ILuxService lux,
            IInfluxQueryService influxQuery,
            IEmailService email,
            IBatteryService batt)
            : base(logger)
        {
            _BurstLog = burstLog;
            _Plans = plans;
            _Lux = lux;
            _InfluxQuery = influxQuery;
            _Email = email;
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
            if (t0 < sunrise || t0 > sunset)
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
            // We're good to go...

            // Generation start and end. Guess from yesterday.
            DateTime gStart = sunrise;
            DateTime gEnd = sunset;
            gStart = sunrise;
            gEnd = sunset;
            (gStart, _) = (await _InfluxQuery.QueryAsync(Query.StartOfGeneration, currentPeriod.Start)).First().FirstOrDefault<double>();
            (gEnd, _) = (await _InfluxQuery.QueryAsync(Query.EndOfGeneration, currentPeriod.Start)).First().FirstOrDefault<double>();

            StringBuilder actions = new StringBuilder();
            StringBuilder actionInfo = new StringBuilder();

            Dictionary<string, string> settings = await _Lux.GetSettingsAsync();
            while (settings.Any(z => z.Value == "DATAFRAME_TIMEOUT"))
            {
                settings = await _Lux.GetSettingsAsync();
            }
            int battChargeRate = _Lux.GetBatteryChargeRate(settings);
            int battChargeRateWanted = battChargeRate; // No change.

            (bool outEnabled, DateTime outStart, DateTime outStop, int outBatteryLimitPercent) = _Lux.GetDischargeToGrid(settings);
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

                double powerNeeded = 0;
                if (battLevelTarget < battLevel)
                {
                    powerNeeded = _Batt.CapacityPercentToKiloWattHours(battLevelTarget - battLevel);
                }

                    actionInfo.AppendLine($"Generation: {generation}W");
                actionInfo.AppendLine($"Inverter output: {inverterOutput}W");
                actionInfo.AppendLine($"Battery level: {battLevel}%");
                actionInfo.AppendLine($"Battery level target: {battLevelTarget}%; behind by {powerNeeded:#,##0.0}kWh.");

                // Plan A
                double hoursToCharge = (plan.Next.Start - t0).TotalHours;
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
                int battChargeRatePlan = _Batt.RoundPercent(b);

                if (generation > 3600)
                {
                    // Manage the limit.
                    if (inverterOutput < 3300)
                    {
                        // Can export more.
                        battChargeRateWanted = _Batt.TransferKiloWattsToPercent(Convert.ToDouble(generation - 3600) / 1000.0);
                        // It seems to over-estimate. In this case we expect to decrease.
                        if (battChargeRateWanted >= battChargeRate)
                        {
                            battChargeRateWanted = battChargeRate - 5;
                            actionInfo.AppendLine($"Battery charge rate {battChargeRateWanted}% = {battChargeRate}% - 5%.");
                        }
                        else
                        {
                            actionInfo.AppendLine($"Battery charge rate: {generation}W - 3600W -> {battChargeRateWanted}%.");
                        }
                    }
                    else if (inverterOutput > 3500)
                    {
                        int battChargeActual = _Batt.TransferKiloWattsToPercent(Convert.ToDouble(battCharge) / 1000.0);
                        int forBatt = 200 + generation - inverterOutput;
                        battChargeRateWanted = _Batt.TransferKiloWattsToPercent(Convert.ToDouble(forBatt) / 1000.0);

                        actionInfo.AppendLine($"{generation}W + 200W - {inverterOutput}W = {forBatt}W -> {battChargeRateWanted}%");
                    }
                }
                else if (generation > 2700 && inverterOutput > 3200 && inverterOutput < 3700)
                {
                    // Generation could be limited.
                    battChargeRateWanted = _Batt.TransferKiloWattsToPercent(Convert.ToDouble(generation + 200 - 3600) / 1000.0);
                    if (battChargeRateWanted <= battChargeRate)
                    {
                        battChargeRateWanted = battChargeRate + 5;
                        actionInfo.AppendLine($"Generation {generation} > 2700 and 3200 < inverterOutput:{inverterOutput} < 3700 therefore generation could be limited.");
                    }
                }
                else if (t0.Hour <= 10 && generationMax > 1000 && battLevel > battLevelTarget - 5)
                {
                    // It's early and it looks like it's going to be a good day.
                    // So keep the battery empty to make space for later.
                    battChargeRateWanted = 8;
                    actionInfo.AppendLine($"Generation peak of {generationMax} before 11AM UTC suggests that it could be a good day. Battery level {battLevel}, target of {battLevelTarget} therefore keep some space.");
                    outBatteryLimitPercentWanted = battLevelTarget - 5;
                }

                if (battChargeRateWanted < battChargeRatePlan)
                {
                    battChargeRateWanted = battChargeRatePlan;
                    string s = battLevelTarget != battLevel ? $" (should be {battLevelTarget}%)" : "";
                    actionInfo.AppendLine($"{powerRequiredKwh:0.0}kWh needed to get from {battLevel}%{s} to {_Batt.BatteryLimit}% in {hoursToCharge:0.0} hours until {plan.Next.Start:HH:mm} (mean rate {kW:0.0}kW -> {battChargeRatePlan}%).");
                }
            }

            if (battChargeRateWanted > 71)
            {
                actionInfo.AppendLine($"Battery charge rate wanted {battChargeRateWanted} reduced to 71%.");
                battChargeRateWanted = 71;
            }
            else if (battChargeRateWanted < 5)
            {
                actionInfo.AppendLine($"Battery charge rate wanted {battChargeRateWanted} increased to 5%.");
                battChargeRateWanted = 5;
            }

            if (battChargeRateWanted != battChargeRate)
            {
                await _Lux.SetBatteryChargeRateAsync(battChargeRateWanted);
                actions.AppendLine($"SetBatteryChargeRate({battChargeRateWanted}) was {battChargeRate}.");
            }

            if (outEnabled && outBatteryLimitPercentWanted < 100 && outBatteryLimitPercent != outBatteryLimitPercentWanted)
            {
                await _Lux.SetDischargeToGridLevelAsync(outBatteryLimitPercentWanted);
                actions.AppendLine($"SetDischargeToGridLevelAsync({outBatteryLimitPercentWanted}) was {outBatteryLimitPercent}.");
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
