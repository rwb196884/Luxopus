using Luxopus.Services;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace Luxopus.Jobs
{
    internal class LuxMonitor : Job
    {

        private readonly ILuxService _Lux;
        private readonly IInfluxWriterService _Influx;

        public LuxMonitor(ILogger<LuxMonitor> logger, ILuxService lux, IInfluxWriterService influx)  :base(logger)
        {
            _Lux= lux;
            _Influx= influx;
        }

        public override async Task RunAsync(CancellationToken cancellationToken)
        {
            string json = await _Lux.GetInverterEnergyInfoAsync();
            Logger.LogTrace("LuxMonitor.RunAsync");
        }
    }
}
