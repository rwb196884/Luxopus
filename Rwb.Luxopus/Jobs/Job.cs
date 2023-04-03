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
            try
            {
                await WorkAsync(cancellation);
            }
            catch( Exception e)
            {
                Logger.LogError(e, $"Job {this.GetType().Name} failed.");
            }
        }

        protected abstract Task WorkAsync(CancellationToken cancellation);
    }
}
