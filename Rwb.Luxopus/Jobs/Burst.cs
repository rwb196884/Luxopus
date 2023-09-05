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
        private readonly ILuxopusPlanService _Plans;
        private readonly ILuxService _Lux;
        private readonly IInfluxQueryService _InfluxQuery;
        private readonly IEmailService _Email;
        private readonly IBatteryService _Batt;

        private const int _BatteryUpperLimit = 97;
        private const int _InverterLimit = 3600;
        private const int _BatteryChargeMaxRate = 4000;

        public Burst(ILogger<LuxMonitor> logger, ILuxopusPlanService plans, ILuxService lux, IInfluxQueryService influxQuery, IEmailService email, IBatteryService batt)
            : base(logger)
        {
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

            (DateTime sunrise, long _) = (await _InfluxQuery.QueryAsync(Query.Sunrise, currentPeriod.Start)).First().FirstOrDefault<long>();
            (DateTime sunset, long _) = (await _InfluxQuery.QueryAsync(Query.Sunset, currentPeriod.Start)).First().FirstOrDefault<long>();
            if (t0 < sunrise || t0 > sunset)
            {
                return;
            }

            (DateTime _, long generationMax) = (await _InfluxQuery.QueryAsync(@$"
            from(bucket: ""solar"")
              |> range(start: {plan.Current.Start.ToString("yyyy-MM-ddTHH:mm:00Z")}, stop: now())
              |> filter(fn: (r) => r[""_measurement""] == ""inverter"" and r[""_field""] == ""generation"")
              |> max()")).First().FirstOrDefault<long>();

            if(generationMax < 2500)
            {
                return;
            }

            // We're good to go...
            StringBuilder actions = new StringBuilder();

            Dictionary<string, string> settings = await _Lux.GetSettingsAsync();
            while (settings.Any(z => z.Value == "DATAFRAME_TIMEOUT"))
            {
                settings = await _Lux.GetSettingsAsync();
            }
            int battChargeRate = _Lux.GetBatteryChargeRate(settings);
            int battChargeRateWanted = battChargeRate; // No change.

            string runtimeInfo = await _Lux.GetInverterRuntimeAsync();

            DateTime tBattChargeFrom = plan.Current.Start < sunrise ? sunrise : plan.Current.Start;

            int battLevelStart = await _InfluxQuery.GetBatteryLevelAsync(plan.Current.Start);
            double battLevelNow = 
                Convert.ToDouble(100 - battLevelStart) 
                * (DateTime.UtcNow.Subtract(tBattChargeFrom).TotalMinutes + 30)
                / plan.Next.Start.Subtract(tBattChargeFrom).TotalMinutes;

            using (JsonDocument j = JsonDocument.Parse(runtimeInfo))
            {
                JsonElement.ObjectEnumerator r = j.RootElement.EnumerateObject();
                int generation = r.Single(z => z.Name == "ppv").Value.GetInt32();
                int export = r.Single(z => z.Name == "pToGrid").Value.GetInt32();
                int inverterOutput = r.Single(z => z.Name == "pinv").Value.GetInt32();
                int battLevel = r.Single(z => z.Name == "soc").Value.GetInt32();
                int battCharge = r.Single(z => z.Name == "pCharge").Value.GetInt32();
                //int battDisharge = r.Single(z => z.Name == "pDisharge").Value.GetInt32();

                if(generation > 3600)
                {
                    // Manage the limit.
                    if( inverterOutput < 3200)
                    {
                        // Can export more.
                        battChargeRateWanted = _Batt.TransferKiloWattsToPercent(Convert.ToDouble(generation - 3600) / 1000.0);
                        battChargeRateWanted = battChargeRateWanted <= battChargeRate ? battChargeRate + 5 : battChargeRateWanted;
                    }
                    else if( inverterOutput > 3500)
                    {
                        // Could be limited.
                        if(battChargeRate > 41)
                        {
                            battChargeRateWanted += 5;
                        }
                        else if( battCharge > 0 && battCharge < 41)
                        {
                            battChargeRateWanted += 5;
                        }
                    }
                }
                else if (generation > 2700 && inverterOutput > 3000 && inverterOutput < 3700)
                {
                    // Generation could be limited.
                    battChargeRateWanted = _Batt.TransferKiloWattsToPercent(Convert.ToDouble(generation + 200 - 3600) / 1000.0);
                    if(battChargeRateWanted <= battChargeRate)
                    {
                        battChargeRateWanted = battChargeRate + 5;
                    }
                }
                else
                {
                    // Plan A
                    double hoursToCharge = (plan.Next.Start - t0).TotalHours;
                    double powerRequiredKwh = _Batt.CapacityPercentToKiloWattHours(_BatteryUpperLimit - battLevel);

                    // Are we behind schedule?
                    double extraPowerNeeded = 0.0;
                    double powerAtPreviousRate = hoursToCharge * _Batt.TransferPercentToKiloWatts(battChargeRate);
                    if (powerAtPreviousRate < powerRequiredKwh)
                    {
                        // We didn't get as much as we thought, so now we need to make up for it.
                        extraPowerNeeded = 2 * (powerRequiredKwh - powerAtPreviousRate);
                        // Two: one for the past period, and one for this period just in case.
                    }
                    else if (battLevel < battLevelNow)
                    {
                        extraPowerNeeded = 2 * _Batt.CapacityPercentToKiloWattHours(Convert.ToInt32(battLevelNow) - battLevel);
                    }

                    double kW = (powerRequiredKwh + extraPowerNeeded) / hoursToCharge;
                    int b = _Batt.TransferKiloWattsToPercent(kW);

                    // Set the rate.
                    battChargeRateWanted = _Batt.RoundPercent(b);
                    string s = battLevelNow != battLevel ? $" (should be {battLevelNow}%)" : "";
                    actions.AppendLine($"{powerRequiredKwh:0.0}kWh needed to get from {battLevel}%{s} to {_BatteryUpperLimit}% in {hoursToCharge:0.0} hours until {plan.Next.Start:HH:mm} (mean rate {kW:0.0}kW).");
                }
            }

            if (battChargeRateWanted > 71)
            {
                battChargeRateWanted = 71;
            }
            else if( battChargeRateWanted < 10)
            {
                battChargeRateWanted = 8;
            }

            if (battChargeRateWanted != battChargeRate)
            {
                await _Lux.SetBatteryChargeRateAsync(battChargeRateWanted);
                actions.AppendLine($"SetBatteryChargeRate({battChargeRateWanted}) was {battChargeRate}.");
            }

            // Report any changes.
            if (actions.Length > 0)
            {
                //_Email.SendEmail($"Burst {DateTime.UtcNow.ToString("dd MMM HH:mm")}", actions.ToString());
                Logger.LogInformation("Burst made changes: " + Environment.NewLine + actions.ToString());
            }
        }
    }
}
