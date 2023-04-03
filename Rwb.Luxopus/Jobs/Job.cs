using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Jobs
{
    public abstract class Job
    {
        protected ILogger Logger;

        protected Job(ILogger<Job> logger)
        {
            Logger = logger;
        }

        public async Task RunAsync(CancellationToken cancellation)
        {
            Logger.LogInformation($"Running {this.GetType().Name}.");
            try
            {
                await WorkAsync(cancellation);
                Logger.LogInformation($"Finished {this.GetType().Name} successfully.");
            }
            catch( Exception e)
            {
                Logger.LogError(e, $"Job {this.GetType().Name} failed.");
            }
        }

        protected abstract Task WorkAsync(CancellationToken cancellation);
    }
}
