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
    /// Plan for fixed export price -- 15p at the time of writing.
    /// </para>
    /// </summary>
    public class Plan15 : Planner
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

        public Plan15(ILogger<LuxMonitor> logger, ILuxService lux, IInfluxQueryService influxQuery, ILuxopusPlanService plan, IEmailService email, ISmsService sms) : base(logger, influxQuery, plan)
        {
            _Lux = lux;
            _Email = email;
            _Sms = sms;
        }

        protected override async Task WorkAsync(CancellationToken cancellationToken)
        {
            //DateTime t0 = new DateTime(2023, 03, 31, 17, 00, 00);
            DateTime t0 = new DateTime(2023, 04, 02, 17, 00, 00);

            // Get prices and set up plan.
            DateTime start = t0.StartOfHalfHour();
            DateTime stop = (new DateTime(t0.Year, t0.Month, t0.Day, 18, 0, 0)).AddDays(1);
            List<ElectricityPrice> prices = await InfluxQuery.GetPricesAsync(start, stop, "E-1R-AGILE-FLEX-22-11-25-E", "OUTGOING-FIX-12M-19-05-13");
            Plan plan = new Plan(prices);

            // When is it economical to buy?
            foreach (HalfHourPlan p in plan.Plans.Where(z => z.Buy < ExportPrice * 0.8M))
            {
                p.Action = new PeriodAction()
                {
                    ChargeFromGrid = p.Buy < 0 ? 100 : 99,
                    BatteryChargeRate = 100,
                    BatteryDischargeRate = 0,
                    DischargeToGrid = 100
                };
            }

            // Empty the battery ready to buy.
            foreach( HalfHourPlan p in plan.Plans.Where(z => (z?.Action.ChargeFromGrid ?? 0) > 0))
            {
                HalfHourPlan? pp = plan.GetPrevious(p);
                if( pp == null || (pp?.Action.ChargeFromGrid ?? 0) > 0) { continue; }

                if(pp.Action != null)
                {
                    throw new NotImplementedException();
                }

                int battEstimate = 80; // Current level.
                while(battEstimate > 20 && pp != null)
                {
                    pp.Action = new PeriodAction()
                    {
                        ChargeFromGrid = 0,
                        BatteryChargeRate = 100,
                        BatteryDischargeRate = 100,
                        DischargeToGrid = 20
                    };

                    battEstimate = battEstimate = BatteryDrainPerHalfHour;
                    //pp = plan.GetPrevious(pp);
                }
            }

            PlanService.Save(plan);
            SendEmail(plan);
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
