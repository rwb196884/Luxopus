using CoordinateSharp;
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
    public enum FluxCase
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
    public abstract class PlanFlux : Planner
    {
        private readonly IEmailService _Email;

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

            if (p.Start.Hour < 4)
            {
                return FluxCase.Low;
            }
            else if (p.Start.Hour >= 15 && p.Start.Hour <= 17)
            {
                return FluxCase.Peak;
            }
            return FluxCase.Daytime;

            throw new NotImplementedException();
        }

        public PlanFlux(ILogger<LuxMonitor> logger, IInfluxQueryService influxQuery, ILuxopusPlanService plan, IEmailService email)
            : base(logger, influxQuery, plan)
        {
            _Email = email;
        }

        protected void SendEmail(Plan plan)
        {
            StringBuilder message = new StringBuilder();
            foreach (HalfHourPlan p in plan.Plans.OrderBy(z => z.Start))
            {
                message.AppendLine(p.ToString());
            }

            _Email.SendEmail($"Solar strategy ({this.GetType().Name}) " + plan.Plans.First().Start.ToString("dd MMM"), message.ToString());
            Logger.LogInformation($"Planner '{this.GetType().Name}' creted new plan: " + Environment.NewLine + message.ToString());
        }
    }
}
