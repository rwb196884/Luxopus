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
    static class PlanAHExtensions
    {
        public static IEnumerable<T> Evening<T>(this IEnumerable<T> things) where T : HalfHour
        {
            // This is probably a proxy for 'global maximum'
            return things.Where(z => z.Start.Hour >= 16 && z.Start.Hour < 20);
        }
        public static IEnumerable<T> Overnight<T>(this IEnumerable<T> things) where T : HalfHour
        {
            // This is probably a proxy for 'global minimum'
            return things.Where(z => z.Start.Hour >= 0 && z.Start.Hour < 9);
        }
        public static IEnumerable<T> Morning<T>(this IEnumerable<T> things) where T : HalfHour
        {
            // This is probably a proxy for 'second maximum'
            return things.Where(z => z.Start.Hour >= 7 && z.Start.Hour < 11);
        }
        public static IEnumerable<T> Daytime<T>(this IEnumerable<T> things) where T : HalfHour
        {
            // This is probably a proxy for 'second minimum' and/or 'generation period'.
            return things.Where(z => z.Start.Hour >= 9 && z.Start.Hour < 16);
        }

        public static IEnumerable<decimal> BuyPrice<T>(this IEnumerable<T> things) where T : ElectricityPrice
        {
            return things.Select(z => z.Buy);
        }
        public static IEnumerable<decimal> SellPrice<T>(this IEnumerable<T> things) where T : ElectricityPrice
        {
            return things.Select(z => z.Sell);
        }

        public static decimal Median(this IEnumerable<decimal> things)
        {
            int n = things.Count();
            if (n % 2 == 0)
            {
                return things.Skip(n / 2).Take(2).Average();
            }
            else
            {
                return things.Skip(n / 2).Take(1).Single();

            }
        }
    }

    /// <summary>
    /// <para>
    /// Buy/sell strategy -- first attempt. Sell at the evening high but leave enough in the battery for use.
    /// </para>
	/// <para>
	/// Probably heavily biassed to the UK market with lots of hard-coded assumptions.
	/// </para>
    /// </summary>
    public class PlanA : Planner
    {
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
        const int BatteryMin = 50;


        private readonly ILuxService _Lux;
        IEmailService _Email;
        ISmsService _Sms;

        public PlanA(ILogger<LuxMonitor> logger, ILuxService lux, IInfluxQueryService influxQuery, ILuxopusPlanService plan, IEmailService email, ISmsService sms) : base(logger, influxQuery, plan)
        {
            _Lux = lux;
            _Email = email;
            _Sms = sms;
        }

        public override async Task RunAsync(CancellationToken cancellationToken)
        {
            Dictionary<string, string> settings = await _Lux.GetSettingsAsync();
            string j = JsonSerializer.Serialize(settings);

            DateTime t0 = DateTime.UtcNow;
            //DateTime t0 = new DateTime(2023, 03, 31, 17, 00, 00);
            //DateTime t0 = new DateTime(2023, 04, 02, 17, 00, 00);

            Plan? current = PlanService.Load(t0);
            if(current != null)
            {
                return;
                // TODO: create a new -- updated -- plan.
            }

            // Get prices and set up plan.
            DateTime start = t0.StartOfHalfHour();
            DateTime stop = (new DateTime(t0.Year, t0.Month, t0.Day, 18, 0, 0)).AddDays(1);

            List<ElectricityPrice> prices = await InfluxQuery.GetPricesAsync(start, stop, "E-1R-AGILE-FLEX-22-11-25-E", "E -1R-AGILE-OUTGOING-19-05-13-E");

            Plan plan = new Plan(prices);

            // How low can we go?
            int battMin = BatteryMin;
            if (plan.Plans.Any(z => z.Buy < 0))
            {
                battMin = 30;
            }

            if (plan.Plans.Morning().SellPrice().DefaultIfEmpty(0).Max() > plan.Plans.Overnight().BuyPrice().DefaultIfEmpty(100).Min() + 3M)
            {
                battMin = 30;
            }

            // Battery: current level.
            int b = await InfluxQuery.GetBatteryLevelAsync();
            if( b < 0) { b = 90; }

            // Battery: level change per kWh.

            // Set SELL in order to reach the required battery (low) level.
            int periods = (b - battMin) / BatteryDrainPerHalfHour;

            // TO DO: we might want to choose periods before the maximum.
            foreach (HalfHourPlan p in plan.Plans.Take(12 /* don't use tomorrow's high if it's higher; have to sell today */).OrderByDescending(z => z.Sell).Take(periods + 1 /* Use batt limit to stop. */))
            {
                p.Action = new PeriodAction()
                {
                    ChargeFromGrid = 0,
                    ExportGeneration = false,
                    DischargeToGrid = battMin
                };
            }

            // Buy over night when price is -ve or lower than morning sell price.
            decimal morningSellMax = plan.Plans.Morning().SellPrice().DefaultIfEmpty(0).Max();
            foreach (HalfHourPlan p in plan.Plans.Where(z => z.Buy < 0 /* paid to buy*/ || z.Buy < morningSellMax / 1.2M))
            {
                p.Action = new PeriodAction()
                {
                    ChargeFromGrid = 100,
                    ExportGeneration = false,
                    DischargeToGrid = 100
                };
            }
            decimal overnightBuyMean = plan.Plans.Where(z => z.Buy < 0 /* paid to buy*/ || z.Buy < morningSellMax - 2M).Select(z => z.Buy).DefaultIfEmpty(100M).Average();

            // Morning sell.
            foreach (HalfHourPlan p in plan.Plans.Morning().Where(z => z.Sell > overnightBuyMean * 1.2M))
            {
                if (overnightBuyMean < morningSellMax - 2M)
                {
                    p.Action = new PeriodAction()
                    {
                        ChargeFromGrid = 0,
                        ExportGeneration = false,
                        DischargeToGrid = 50 // Enough space for the day's generation.
                    };
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

            if (plan.Plans.Any(z => (z.Action?.ChargeFromGrid ?? 0) > 0))
            {
                emailSubjectPrefix += "I";
            }

            string s = message.ToString();

            _Email.SendEmail(emailSubjectPrefix + "Solar strategy " + plan.Plans.First().Start.ToString("dd MMM"), message.ToString());

            if (emailSubjectPrefix.Length > 0)
            {
                string buy = "";
                if(plan.Plans.Overnight().Any())
                {
                decimal overnightBuyMin = plan.Plans.Overnight().Select(z => z.Buy).Min();
                    buy = $"Buy {overnightBuyMin.ToString("00.0")} ";
                }

                string sell = "";
                if(plan.Plans.Evening().Any())
                {
                    decimal eveningSellHigh = plan.Plans.Evening().Select(z => z.Sell).Max();
                    sell = $"Sell {eveningSellHigh.ToString("00.0")}";
                }

                _Sms.SendSms($"SOLAR! {buy}{sell}");
            }
        }
    }
}
