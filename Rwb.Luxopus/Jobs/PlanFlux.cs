using Microsoft.Extensions.Logging;
using Rwb.Luxopus.Services;
using System;
using System.Linq;
using System.Text;

namespace Rwb.Luxopus.Jobs
{
    public enum FluxCase
    {
        Peak,
        Daytime,
        Low,
        //Evening,
        Zero
    }

    /// <summary>
    /// <para>
    /// Plan for 'flux' tariff https://octopus.energy/smart/flux/
    /// </para>
    /// </summary>
    public abstract class PlanFlux : Planner
    {
        protected readonly IEmailService Email;

        protected static FluxCase GetFluxCase(Plan plan, HalfHourPlan p)
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

            if(p.Buy <= 0) { return FluxCase.Zero; }

            if (p.Start.Hour < 4)
            {
                return FluxCase.Low;
            }
            else if (p.Start.Hour >= 15 && p.Start.Hour <= 17)
            {
                return FluxCase.Peak;
            }
            //else if( p.Start.Hour >= 18 && p.Start.Hour <= 23)
            //{
            //    return FluxCase.Evening;
            //}
            return FluxCase.Daytime;

            throw new NotImplementedException();
        }

        public PlanFlux(ILogger<LuxMonitor> logger, IInfluxQueryService influxQuery, ILuxopusPlanService plan, IEmailService email)
            : base(logger, influxQuery, plan)
        {
            Email = email;
        }

        protected void SendEmail(Plan plan, string notes)
        {
            StringBuilder message = new StringBuilder();
            foreach (HalfHourPlan p in plan.Plans.OrderBy(z => z.Start))
            {
                message.AppendLine(p.ToString());
            }

            Email.SendEmail($"Solar strategy ({this.GetType().Name}) " + plan.Plans.First().Start.ToString("dd MMM"), message.ToString() + Environment.NewLine + Environment.NewLine + notes);
            Logger.LogInformation($"Planner '{this.GetType().Name}' created new plan: " + Environment.NewLine + message.ToString() + Environment.NewLine + notes);
        }

        /// <summary>
        /// <para>What limit should we set now in order to reach a target later?</para>
        /// <para>Fudged by examining previous case. TODO: use rate data.</para>
        /// </summary>
        /// <param name="up"></param>
        /// <param name="target"></param>
        /// <param name="absoluteLimit"></param>
        /// <param name="previousLimit"></param>
        /// <param name="previousActual"></param>
        /// <returns></returns>
        protected static long AdjustLimit(bool up, long previousValue, long previousResult, long wantedResult, long newValueLimit)
        {
            long newValue = previousValue;
            if (up)
            {
                // Adjust up to maximum.
                if (previousResult > wantedResult)
                {
                    // We over-shot, so throttle it back.
                    newValue -= (previousResult - wantedResult);
                }
                else if (previousResult < wantedResult)
                {
                    // Try harder.
                    newValue += (wantedResult - previousResult);
                }

                if (newValue > newValueLimit)
                {
                    newValue = newValueLimit;
                }
            }
            else
            {
                // Adjust down to minimum.
                if (previousResult < wantedResult)
                {
                    // We over-shot, so throttle it back.
                    newValue += (wantedResult - previousResult);
                }
                else if (previousResult > wantedResult)
                {
                    // Try harder.
                    newValue -= (previousResult - wantedResult);
                }

                if (newValue < newValueLimit)
                {
                    newValue = newValueLimit;
                }
            }
            return newValue;
        }

        protected static long SetLimit(long valueNow, decimal rate, long targetLater)
        {
            throw new NotImplementedException();
        }
    }
}
