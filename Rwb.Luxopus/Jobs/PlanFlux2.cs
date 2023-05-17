using InfluxDB.Client.Core.Flux.Domain;
using Microsoft.Extensions.Logging;
using Rwb.Luxopus.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Jobs
{
    public class BatteryUsageProfile
    {
        private readonly IEnumerable<FluxTable> _Data;
        public BatteryUsageProfile(IEnumerable<FluxTable> batteryUsageProfileQueryResult)
        {
            _Data = batteryUsageProfileQueryResult;
        }
        public double GetMean()
        {
            return _Data.Single(z => z.Records.Any(y => y.GetValue<string>("result") == "mean")).Records.First().GetValue<double>();
        }

        public double GetMean(DayOfWeek weekDay)
        {
            // Thankfully, WeekDay.Sunday is zero which coincides with InfluxDB.
            return _Data.Single(z => z.Records.Any(y => y.GetValue<string>("result") == "daily-mean" && y.GetValue<long>("table") == (long)weekDay)).Records.First().GetValue<double>();
        }
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
            DateTime t0 = DateTime.UtcNow;
            Plan? current = PlanService.Load(t0);

            List<FluxTable> bupQ = await InfluxQuery.QueryAsync(Query.BatteryUsageProfile, t0);
            BatteryUsageProfile bup = new BatteryUsageProfile(bupQ);
            double batteryUseProfileDayMean = bup.GetMean(t0.DayOfWeek);

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

                        // TODO: user flag to keep back more. Use MQTT?

                        // Adjust according to historical use data.
                        if (dischargeToGrid < BatteryAbsoluteMinimum + batteryUseProfileDayMean)
                        {
                            dischargeToGrid = Convert.ToInt64(Math.Round(BatteryAbsoluteMinimum + batteryUseProfileDayMean));
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
                        double hoursUntilGeneration = (startOfGeneration.AddDays(1).TimeOfDay - p.Start.TimeOfDay).TotalHours;
                        double powerRequired = 0.2 * hoursUntilGeneration; // TODO: estimate demand that cannot be satisfied from generation.
                        int battRequired = _Batt.CapacityKiloWattHoursToPercent(powerRequired);

                        long chargeFromGrid = AdjustLimit(true, batteryCharged, batteryMorningLow, battRequired, 20);

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
            SendEmail(plan);
        }

        private DateTime GenerationStartTomorrow(DateTime today)
        {
            // TODO: query.
            return today.Date.AddDays(1).AddHours(10);
        }
    }
}
