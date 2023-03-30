using Luxopus.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Luxopus.Jobs
{
    internal class OctopusMeters : Job
    {
        private const string Measurement = "meters";

        private readonly IOctopusService _Octopus;
        private readonly IInfluxWriterService _Influx;

        public OctopusMeters(ILogger<LuxMonitor> logger, IOctopusService octopusService, IInfluxWriterService influx) : base(logger)
        {
            _Octopus = octopusService;
            _Influx = influx;
        }

        public override async Task RunAsync(CancellationToken cancellationToken)
        {
            foreach (string mpan in await _Octopus.GetElectricityMeterPoints())
            {
                foreach (string serialNumber in await _Octopus.GetElectricityMeters(mpan))
                {
                    IEnumerable<MeterReading> m = await _Octopus.GetElectricityMeterReadings(mpan, serialNumber, DateTime.Now.AddDays(-7), DateTime.Now);
                }
            }
        }
    }
}
