using Luxopus.Services;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace Luxopus.Jobs
{
    internal class OctopusPrices : Job
    {
        private const string Measurement = "prices";

        private readonly IInfluxWriterService _Influx;

        public OctopusPrices(ILogger<LuxMonitor> logger, IInfluxWriterService influx)  :base(logger)
        {
            _Influx= influx;
        }

        public override async Task RunAsync(CancellationToken cancellationToken)
        {
        }
    }
}
