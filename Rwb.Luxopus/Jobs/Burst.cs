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

            (bool outEnabled, DateTime outStart, DateTime outStop, int outBatteryLimitPercent) = _Lux.GetDischargeToGrid(settings);
            bool outEnabledWanted = outEnabled;
            DateTime outStartWanted = outStart;
            DateTime outStopWanted = outStop;
            int outBatteryLimitPercentWanted = outBatteryLimitPercent;

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

                if(generation > 4000)
                {
                    // Manage the limit.
                    if( inverterOutput < 3000)
                    {
                        // Can export more.
                        battChargeRateWanted = _Batt.TransferKiloWattsToPercent(Convert.ToDouble(generation - 3600) / 1000.0);
                    }
                    else if( inverterOutput > 3500)
                    {
                        // Could be limited.
                        if(battChargeRate > 41)
                        {
                            battChargeRateWanted += 5;
                        }
                        else
                        {
                            battChargeRateWanted = 41;
                        }
                    }

                    outEnabledWanted = false;
                }
                else if (generation > 2700 && inverterOutput > 3000 && inverterOutput < 3700)
                {
                    // Generation could be limited.
                    battChargeRateWanted = battChargeRate < 41 ? 41 : battChargeRate + 5;
                    outEnabledWanted = false;

                    battChargeRateWanted = _Batt.TransferKiloWattsToPercent(Convert.ToDouble(generation + 200 - 3600) / 1000.0);

                }
                else if ( generation < 2500 && battLevel > battLevelNow + 5)
                {
                    // Can we discharge some over-generation?
                    // Output could be seturated: swich to burst mode.
                    // We don't know what could be generated so we guess +8.
                    battChargeRateWanted = battChargeRate < 71 ? 71 : battChargeRate + 8;
                    if (battChargeRateWanted > 97)
                    {
                        battChargeRateWanted = 97;
                    }
                    outEnabledWanted = true;
                    outStartWanted = plan.Current.Start;
                    outStopWanted = plan.Next.Start;
                    outBatteryLimitPercentWanted = Convert.ToInt32(battLevelNow);
                    if(outBatteryLimitPercentWanted > 95)
                    {
                        outBatteryLimitPercentWanted = 95;
                    }

                    actions.AppendLine($"Set generation burst mode with export battery level limit of {outBatteryLimitPercentWanted}%.");
                }
                else
                {
                    return;
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
