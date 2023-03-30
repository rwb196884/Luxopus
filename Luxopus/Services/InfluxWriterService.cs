using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Threading.Tasks;

namespace Luxopus.Services
{
    internal interface IInfluxWriterService
    {
        Task WriteAsync(string lineData);
    }

    internal class InfluxWriterService : InfluxService, IInfluxWriterService
    {
        public InfluxWriterService(ILogger<InfluxWriterService> logger, IOptions<InfluxDBSettings> settings) : base(logger, settings) { }

        public async Task WriteAsync(string lineData)
        {
            throw new NotImplementedException();
        }
    }
}
