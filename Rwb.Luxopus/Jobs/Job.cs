using Microsoft.Extensions.Logging;
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

        public abstract Task RunAsync(CancellationToken cancellation);
    }
}
