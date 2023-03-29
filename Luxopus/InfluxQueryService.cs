using Microsoft.Extensions.Options;
using InfluxDB.Client;
using System.Threading.Tasks;
using System;

namespace Luxopus
{
    /// <summary>
    /// Seems not to be provided by InfluxDB.Client so we make on rather than demand the entire config and then do .GetValue("InfluxDB:Token");.
    /// </summary>
    internal class InfluxDBSettings
    {
        public string Token { get; set; }
        public string Server { get; set; }
    }

    internal class InfluxQueryService
    {
        private readonly InfluxDBSettings _AppSettings;

        public InfluxQueryService(IOptions<InfluxDBSettings> settings)
        {
            _AppSettings = settings.Value;
        }

        public async Task<T> QueryAsync<T>(Func<QueryApi, Task<T>> action)
        {
            using var client = InfluxDBClientFactory.Create("http://localhost:8086", _AppSettings.Token);
            var query = client.GetQueryApi();
            return await action(query);
        }
    }
}
