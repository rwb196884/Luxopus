using Microsoft.Extensions.Options;
using InfluxDB.Client;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.ComponentModel.DataAnnotations.Schema;
using InfluxDB.Client.Core.Flux.Domain;
using System.Linq;
using NodaTime;

namespace Luxopus
{
    internal class Price
    {
        DateTime Time { get; set; }
        public float Buy { get; set; }
        public float Sell { get; set; }
    }
    
    internal static class FluxExtensions
    {
        public static DateTime ToDateTime(this Instant instant)
        {
            return DateTime.Now;
        }
    }

    internal class InfluxQueryService : IDisposable
    {
        private readonly InfluxDBSettings _AppSettings;
        private readonly IInfluxDBClient _Client;
        private bool disposedValue;

        public InfluxQueryService(IOptions<InfluxDBSettings> settings)
        {
            _AppSettings = settings.Value;
            _Client = new InfluxDBClient(_AppSettings.Server, _AppSettings.Token); // TODO: DI.
        }

        private async Task<T> QueryAsync<T>(Func<QueryApi, Task<T>> action)
        {
            using var client = InfluxDBClientFactory.Create("http://localhost:8086", _AppSettings.Token);
            var query = client.GetQueryApi();
            return await action(query);
        }

        private async Task<string> ReadFluxAsync(string name)
        {
            string resourceName = $"Luxopus.InfluxQueries.{name}";

            using (Stream stream = Assembly.GetAssembly(this.GetType()).GetManifestResourceStream(resourceName))
            {
                using (StreamReader reader = new StreamReader(stream))
                {
                    return await reader.ReadToEndAsync();
                }
            }
        }

        public async Task<List<Price>> GetPrices()
        {
            string flux = await ReadFluxAsync("prices");

            List<FluxTable> tables = await _Client.GetQueryApi().QueryAsync(flux, _AppSettings.Org);

                return tables.SelectMany(table =>
                    table.Records.Select(record =>
                        new Price
                        {
                            Time = record.GetTime()?.ToDateTime(),
                            Buy = (float)record.get("Buy"),
                            Sell = (float)record.GetField("Sell")
                        }));
=        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects)
                    _Client.Dispose();
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                disposedValue = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~InfluxQueryService()
        // {
        //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
