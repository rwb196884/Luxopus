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
            //DateTime t0 = new DateTime(2023, 4, 10, 18, 0, 0);
            //DateTime t0 = new DateTime(2023, 4, 13, 15, 0, 0);
            //DateTime t0 = new DateTime(2023, 4, 15, 15, 0, 0);
            //DateTime t0 = new DateTime(2023, 4, 16, 15, 0, 0); // Has morning sell high.
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
            //List<HalfHourPlan> maxima = plan.Plans.Where(z =>
            //{
            //    HalfHourPlan? p = plan.GetPrevious(z);
            //    HalfHourPlan? n = plan.GetNext(z);
            //    return (p == null || z.Sell >= p.Sell) && (n == null || z.Sell >= n.Sell);
            //}).ToList();
            //List<HalfHourPlan> minima = plan.Plans.Where(z =>
            //{
            //    HalfHourPlan? p = plan.GetPrevious(z);
            //    HalfHourPlan? n = plan.GetNext(z);
            //    return (p == null || z.Sell <= p.Sell) && (n == null || z.Sell <= n.Sell);
            //}).ToList();

            //foreach(HalfHourPlan max in maxima)
            //{
            //    HalfHourPlan? p = minima.OrderByDescending(z => z.Sell).FirstOrDefault(z => z.Start < max.Start);
            //    HalfHourPlan? n = minima.OrderBy(z => z.Sell).FirstOrDefault(z => z.Start > max.Start);
            //    ConfigurePeriod(plan.Plans.Where(z => (p == null || z.Start >= p.Start) || (n == null || z.Start <= n.Start)));
            //}

            // TODO: determine discharge periods.
            ConfigurePeriod(plan.Plans.Where(z => z.Start.Date == t0.Date && z.Start.Hour <= 14));
            ConfigurePeriod(plan.Plans.Where(z => z.Start.Date == t0.Date && z.Start.Hour >= 14));
            ConfigurePeriod(plan.Plans.Where(z => z.Start.Date == t0.Date.AddDays(1) && z.Start.Hour <= 14));
            ConfigurePeriod(plan.Plans.Where(z => z.Start.Date == t0.Date.Date.AddDays(1) && z.Start.Hour >= 14));

            // Make room in the battery.
            foreach (HalfHourPlan p in plan.Plans.Where(z => z.Buy < 0 && (plan.Plans.GetPrevious(z)?.Buy ?? -1) > 0))
            {
                int battMin = 90 - _BattDischargePerHalfHour *(1 + plan.Plans.Where(z => z.Start > p.Start && z.Buy < 0).Count());
                battMin = battMin < 20 ? 20 : battMin;
                int runLength = 1;
                HalfHourPlan? q = plan.Plans.GetNext(p);
                while (q != null && q.Buy < 0)
                {
                    q = plan.Plans.GetNext(q);
                    runLength++;
                }

                q = plan.Plans.GetPrevious(p);
                for (int i = 1; i <= runLength + 1 // Add an extra one to make sure there's space.
                    && q != null // Can't sell in the past -- unfortunately.
                    && q.Sell > p.Buy // Otherwise there's no point.
                    && (q.Action == null || q.Action.DischargeToGrid == 100 && q.Action.ChargeFromGrid == 0) // Not already doing something.
                    ; i++)
                {
                    q.Action = new PeriodAction()
                    {
                        ChargeFromGrid = 0,
                        //BatteryChargeRate = 100,
                        //BatteryGridDischargeRate = 95,
                        DischargeToGrid = battMin + _BattDischargePerHalfHour * (i - 1)
                    };
                    q = plan.Plans.GetPrevious(q);
                }
            }

            //PlanService.Save(plan); // Disabled because PlanFlux is in use.
            SendEmail(plan);
        }

        // TODO: estimate these parameters from historical data. Or move to settings.
        private const int _BattMin = 65;
        private const int _BattDischargePerHalfHour = 12; // Currently set tp 66% discharge rate. Should do about 20% (need 15) per half hour at 95% discharge.

        private void ConfigurePeriod(IEnumerable<HalfHourPlan> period)
        {
            if (!period.Any(z => z.Sell > 15M)) { return; } // TODO: determine sellable periods properly.
            // Discharge what we can in the most profitable periods.
            int periodsToDischarge = ((100 - _BattMin) / _BattDischargePerHalfHour /* integer division */ + 1);
            HalfHourPlan? previousDischarge = null;
            foreach (HalfHourPlan p in period.OrderByDescending(z => z.Sell).Take(periodsToDischarge /* There may be fewer. */).OrderByDescending(z => z.Start))
            {
                p.Action = new PeriodAction()
                {
                    ChargeFromGrid = 0,
                    //BatteryChargeRate = 0, // Send any generation straight out.
                    //BatteryGridDischargeRate = 33 + (p.Sell > 15 ? 33 : 0),
                    DischargeToGrid = previousDischarge == null ? _BattMin : previousDischarge.Action.DischargeToGrid + _BattDischargePerHalfHour
                };
                previousDischarge = p;
            }
            HalfHourPlan lastMainDischarge = period.Where(z => z.Action != null).OrderBy(z => z.Start).Last();

            // Move limits backwards if the earlier price is higher.
            foreach(HalfHourPlan p in period.Where(z => Plan.DischargeToGridCondition(z)))
            {
                HalfHourPlan? pp = period.Where(z => Plan.DischargeToGridCondition(z) && z.Start < p.Start).OrderBy(z => z.Start).LastOrDefault();
                if( pp != null && pp.Sell > p.Sell && pp?.Action.DischargeToGrid > p.Action.DischargeToGrid)
                {
                    pp.Action.DischargeToGrid = p.Action.DischargeToGrid;
                }
            }

            // Fill in gaps with discharge to the previous level.
            foreach (HalfHourPlan p in period.Where(z => z.Action != null))
            {
                foreach (HalfHourPlan g in period.OrderBy(z => z.Start).Gap(p, z => z.Action == null || z.Action.DischargeToGrid == 100 && z.Action.ChargeFromGrid == 0))
                {
                    g.Action = new PeriodAction()
                    {
                        ChargeFromGrid = 0,
                        DischargeToGrid = p.Action!.DischargeToGrid
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
