﻿using Microsoft.Extensions.Logging;
using Rwb.Luxopus.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Jobs
{
    /*
Must empty the battery to the grid at the evening peak time.

If you empty the battery completely then you have to buy back to use.
Sell price is 34.9p and buy price is 32.3p therefore it's economic to empty the battery completely.

At the over night low you can either
(1) leave the battery empty, or
(2) fill the battery.

In case (1) what you generate during the day gets store in the battery to be sold in the evening at 34.9p.

In case (2) what you generate during the day must be exported immediately at 21.8p.

The profit in case (2) is 15.2p but the extra in case (1) is 13.1p.

However, there are losses whenever electricity moves through the inverter. The movements are:
Case (1): solar to battery, battery to grid.
Case (2): grid to battery, solar to grid, battery to grid.

In case (2) solar goes to grid instead of battery, battery still goes to grid, but there is an extra movement for charging the battery. Therefore case (2) is only economic if the inverter is at lest 13.1/15/2 = 87% efficient (which it is, I’m seeing over 95%).

Therefore the plan should be:

    empty the battery completely at the evening peak,
    buy grid electricity to use after the evening peak,
    fill the battery at the nighttime low,
    export daytime generation immediately (this is the best time to run appliances because losing 21.8p of export is better than buying at 32.8p to use.)
     */

    /// <summary>
    /// <para>
    /// Plan for 'flux' tariff https://octopus.energy/smart/flux/
    /// </para>
    /// </summary>
    public class PlanFlux1 : PlanFlux
    {
        private const bool _ExportTariffWorking = true; // 10 fucking weeks.

        public PlanFlux1(ILogger<LuxMonitor> logger, IInfluxQueryService influxQuery, ILuxopusPlanService plan, IEmailService email)
            : base(logger, influxQuery, plan, email)
        { }

        protected override async Task WorkAsync(CancellationToken cancellationToken)
        {
            /*
             * Flux    B:20p, S:08p 2am to 5am local time.
             * Daytime B:33p, S:21p
             * Peak    B:46p, S:34p 4pm to 7pm
             * 
             * Fixed LUX configuration:
             * Force charge 15:45 to 16:00 to top up battery to use any available space.
             * Force discharge 16:00 to 19:00 cut off 5. 
             * Buy power to use if necessary rather than keep it in battery.
             * Force charge 02:00 to 05:00 cut off at 99
             * 
             * To discharge from 95 to 5 in 3 hours (6 periods) will need 15% out per half hour.
             */

            DateTime t0 = DateTime.UtcNow;
            Plan? current = PlanService.Load(t0);
            //if (current != null && current.Current == current.Plans.First())
            //{
            //    Logger.LogInformation($"Current plan is new; not creating new plan.");
            //    return;
            //}
            //Logger.LogInformation($"Creating new plan.");

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

                        long dischargeToGrid = dischargeAchievedYesterday;
                        if (batteryLowBeforeChargingYesterday <= BatteryAbsoluteMinimum)
                        {
                            // If the battery got down to BatteryAbsoluteMinimum then we sold too much yesterday.
                            dischargeToGrid += 1 + (BatteryAbsoluteMinimum - batteryLowBeforeChargingYesterday);
                        }
                        else if (batteryLowBeforeChargingYesterday > BatteryAbsoluteMinimum + 1)
                        {
                            // Could sell more,
                            dischargeToGrid -= (batteryLowBeforeChargingYesterday - BatteryAbsoluteMinimum - 1);
                        }
                        // If batteryMinimumYesterday == BatteryAbsoluteMinimum + 1 then we got it right.

                        if (dischargeToGrid < DischargeAbsoluteMinimum)
                        {
                            dischargeToGrid = DischargeAbsoluteMinimum;
                        }

                        // TODO: look at this afternoon's house usage and work out if it's unusually high (e.g., visitors)
                        // and adjust prediction for evening use.

                        p.Action = new PeriodAction()
                        {
                            ChargeFromGrid = 0,
                            DischargeToGrid = _ExportTariffWorking ? Convert.ToInt32(dischargeToGrid) : 65, // We can buy back cheaper before the low. On-grid cut-off is 5. 
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
                        p.Action = new PeriodAction()
                        {
                            ChargeFromGrid = _ExportTariffWorking ? 96 : 0,
                            DischargeToGrid = 100,
                            //BatteryChargeRate = 100,
                            //BatteryGridDischargeRate = 0,
                        };
                        break;
                }
            }

            // Do not fill up just before the peak.
            // Buy at 33p with 90% efficiency each way leaves 0.81 units at 34p = 27p.

            // No different efficiency between exporting and using therefore empty the battery (and buy to use) rather than keep for use.

            // Do not empty just before the low.
            // Buy at 20p with 90% efficiency each way leaves 0.81 units at 21p = 17p.

            PlanService.Save(plan);
            SendEmail(plan, "");

            // 7PM: sell but keep enough to get to 2AM. Export generation.
            // 2AM: fill the battery.
            // 5AM: export generation.
            // And just to really fuck things up: these are always local time.

            // TO DO:
            // Get what we set it to yesterday.
            // Get batt level at 2am.
            // If batt was >10% then we can go a bit lower today.

            // Discharge at peak. Keep enough to get to over night mininum.
        }

        private const int DischargeAbsoluteMinimum = 18;

        private const int BatteryAbsoluteMinimum = 5;
    }
}
