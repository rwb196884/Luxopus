using InfluxDB.Client.Core.Flux.Domain;
using Luxopus.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Luxopus.Jobs
{

    /// <summary>
    /// <para>
    /// Buy/sell strategy
    /// </para>
	/// <para>
	/// Probably heavily biassed to the UK market with lots of hard-coded assumptions.
	/// </para>
    /// </summary>
    internal class PlanA : Job
    {
        const int BatteryDrainPerHalfHour = 10;
        const int BatteryMin = 50;

        private readonly ILuxService _Lux;
        private readonly IInfluxQueryService _InfluxQuery;
        private readonly ILuxopusPlanService _Plan;
        IEmailService _Email;

        public PlanA(ILogger<LuxMonitor> logger, ILuxService lux, IInfluxQueryService influxQuery, ILuxopusPlanService plan, IEmailService email)  :base(logger)
        {
            _Lux= lux;
            _InfluxQuery= influxQuery;
            _Plan= plan;
            _Email= email;
        }

        public override async Task RunAsync(CancellationToken cancellationToken)
        {
            int b = await _Lux.GetBatteryLevelAsync();
            int periods = (b - BatteryMin) / BatteryDrainPerHalfHour;

            DateTime t0 = new DateTime(2023, 03, 31, 18, 00, 00);

			// Prices.
			List<ElectricityPrice> prices = await _InfluxQuery.GetPricesAsync(t0);

            List<HalfHourPlan> plan = prices.Select(z => new HalfHourPlan(z)).ToList();

            // TO DO: we might want to choose periods after the maximum.
            foreach( HalfHourPlan p in plan.Where(z => z.Sell > 15).OrderByDescending(z => z.Sell).Take(periods + 2 /* Use batt limit to stop */))
            {
                p.Action = new PeriodAction()
                {
                    ChargeFromGrid = false,
                    ChargeFromGeneration = false,
                    DischargeToGrid = true
                };
            }

            _Plan.SavePlans(plan);

            // TO DO: send email.

        }
    }
}
