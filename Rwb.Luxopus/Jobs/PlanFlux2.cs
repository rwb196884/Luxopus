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
                        notes.AppendLine($"       dischargeAchievedYesterday: {dischargeAchievedYesterday}");
                        notes.AppendLine($"batteryLowBeforeChargingYesterday: {batteryLowBeforeChargingYesterday}");
                        notes.AppendLine($"           BatteryAbsoluteMinimum: {BatteryAbsoluteMinimum}");
                        notes.AppendLine($"         DischargeAbsoluteMinimum: {DischargeAbsoluteMinimum}");
                        notes.AppendLine($"                      AdjustLimit: {dischargeToGrid}");

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

                            notes.AppendLine($"          hours: {hours:0.0}");
                            notes.AppendLine($"            kWh: {kWh:0.0}");
                            notes.AppendLine($"  percentForUse: {percentForUse}");
                            notes.AppendLine($"dischargeToGrid: {dischargeToGrid}");
                        }
                        else
                        {
                            notes.AppendLine($"overnight low not found");
                        }

                        // Adjust according to historical use data.
                        if (dischargeToGrid < BatteryAbsoluteMinimum + _Batt.CapacityKiloWattHoursToPercent(bup.GetKwkh(t0.DayOfWeek, 19, 2)))
                        {
                            dischargeToGrid = Convert.ToInt64(Math.Round(BatteryAbsoluteMinimum + bup.GetKwkh(t0.DayOfWeek, 19, 2)));
                            notes.AppendLine($"   BatteryAbsoluteMinimum: {BatteryAbsoluteMinimum}");
                            notes.AppendLine($"              bup.GetKwkh: {bup.GetKwkh(t0.DayOfWeek, 19, 2):0.0}");
                            notes.AppendLine($"          dischargeToGrid: {dischargeToGrid}");
                        }
                        else
                        {
                            notes.AppendLine($"bup.GetKwkh: {bup.GetKwkh(t0.DayOfWeek, 19, 2):0.0} (not used)");
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
                        // Power needed to get to start of solar generation.
                        (DateTime sunrise, long _) = (await InfluxQuery.QueryAsync(Query.Sunrise, p.Start)).First().FirstOrDefault<long>();
                        HalfHourPlan? next = plan.Plans.GetNext(p);
                        double hoursWhileSunrise = (sunrise - (next?.Start ?? p.Start.Date.AddHours(5 /* FLUX low usually ends at 5AM UTC */))).TotalHours;
                        double nighttimeConsumption = 0.2; // TODO: query.
                        double kWhNeeded = hoursWhileSunrise * nighttimeConsumption;
                        // TODO: How long after sunrise does generation start? Depending on UVI/solcast?

                        // Hack: If adjust according to yesterday's battery morning low.
                        DateTime tt = t0.Hour < 10 ? t0.AddDays(-1) : t0;
                        (DateTime _, long batteryMorningLow) = (await InfluxQuery.QueryAsync(Query.BatteryMorningLow, tt)).First().FirstOrDefault<long>();
                        (DateTime _, long batteryCharged) = (await InfluxQuery.QueryAsync(Query.BatteryGridChargeHigh, tt)).First().FirstOrDefault<long>();

                        // How much do we want?
                        (DateTime startOfGeneration, long _) = (await InfluxQuery.QueryAsync(Query.StartOfGenerationYesterday, t0)).First().FirstOrDefault<long>();
                        double powerRequired = bup.GetKwkh(t0.DayOfWeek, p.Start.Hour, startOfGeneration.Hour);
                        int battRequired = _Batt.CapacityKiloWattHoursToPercent(powerRequired);

                        long chargeFromGrid = AdjustLimit(true, batteryCharged, batteryMorningLow, battRequired, 20);
                        notes.AppendLine($"Low: AdjustLimit    batteryCharged {batteryCharged}");
                        notes.AppendLine($"Low: AdjustLimit batteryMorningLow {batteryMorningLow}");
                        notes.AppendLine($"Low: AdjustLimit startOfGeneration {startOfGeneration:HH:mm} ");
                        notes.AppendLine($"Low: AdjustLimit     powerRequired {powerRequired} = bup.GetKwkh(t0.DayOfWeek, {p.Start.Hour}, {startOfGeneration.Hour})");
                        notes.AppendLine($"Low: AdjustLimit      battRequired {battRequired}");
                        notes.AppendLine($"Low: AdjustLimit    chargeFromGrid {chargeFromGrid}");

                        //chargeFromGrid = BatteryAbsoluteMinimum + battRequired;
                        notes.AppendLine($"Low: chargeFromGrid {chargeFromGrid} = BatteryAbsoluteMinimum {BatteryAbsoluteMinimum} + battRequired {battRequired}");

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
