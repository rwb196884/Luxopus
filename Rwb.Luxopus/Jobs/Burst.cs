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
        private const int _InverterLimit = 3700;

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
            // Suggested cron: * 10-16 * * *

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

                if (inverterOutput > 3600)
                {
                    // Out put could be seturated: swich to burst mode.
                    // We don't know what could be generated so we guess +8.
                    battChargeRateWanted = battChargeRate < 71 ? 71 : battChargeRate + 8;
                    if (battChargeRateWanted > 97)
                    {
                        battChargeRateWanted = 97;
                    }
                    outStartWanted = plan.Current.Start;
                    outStopWanted = plan.Next.Start;
                    outBatteryLimitPercentWanted = Convert.ToInt32(Math.Max(battLevel, battLevelNow)) + 5;
                    if(outBatteryLimitPercentWanted > 95)
                    {
                        outBatteryLimitPercentWanted = 95;
                    }

                    actions.AppendLine($"Set generation burst mode with export battery level limit of {outBatteryLimitPercentWanted}%.");
                }
                else if (outEnabled) // If not enabled then we weren't allowing burst.
                {
                    // Revert from burst mode.
                    outBatteryLimitPercentWanted = 100;

                    // Set standard schedule.
                    // Get fully charged before the discharge period.

                    // Plan A
                    double hoursToCharge = (plan!.Next!.Start - t0).TotalHours;
                    double powerRequiredKwh = _Batt.CapacityPercentToKiloWattHours(_BatteryUpperLimit - battLevel);
                    double kW = (powerRequiredKwh * 1.2) / hoursToCharge;
                    int b = _Batt.TransferKiloWattsToPercent(kW);

                    // Set the rate.
                    battChargeRateWanted = _Batt.RoundPercent(b);

                    actions.AppendLine($"Disable generation busrt mode. {powerRequiredKwh:0.0}kWh needed to get from {battLevel}% to {_BatteryUpperLimit}% in {hoursToCharge:0.0} hours until {plan.Next.Start:HH:mm} (mean rate {kW:0.0}kW).");
                }
                else
                {
                    return;
                }
            }

            bool changes = false;

            if (outStart != outStartWanted)
            {
                await _Lux.SetDischargeToGridStartAsync(outStartWanted);
                changes = true;
            }

            if (outStop != outStopWanted)
            {
                await _Lux.SetDischargeToGridStopAsync(outStopWanted);
                changes = true;
            }

            if (outEnabled && outBatteryLimitPercent != outBatteryLimitPercentWanted)
            {
                await _Lux.SetDischargeToGridLevelAsync(outBatteryLimitPercentWanted);
                changes = true;
            }

            if (battChargeRateWanted != battChargeRate)
            {
                await _Lux.SetBatteryChargeRateAsync(battChargeRateWanted);
                changes = true;
            }

            // Report any changes.
            if (changes)
            {
                //_Email.SendEmail($"Burst {DateTime.UtcNow.ToString("dd MMM HH:mm")}", actions.ToString());
                Logger.LogInformation("Burst made changes: " + Environment.NewLine + actions.ToString());
            }
        }
    }
}
