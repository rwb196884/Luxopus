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
    /// Check that plans are running. Simple version: look only at the current period.
    /// </para>
    /// </summary>
    public class GenerationLimiter : Job
    {
        private readonly ILuxopusPlanService _Plans;
        private readonly ILuxService _Lux;
        private readonly IInfluxQueryService _InfluxQuery;
        private readonly IEmailService _Email;
        private readonly IBatteryService _Batt;

        private const int _BatteryUpperLimit = 97;
        private const int _InverterLimit = 3700;

        public GenerationLimiter(ILogger<LuxMonitor> logger, ILuxopusPlanService plans, ILuxService lux, IInfluxQueryService influxQuery, IEmailService email, IBatteryService batt)
            : base(logger)
        {
            _Plans = plans;
            _Lux = lux;
            _InfluxQuery = influxQuery;
            _Email = email;
            _Batt = batt;
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
            // Suggested cron: * 10-16 * * *

            DateTime t0 = DateTime.UtcNow;
            IEnumerable<Plan> ps = _Plans.LoadAll(t0);

            Plan? plan = _Plans.Load(t0);
            if (plan == null || plan.Next == null)
            {
                Logger.LogError($"No plan at UTC {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")}.");
                // If there is plan then default configuration will be set.
            }

            HalfHourPlan? currentPeriod = plan?.Current;

            if (currentPeriod == null || currentPeriod.Action == null)
            {
                Logger.LogError($"No current plan at UTC {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")}.");
                currentPeriod = new HalfHourPlan()
                {
                    Action = new PeriodAction() // Use the default values.
                };
            }

            if ( currentPeriod.Action.ChargeFromGrid > 0 || currentPeriod.Action.DischargeToGrid < 100)
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
            (DateTime _, long batteryLowBeforeCharging) = (await _InfluxQuery.QueryAsync(Query.BatteryLowBeforeCharging, t0)).First().FirstOrDefault<long>();

            StringBuilder actions = new StringBuilder();

            Dictionary<string, string> settings = await _Lux.GetSettingsAsync();
            while (settings.Any(z => z.Value == "DATAFRAME_TIMEOUT"))
            {
                settings = await _Lux.GetSettingsAsync();
            }
            int battChargeRate = _Lux.GetBatteryChargeRate(settings);

            // Batt charge.
            int requiredBattChargeRate = battChargeRate; // No change.

            string runtimeInfo = await _Lux.GetInverterRuntimeAsync();

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
                    if( generation - 50 > battCharge + inverterOutput)
                    {
                        // We don't know what could be generated so we guess +8.
                        requiredBattChargeRate = battChargeRate + 8;
                    }
                    else
                    {
                        requiredBattChargeRate = _Batt.TransferKiloWattsToPercent((generation - 3600.0)/1000.0);
                    }

                    actions.AppendLine($"Inverter output is {inverterOutput}W; changing battery charge rate from {battChargeRate}% to {requiredBattChargeRate}%.");
                }
                else
                {
                    int battMaxW = generation - 3600;

                    // Set standard schedule.
                    // Get fully charged before the discharge period.

                    // Plan A
                    double hoursToCharge = (plan!.Next!.Start - t0).TotalHours;
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

                    double kW = (powerRequiredKwh + extraPowerNeeded) / hoursToCharge;
                    int b = _Batt.TransferKiloWattsToPercent(kW);

                    // Set the rate.
                    requiredBattChargeRate = _Batt.RoundPercent(b);
                    actions.AppendLine($"{powerRequiredKwh:0.0}kWh needed to get from {battLevel}% to {_BatteryUpperLimit}% in {hoursToCharge:0.0} hours until {plan.Next.Start:HH:mm} (mean rate {kW:0.0}kW).");
                }
            }

            // Report any changes.
            if (requiredBattChargeRate != battChargeRate)
            {
                _Lux.SetBatteryChargeRateAsync(requiredBattChargeRate);
                //_Email.SendEmail($"GenerationLimiter {DateTime.UtcNow.ToString("dd MMM HH:mm")}", actions.ToString());
                Logger.LogInformation("PlanChecker made changes: " + Environment.NewLine + actions.ToString());
            }
        }
    }
}
