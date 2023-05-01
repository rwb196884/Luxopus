using Microsoft.Extensions.Logging;
using Rwb.Luxopus.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Jobs
{
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

        public PlanFlux2(ILogger<LuxMonitor> logger, IInfluxQueryService influxQuery, ILuxopusPlanService plan, IEmailService email)
            : base(logger, influxQuery, plan, email)
        { }

        protected override async Task WorkAsync(CancellationToken cancellationToken)
        {
            DateTime t0 = DateTime.UtcNow;
            Plan? current = PlanService.Load(t0);

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
                        p.Action = new PeriodAction()
                        {
                            ChargeFromGrid = 0,
                            DischargeToGrid = BatteryAbsoluteMinimum
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

                        long chargeFromGrid = batteryCharged;
                        if(batteryMorningLow < 15)
                        {
                            chargeFromGrid += (15 - batteryMorningLow);
                        }
                        else if( batteryMorningLow > 17)
                        {
                            chargeFromGrid --;
                        }

                        // Hack.
                        if( chargeFromGrid > 50)
                        {
                            chargeFromGrid = 10; 
                        }

                        // Power needed to satisfy daytime demand if there is not enough solar generation (e.g., ovening in winter).

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
