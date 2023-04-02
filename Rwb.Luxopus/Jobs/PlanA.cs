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
    /// Buy/sell strategy
    /// </para>
	/// <para>
	/// Probably heavily biassed to the UK market with lots of hard-coded assumptions.
	/// </para>
    /// </summary>
    public class PlanA : Planner
    {

        const int BatteryDrainPerHalfHour = 10;
        const int BatteryMin = 50;

        private readonly ILuxService _Lux;
        private readonly ILuxopusPlanService _Plan;
        IEmailService _Email;
        ISmsService _Sms;

        public PlanA(ILogger<LuxMonitor> logger, ILuxService lux, IInfluxQueryService influxQuery, ILuxopusPlanService plan, IEmailService email, ISmsService sms) : base(logger, influxQuery)
        {
            _Lux = lux;
            _Plan = plan;
            _Email = email;
            _Sms = sms;
        }

        public override async Task RunAsync(CancellationToken cancellationToken)
        {
            DateTime t0 = new DateTime(2023, 03, 31, 18, 00, 00);

            // Get prices and set up plan.
            List<ElectricityPrice> prices = await InfluxQuery.GetPricesAsync(t0);

            Plan plan = new Plan(prices);

            // How low can we go?
            int battMin = BatteryMin;
            if (plan.Plans.Any(z => z.Buy < 0))
            {
                battMin = 30;
            }

            if (plan.Plans.Morning().SellPrice().Max() > plan.Plans.Overnight().BuyPrice().Min())
            {
                battMin = 40;
            }

            // Battery: current level.
            int b = await GetBatteryLevelAsync();

            // Battery: level change per kWh.

            // Set SELL in order to reach the required battery (low) level.
            int periods = (b - battMin) / BatteryDrainPerHalfHour;

            // TO DO: we might want to choose periods after the maximum.
            foreach (HalfHourPlan p in plan.Plans.OrderByDescending(z => z.Sell).Take(periods + 2 /* Use batt limit to stop */))
            {
                p.Action = new PeriodAction()
                {
                    ChargeFromGrid = false,
                    ChargeFromGeneration = false,
                    DischargeToGrid = battMin
                };
            }

            // Buy over night when price is -ve or lower than morning sell price.
            decimal morningSellMax = plan.Plans.Morning().SellPrice().Max();
            foreach (HalfHourPlan p in plan.Plans.Where(z => z.Buy < 0 /* paid to buy*/ || z.Buy < morningSellMax - 2M))
            {
                p.Action = new PeriodAction()
                {
                    ChargeFromGrid = true,
                    ChargeFromGeneration = false,
                    DischargeToGrid = 100
                };
            }
            decimal overnightBuyMean = plan.Plans.Where(z => z.Buy < 0 /* paid to buy*/ || z.Buy < morningSellMax - 2M).Select(z => z.Buy).Average();

            // Morning sell.
            foreach (HalfHourPlan p in plan.Plans.Morning().Where(z => z.Sell < overnightBuyMean - 2M))
            {
                if (overnightBuyMean < morningSellMax - 2M)
                {
                    p.Action = new PeriodAction()
                    {
                        ChargeFromGrid = false,
                        ChargeFromGeneration = false,
                        DischargeToGrid = 30
                    };
                }
            }

            decimal daytimeSellMedian = plan.Plans.Daytime().Select(z => z.Sell).Median();
            // if morning sell max > daytime sell median then empty the battery to store daytime generation.

            // Daytime
            // Should have made enough space in the battery to store in order to sell during the evening peak.
            (decimal min, decimal lq, decimal median, decimal mean, decimal uq, decimal max) = await GetSolcastFactorsAsync();

            _Plan.Save(plan);
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

            if (plan.Plans.Any(z => (z.Action?.ChargeFromGrid ?? false)))
            {
                emailSubjectPrefix += "I";
            }

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
