using Microsoft.Extensions.Logging;
using Rwb.Luxopus.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Jobs
{
    /// <summary>
    /// This needs to run at 11pm at inverter time.
    /// </summary>
    public class AtJob : Job
    {
        private readonly IAtService _AtService;

        public AtJob(ILogger<NullJob> logger, IAtService atService) : base(logger) { _AtService = atService; }

        protected override async Task WorkAsync(CancellationToken cancellationToken)
        {
            List<Func<Task>> thingsToDo = _AtService.Dequeue();
            if (thingsToDo.Count == 0) { return; }
            Logger.LogInformation($"Running {thingsToDo.Count} jobs.");
            foreach(Func<Task> f in thingsToDo)
            {
                await f();
            }
        }
    }
}
