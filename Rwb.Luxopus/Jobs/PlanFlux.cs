using Microsoft.Extensions.Logging;
using Rwb.Luxopus.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Jobs
{
    enum FluxCase
    {
        Peak,
        Daytime,
        Low
    }

    /// <summary>
    /// <para>
    /// Plan for 'flux' tariff https://octopus.energy/smart/flux/
    /// </para>
    /// </summary>
    public class PlanFlux : Planner
    {
        private static FluxCase GetFluxCase(Plan plan, HalfHourPlan p)
        {
            //List<decimal> ps = plan.Plans.Select(z => z.Sell).Distinct().OrderBy(z => z).ToList();
            //if(p.Sell == ps[0])
            //{
            //    return FluxCase.Low;
            //}
            //else if( p.Sell == ps[1])
            //{
            //    return FluxCase.Daytime;
            //}
            //else if( p.Sell == ps[2])
            //{
            //    return FluxCase.Peak;
            //}

            if(p.Start.Hour < 4)
            {
                return FluxCase.Low;
            }
            else if( p.Start.Hour >= 15 && p.Start.Hour <= 17)
            {
                return FluxCase.Peak;
            }
            return FluxCase.Daytime;

            throw new NotImplementedException();
        }

        private readonly ILuxService _Lux;
        IEmailService _Email;

        public PlanFlux(ILogger<LuxMonitor> logger, ILuxService lux, IInfluxQueryService influxQuery, ILuxopusPlanService plan, IEmailService email) : base(logger, influxQuery, plan)
        {
            _Lux = lux;
            _Email = email;
        }

        //private DateTime MakeUtcTime(int localHours, int localMinutes)
        //{
        //    DateTime local = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, localHours, localMinutes, 0);
        //    local = DateTime.SpecifyKind(local, DateTimeKind.Local);
        //    return _Lux.ToUtc(local);
        //}

        protected override async Task WorkAsync(CancellationToken cancellationToken)
        {
            /*
             * Flux    B:20p, S:08p 2am to 5am
             * Daytime B:33p, S:21p
             * Peak    B:46p, S:34p 4pm to 7pm
             * 
             * Fixed LUX configuration:
             * Force discharge 16:30 to 17:00 cut off 30
             * Force charge 02:00 to 05:00 cut off at 99
             */

            // We don't need a plan!

            DateTime t0 = DateTime.UtcNow;
            Plan? current = PlanService.Load(t0);
            if (current != null)
            {
                if (current.Plans.Where(z => z.Start > t0).Count() > 4)
                {
                    Logger.LogInformation($"No need to create new plan: current plan has {current.Plans.Where(z => z.Start > t0).Count()} future periods.");
                    return;
                }
                // else: create a new -- updated -- plan.
                Logger.LogInformation($"No need to create new plan: there is a current plan.");
                return;
                // Can't overwrite an existing plan file.
            }
            Logger.LogInformation($"Creating new plan.");

            DateTime start = t0.StartOfHalfHour().AddDays(-1);
            DateTime stop = (new DateTime(t0.Year, t0.Month, t0.Day, 21, 0, 0)).AddDays(1);
            List<ElectricityPrice> prices = await InfluxQuery.GetPricesAsync(start, stop, "E-1R-FLUX-IMPORT-23-02-14-E", "E-1R-FLUX-EXPORT-23-02-14-E");

            // Find the current period: the last period that starts before t0.
            ElectricityPrice priceNow = prices.Where(z => z.Start < t0).OrderByDescending(z => z.Start).FirstOrDefault();
            Plan plan = new Plan(prices.Where(z => z.Start >= priceNow.Start));

            foreach(HalfHourPlan p in plan.Plans)
            {
                switch (GetFluxCase(plan, p))
                {
                    case FluxCase.Peak:
                        p.Action = new PeriodAction()
                        {
                            ChargeFromGrid = 0,
                            DischargeToGrid = 20,
                            BatteryChargeRate = 0,
                        BatteryDischargeRate = 100,
                        };
                        break;
                    case FluxCase.Daytime:
                        p.Action = new PeriodAction()
                        {
                            ChargeFromGrid = 0,
                            DischargeToGrid = 100,
                            BatteryChargeRate = 75,
                            BatteryDischargeRate = 100,
                        };
                        break;
                    case FluxCase.Low:
                        p.Action = new PeriodAction()
                        {
                            ChargeFromGrid = 98,
                            DischargeToGrid = 100,
                            BatteryChargeRate = 100,
                            BatteryDischargeRate = 0,
                        };
                        break;
                }
            }

            //PlanService.Save(plan);
            SendEmail(plan);

            // 7PM: sell but keep enough to get to 2AM. Export generation.
            // 2AM: fill the battery.
            // 5AM: export generation.
            // And just to really fuck things up: these are always local time.

            // TO DO:
            // Get what we set it to yesterday.
            // Get batt level at 2am.
            // If batt was >10% then we can go a bit lower today.

            // Discharge at peak. Keep enough to get to over night mininum.

            // TODO: UTC
            //await _Lux.SetDishargeToGridAsync(MakeUtcTime(16, 06), MakeUtcTime(18, 55), 20);
            //await _Lux.SetChargeFromGridAsync(MakeUtcTime(2, 05), MakeUtcTime(4, 55), 98);
        }

        private void SendEmail(Plan plan)
        {
            StringBuilder message = new StringBuilder();
            foreach (HalfHourPlan p in plan.Plans.OrderBy(z => z.Start))
            {
                message.AppendLine(p.ToString());
            }

            _Email.SendEmail("Solar strategy (flux) " + plan.Plans.First().Start.ToString("dd MMM"), message.ToString());
        }
    }
}
