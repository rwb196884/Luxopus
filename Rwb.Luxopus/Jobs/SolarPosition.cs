using Microsoft.Extensions.Logging;
using Rwb.Luxopus.Services;
using System.Threading;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Jobs
{
    public class SolarPosition : Job
    {
        private readonly ISunService _Sun;
        private readonly IInfluxWriterService _InfluxWrite;

        public SolarPosition(ILogger<LuxMonitor> logger, ISunService sun, IInfluxWriterService influx) : base(logger)
        {
            _Sun = sun;
            _InfluxWrite = influx;
        }

        protected override async Task WorkAsync(CancellationToken cancellationToken)
        {
            LineDataBuilder lines = new LineDataBuilder();
            lines.Add("sun", "elevation", _Sun.GetSunSolarElevationAngle());
            lines.Add("sun", "azimuth", _Sun.GetSunSolarAzimuth());

            await _InfluxWrite.WriteAsync(lines);
        }
    }
}
