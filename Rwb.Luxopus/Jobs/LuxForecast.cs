using Microsoft.Extensions.Logging;
using Rwb.Luxopus.Services;
using System.Threading;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Jobs
{
    /// <summary>
    /// Get LUX's forecast. It seems that it can update sometimes.
    /// </summary>
    public class LuxForecast : Job
    {
        private const string Measurement = "forecast";

        private readonly ILuxService _Lux;
        private readonly IInfluxWriterService _Influx;
        private readonly IEmailService _Email;

        public LuxForecast(ILogger<LuxMonitor> logger, ILuxService lux, IInfluxWriterService influx, IEmailService email)  :base(logger)
        {
            _Lux= lux;
            _Influx= influx;
            _Email = email;
        }

        protected override async Task WorkAsync(CancellationToken cancellationToken)
        {
            LineDataBuilder lines = new LineDataBuilder();
            (double today, double tomorrow) = await _Lux.Forecast();
            lines.Add(Measurement, "today", today);
            lines.Add(Measurement, "tomorrow", tomorrow);
            await _Influx.WriteAsync(lines);
        }
    }
}
