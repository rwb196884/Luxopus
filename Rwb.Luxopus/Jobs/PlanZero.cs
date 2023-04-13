using Microsoft.Extensions.Logging;
using Rwb.Luxopus.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Joins;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Jobs
{
    /// <summary>
    /// <para>
    /// Plan for unable to sell but can buy at less than zero.
	/// </para>
    /// </summary>
    public class PlanZero : Planner
    {
        IEmailService _Email;

        public PlanZero(ILogger<LuxMonitor> logger, IInfluxQueryService influxQuery, ILuxopusPlanService plan, IEmailService email) : base(logger, influxQuery, plan)
        {
            _Email = email;
        }

        protected override async Task WorkAsync(CancellationToken cancellationToken)
        {
            DateTime t0 = DateTime.UtcNow;
            //DateTime t0 = new DateTime(2023, 4, 10, 22, 0, 0);
            Plan? current = PlanService.Load(t0);
            if (current != null && current.Current == current.Plans.First())
            {
                Logger.LogInformation($"Current plan is new; not creating new plan.");
                return;
            }

            // Get prices and set up plan.
            DateTime start = t0.StartOfHalfHour();
            DateTime stop = (new DateTime(t0.Year, t0.Month, t0.Day, 22, 0, 0)).AddDays(1); // 10pm tomorrow.

            List<ElectricityPrice> prices = await InfluxQuery.GetPricesAsync(start, stop, "E-1R-AGILE-FLEX-22-11-25-E", "E-1R-AGILE-OUTGOING-19-05-13-E");
            Plan plan = new Plan(prices);

            // Buy when the price is negative.
            foreach (HalfHourPlan p in plan.Plans.Where(z => z.Buy < 0))
            {
                p.Action = new PeriodAction()
                {
                    ChargeFromGrid = 100, // This might be too much if there is a sequence with a highh (small negative) price first so we want to wait while later.
                    //BatteryChargeRate = 100,
                    //BatteryGridDischargeRate = 0,
                    DischargeToGrid = 100
                };
            }

            // Export a little at peak time to show willing.
            ConfigurePeriod(plan.Plans.Where(z => z.Start.Date == t0.Date));
            ConfigurePeriod(plan.Plans.Where(z => z.Start.Date == t0.AddDays(1).Date));

            // Make room in the battery.
            int battMin = 90 - 15 * (plan?.Plans?.Where(z => z.Buy < 0)?.Count() ?? 0);
            battMin = battMin < 20 ? 20 : battMin;

            foreach (HalfHourPlan p in plan.Plans.Where(z => z.Buy < 0 && (plan.GetPrevious(z)?.Buy ?? -1) > 0))
            {
                int runLength = 1;
                HalfHourPlan? q = plan.GetPrevious(p);
                while (q != null && q.Buy < 0)
                {
                    q = plan.GetNext(q);
                    runLength++;
                }

                q = plan.GetPrevious(p);
                for (int i = 1; i <= runLength && q != null && q.Buy > 0 && q.Action == null; i++)
                {
                    q.Action = new PeriodAction()
                    {
                        ChargeFromGrid = 0,
                        //BatteryChargeRate = 100,
                        //BatteryGridDischargeRate = 95,
                        DischargeToGrid = battMin + 12 * (i - 1)
                    };
                    q = plan.GetPrevious(q);
                }
            }

            PlanService.Save(plan);
            SendEmail(plan);
        }
        
        private void ConfigurePeriod(IEnumerable<HalfHourPlan> period)
        {
            HalfHourPlan? maxSell = period
                .OrderByDescending(z => z.Sell)
                .First();
            IEnumerable<HalfHourPlan> sellable = period.Where(z => z.Sell > 15M);
            foreach (HalfHourPlan p in sellable.OrderByDescending(z => z.Sell))
            {
                int battMin = 70;
                if ( p.Start < maxSell.Start)
                {
                    battMin += 15 * (sellable.Where(z => z.Start >= p.Start && z.Start < maxSell.Start).Count());
                }
                if (battMin < 100)
                {
                    p.Action = new PeriodAction()
                    {
                        ChargeFromGrid = 0,
                        //BatteryChargeRate = 0, // Send any generation straight out.
                        //BatteryGridDischargeRate = 33 + (p.Sell > 15 ? 33 : 0),
                        DischargeToGrid = battMin
                    };
                }
            }
        }

        private void SendEmail(Plan plan)
        {
            StringBuilder message = new StringBuilder();
            foreach (HalfHourPlan p in plan.Plans.OrderBy(z => z.Start))
            {
                message.AppendLine(p.ToString());
            }

            string emailSubjectPrefix = "";
            if (plan.Plans.Any(z => z.Buy < 0))
            {
                emailSubjectPrefix = "*** ";
            }

            _Email.SendEmail(emailSubjectPrefix + "Solar strategy (plan zero) " + plan.Plans.First().Start.ToString("dd MMM"), message.ToString());
            Logger.LogInformation("PlanZero creted new plan: " + Environment.NewLine + message.ToString());
        }
    }
}
