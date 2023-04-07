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
    /// <summary>
    /// <para>
    /// Plan for 'flux' tariff https://octopus.energy/smart/flux/
    /// </para>
    /// </summary>
    public class PlanFlux : Planner
    {
        const decimal ExportPrice = 15M;

        /// <summary>
        /// Rate at which battery discharges to grid. TODO: estimate from historical data.
        /// Capacity 189 Ah * 55V ~ 10kWh so 1% is 100Wh
        /// Max charge ~ 4kW => 2.5 hours => 20% per half hour.
        /// 3kW => 3.5 hours => 15% per half hour.
        /// TODO: estimate from data.
        /// </summary>
        const int BatteryDrainPerHalfHour = 12;

        /// <summary>
        /// Normal battery minimum allowed level. TODO: estimate from historical data.
        /// Power to house: day 9am--11pm: 250W, night 11pm--8am: 200W.
        /// If 1 battery percent is 100Wh then that's 2.5 resp. 2 percent per hour.
        /// 15 hours over night shoule be about 30% batt.
        /// </summary>
        const int BatteryMin = 30;

        private readonly ILuxService _Lux;
        IEmailService _Email;
        ISmsService _Sms;

        public PlanFlux(ILogger<LuxMonitor> logger, ILuxService lux, IInfluxQueryService influxQuery, ILuxopusPlanService plan, IEmailService email, ISmsService sms) : base(logger, influxQuery, plan)
        {
            _Lux = lux;
            _Email = email;
            _Sms = sms;
        }

        private DateTime MakeUtcTime(int localHours, int localMinutes)
        {
            DateTime local = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, localHours, localMinutes, 0);
            local = DateTime.SpecifyKind(local, DateTimeKind.Local);
            return _Lux.ToUtc(local);
        }

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
                return;
                // TODO: create a new -- updated -- plan.
            }

            DateTime start = t0.StartOfHalfHour();
            DateTime stop = (new DateTime(t0.Year, t0.Month, t0.Day, 18, 0, 0)).AddDays(1);
            List<ElectricityPrice> prices = await InfluxQuery.GetPricesAsync(start, stop, "E-1R-FLUX-IMPORT-23-02-14-E", "E-1R-FLUX-EXPORT-23-02-14-E");
            Plan plan = new Plan(prices);

            HalfHourPlan pSell = plan.Plans.OrderByDescending(z => z.Sell).Take(1).SingleOrDefault();
            if(pSell != null)
            {
                pSell.Action = new PeriodAction()
                {
                    DischargeToGrid = 20
                };
            }

            HalfHourPlan pBuy = plan.Plans.OrderBy(z => z.Buy).Take(1).SingleOrDefault();
            if (pSell != null)
            {
                pSell.Action = new PeriodAction()
                {
                    ChargeFromGrid = 99
                };
            }

            PlanService.Save(plan);
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

            string emailSubjectPrefix = "";
            if (plan.Plans.Any(z => (z.Action?.DischargeToGrid ?? 101) <= 100))
            {
                emailSubjectPrefix += "E";
            }

            if (plan.Plans.Any(z => (z.Action?.ChargeFromGrid ?? 0) > 0) )
            {
                emailSubjectPrefix += "I";
            }

            string s = message.ToString();

            _Email.SendEmail(emailSubjectPrefix + "Solar strategy " + plan.Plans.First().Start.ToString("dd MMM"), message.ToString());

            if (emailSubjectPrefix.Length > 0)
            {
                decimal eveningSellHigh = plan.Plans.Evening().Select(z => z.Sell).Max();
                decimal overnightBuyMin = plan.Plans.Overnight().Select(z => z.Buy).Min();
                _Sms.SendSms($"SOLAR! Sell {eveningSellHigh.ToString("00.0")} Buy {overnightBuyMin.ToString("00.0")}");
            }
        }
    }
}
