using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Luxopus
{
    internal class Luxopus
    {
        private readonly LuxopusSettings _AppSettings;
        private readonly ILuxopusPlanService _PlanService;

        public Luxopus(IOptions<LuxopusSettings> settings, ILuxopusPlanService planService)
        {
            _AppSettings = settings.Value;
            _PlanService = planService;
        }

        public async Task RunAsync()
        {
            // Get the current plan. Make one if we don't have one.
            Plan? p = _PlanService.GetCurrentPlan();
            if( p == null)
            {
                p = await MakePlanAsync();
            }

            // Generate a state file
        }

        /// <summary>
        /// Query Influx and whatnot to make a plan.
        /// </summary>
        /// <returns></returns>
        private async Task<Plan> MakePlanAsync()
        {

        }

        /// <summary>
        /// Implement a plan.
        /// Check that settings are corrent and adjust them if not.
        /// </summary>
        /// <param name="plan"></param>
        /// <returns></returns>
        private async Task ImplementPlanAsync(Plan plan)
        {

        }
    }

    internal class Plan
    {

    }
}
