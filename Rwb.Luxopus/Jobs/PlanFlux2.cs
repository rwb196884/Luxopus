using InfluxDB.Client.Core.Flux.Domain;
using Microsoft.Extensions.Logging;
using Rwb.Luxopus.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Jobs
{
    public class BatteryUsageProfile
    {
        private readonly Dictionary<DayOfWeek, Dictionary<int, double>> _DailyHourly;

        public BatteryUsageProfile(IEnumerable<FluxTable> hourly)
        {
            _DailyHourly = hourly.Single().Records.GroupBy(z => z.GetValue<long>("d"))
            .ToDictionary(
                z => (DayOfWeek)Convert.ToInt32(z.Key), 
                z => z.ToDictionary(
                    y => Convert.ToInt32(y.GetValue<long>("h")), 
                    y => y.GetValue<double>("_value")
                )
            );
        }

        public double GetKwkh(DayOfWeek day, int hourFrom, int hourTo)
        {
            if (hourFrom < hourTo)
            {
                return _DailyHourly
                    .Single(z => z.Key == day).Value
                    .Where(z => z.Key >= hourFrom && z.Key < hourTo).Select(z => z.Value).Sum() / 1000.0;
            }
            return _DailyHourly
                .Single(z => z.Key == day).Value
                .Where(z => z.Key >= hourFrom || z.Key < hourTo).Select(z => z.Value).Sum() / 1000.0;
        }

        // TODO: disaggregate by day of week.
    }


    /*
       https://forum.octopus.energy/t/using-the-flux-tariff-with-solar-with-battery-check-my-working/7510/22

    The inverter is not efficient enough to make it economic to buy electricity to sell.
    Besides losses across the inverter (which is likely to be asymmatrical)
    there are chemical losses in the battery.

    ...so...
    Sell all at peak.
    Buy back to use after peak.
    Buy at low to get through to morning and cover any demand tomorrow that can't be satisfied from generation.

     */

    /// <summary>
    /// <para>
    /// Plan for 'flux' tariff https://octopus.energy/smart/flux/
    /// </para>
    /// </summary>
    public class PlanFlux2 : PlanFlux
    {
        private const int BatteryAbsoluteMinimum = 5;
        private const int DischargeAbsoluteMinimum = 18;

        private IBatteryService _Batt;

        public PlanFlux2(ILogger<LuxMonitor> logger, IInfluxQueryService influxQuery, ILuxopusPlanService plan, IEmailService email, IBatteryService batt)
            : base(logger, influxQuery, plan, email)
        {
            _Batt = batt;
        }

        protected override async Task WorkAsync(CancellationToken cancellationToken)
        {
            DateTime t0 = DateTime.UtcNow.AddHours(-3);
            Plan? current = PlanService.Load(t0);
            StringBuilder notes = new StringBuilder();

            List<FluxTable> bupH = await InfluxQuery.QueryAsync(Query.HourlyBatteryUse, t0);
            BatteryUsageProfile bup = new BatteryUsageProfile(bupH);

            DateTime start = t0.StartOfHalfHour().AddDays(-1);
            DateTime stop = (new DateTime(t0.Year, t0.Month, t0.Day, 21, 0, 0)).AddDays(1);
            List<ElectricityPrice> prices = await InfluxQuery.GetPricesAsync(start, stop, "E-1R-FLUX-IMPORT-23-02-14-E", "E-1R-FLUX-EXPORT-23-02-14-E");

            // Find the current period: the last period that starts before t0.
            ElectricityPrice? priceNow = prices.Where(z => z.Start < t0).OrderByDescending(z => z.Start).FirstOrDefault();
            if (priceNow == null)
            {
                Logger.LogError("No current price.");
                return;
            }
            Plan plan = new Plan(prices.Where(z => z.Start >= priceNow.Start));

            foreach (HalfHourPlan p in plan.Plans)
            {
                switch (GetFluxCase(plan, p))
                {
                    case FluxCase.Peak:
                        /*
                         * I will need an electricity later and there is one in the battery.
                         * Should I sell it now at 34p and buy it back later for 33p, or just keep it?
                         * 33/34 => 97% so: keep.
                         * Therefore we need to work out how much to keep to get to the nighttime low.
                         */
                        (DateTime td, long dischargeAchievedYesterday) = (await InfluxQuery.QueryAsync(Query.DischargeAchievedYesterday, t0)).First().FirstOrDefault<long>();
                        (DateTime tm, long batteryLowBeforeChargingYesterday) = (await InfluxQuery.QueryAsync(Query.BatteryLowBeforeCharging, t0)).First().FirstOrDefault<long>();

                        long dischargeToGrid = AdjustLimit(false, dischargeAchievedYesterday, batteryLowBeforeChargingYesterday, BatteryAbsoluteMinimum, DischargeAbsoluteMinimum);
                        notes.AppendLine($"Peak:        dischargeAchievedYesterday: {dischargeAchievedYesterday}");
                        notes.AppendLine($"Peak: batteryLowBeforeChargingYesterday: {batteryLowBeforeChargingYesterday}");
                        notes.AppendLine($"Peak:            BatteryAbsoluteMinimum: {BatteryAbsoluteMinimum}");
                        notes.AppendLine($"Peak:          DischargeAbsoluteMinimum: {DischargeAbsoluteMinimum}");
                        notes.AppendLine($"Peak:                       AdjustLimit: {dischargeToGrid} (not used)");

                        // TODO: user flag to keep back more. Use MQTT?

                        // How much do we need?
                        // Batt absolute min plus use until ~~morning generation~~ the low.
                        HalfHourPlan? dischargeEnd = plan.Plans.GetNext(p);
                        HalfHourPlan? low = plan.Plans.GetNext(p, z => GetFluxCase(plan, z) == FluxCase.Low);
                        if (dischargeEnd != null && low != null)
                        {
                            double hours = (low.Start - dischargeEnd.Start).TotalHours;
                            double kWh = bup.GetKwkh(t0.DayOfWeek, dischargeEnd.Start.Hour, low.Start.Hour);
                            int percentForUse = _Batt.CapacityKiloWattHoursToPercent(kWh);
                            dischargeToGrid = BatteryAbsoluteMinimum + percentForUse;

                            notes.AppendLine($"Peak:           hours: {hours:0.0}");
                            notes.AppendLine($"Peak:             kWh: {kWh:0.0}");
                            notes.AppendLine($"Peak:   percentForUse: {percentForUse}");
                            notes.AppendLine($"Peak: dischargeToGrid: {dischargeToGrid}");
                        }
                        else
                        {
                            notes.AppendLine($"Peak: overnight low not found");
                        }

                        p.Action = new PeriodAction()
                        {
                            ChargeFromGrid = 0,
                            DischargeToGrid = Convert.ToInt32(dischargeToGrid), // We can buy back cheaper before the low. On-grid cut-off is 5. 
                            // TODO: get prices to check that ^^ is true.
                            // TODO: aim for batt at 6% (cutoff is 5%) at 2AM.
                            // MUST NOT buy during peak threfore leave some.
                            //BatteryChargeRate = 0,
                            //BatteryGridDischargeRate = 100,
                            // Selling at peak for 34p * 0.9 = 30p. Day rate to buy is 33p. Therefore MUST NOT BUT before low.
                            // Need about 20% to get over night, therefore estimate 10% to get to low.
                        };
                        break;
                    case FluxCase.Daytime:
                        p.Action = new PeriodAction()
                        {
                            ChargeFromGrid = 0,
                            DischargeToGrid = 100,
                            //BatteryChargeRate = 75,
                            //BatteryGridDischargeRate = 100,
                        };
                        break;
                    case FluxCase.Low:
                        // Hack: If adjust according to yesterday's battery morning low.
                        DateTime tt = t0.Hour < 10 ? t0.AddDays(-1) : t0;
                        (DateTime _, long batteryMorningLow) = (await InfluxQuery.QueryAsync(Query.BatteryMorningLow, tt)).First().FirstOrDefault<long>();
                        (DateTime _, long batteryCharged) = (await InfluxQuery.QueryAsync(Query.BatteryGridChargeHigh, tt)).First().FirstOrDefault<long>();

                        // Compare to yesterday.
                        long chargeFromGrid = AdjustLimit(true, batteryCharged, batteryMorningLow, BatteryAbsoluteMinimum + 1, 20);
                        notes.AppendLine($"Low: AdjustLimit    batteryCharged {batteryCharged}%");
                        notes.AppendLine($"Low: AdjustLimit batteryMorningLow {batteryMorningLow}%");
                        notes.AppendLine($"Low: AdjustLimit    chargeFromGrid {chargeFromGrid}%");
                        notes.AppendLine();

                        // Work it out properly?
                        // How much do we want?
                        HalfHourPlan? next = plan.Plans.GetNext(p);
                        (DateTime startOfGeneration, long _) = (await InfluxQuery.QueryAsync(Query.StartOfGenerationYesterday, t0)).First().FirstOrDefault<long>();
                        double powerRequired = 0.3;
                        if ((next?.Start.Hour ?? p.Start.Hour + 3) < startOfGeneration.Hour)
                        {
                            bup.GetKwkh(t0.DayOfWeek, (next?.Start.Hour ?? p.Start.Hour + 3), startOfGeneration.Hour);
                        }
                        int battRequired = _Batt.CapacityKiloWattHoursToPercent(powerRequired);
                        notes.AppendLine($"Low: AdjustLimit      battRequired {battRequired}%");
                        notes.AppendLine($"Low: AdjustLimit startOfGeneration {startOfGeneration:HH:mm} ");
                        notes.AppendLine($"Low: AdjustLimit     powerRequired {powerRequired:0.0}kWh = bup.GetKwkh({t0.DayOfWeek}, {(next?.Start.Hour ?? p.Start.Hour + 3)}, {startOfGeneration.Hour})");

                        //chargeFromGrid = BatteryAbsoluteMinimum + battRequired;
                        notes.AppendLine($"Low: chargeFromGrid {BatteryAbsoluteMinimum + battRequired} = BatteryAbsoluteMinimum {BatteryAbsoluteMinimum} + battRequired {battRequired}");

                        // Hack.
                        if (chargeFromGrid > 20)
                        {
                            chargeFromGrid = 20;
                        }

                        p.Action = new PeriodAction()
                        {
                            ChargeFromGrid = Convert.ToInt32(chargeFromGrid),
                            DischargeToGrid = 100,
                            //BatteryChargeRate = 100,
                            //BatteryGridDischargeRate = 0,
                        };
                        break;
                }
            }

            PlanService.Save(plan);
            SendEmail(plan, notes.ToString());
        }

        private DateTime GenerationStartTomorrow(DateTime today)
        {
            // TODO: query.
            return today.Date.AddDays(1).AddHours(10);
        }
    }
}
