using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Jobs
{
    /// <summary>
    /// This needs to run at 11pm at inverter time.
    /// </summary>
    public class NullJob : Job
    {

        public NullJob(ILogger<NullJob> logger) : base(logger) { }

        protected override async Task WorkAsync(CancellationToken cancellationToken)
        {
            Logger.LogInformation("Null job.");
            await Task.CompletedTask;
        }
    }
}
