using InfluxDB.Client;
using InfluxDB.Client.Core.Flux.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace Luxopus.Services
{
    internal static class FluxExtensions
    {
        public static DateTime ToDateTime(this Instant instant)
        {
            return DateTime.Now;
        }
    }

    /// <summary>
    /// Seems not to be provided by InfluxDB.Client so we make on rather than demand the entire config and then do .GetValue("InfluxDB:Token");.
    /// </summary>
    internal class InfluxDBSettings : Settings
    {
        public string Token { get; set; }
        public string Server { get; set; }
        public string Org { get; set; }
    }

    internal abstract class InfluxService : Service<InfluxDBSettings>
    {
        protected readonly IInfluxDBClient Client;
        private bool disposedValue;

        protected InfluxService(ILogger<InfluxService> logger, IOptions<InfluxDBSettings> settings) : base(logger, settings) {
            Client = new InfluxDBClient(Settings.Server, Settings.Token); // TODO: DI.
        }

        public async Task<List<FluxTable>> QueryAsync(string flux)
        {
            return await Client.GetQueryApi().QueryAsync(flux, Settings.Org);
        } 

        public override bool ValidateSettings()
        {
            bool ok = true;
            if (string.IsNullOrEmpty(Settings.Token))
            {
                Logger.LogError("Setting InfluxDB.Token is required.");
                ok = false;
            }
            if (string.IsNullOrEmpty(Settings.Server))
            {
                Logger.LogError("Setting InfluxDB.Server is required.");
                ok = false;
            }
            if (string.IsNullOrEmpty(Settings.Org))
            {
                Logger.LogError("Setting InfluxDB.Org is required.");
                ok = false;
            }
            return ok;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects)
                    Client.Dispose();
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

    internal interface IInfluxQueryService
    {
        Task<List<FluxTable>> QueryAsync(string flux);

        Task<List<Price>> GetPricesAsync();
    }

    internal class InfluxQueryService : InfluxService, IInfluxQueryService, IDisposable
    {

        public InfluxQueryService(ILogger<InfluxQueryService> logger, IOptions<InfluxDBSettings> settings) : base(logger, settings) { }


        private async Task<T> QueryAsync<T>(Func<QueryApi, Task<T>> action)
        {
            using var client = InfluxDBClientFactory.Create("http://localhost:8086", Settings.Token);
            var query = client.GetQueryApi();
            return await action(query);
        }

        private async Task<string> ReadFluxAsync(string name)
        {
            string resourceName = $"Luxopus.InfluxQueries.{name}";

            using (Stream stream = Assembly.GetAssembly(GetType()).GetManifestResourceStream(resourceName))
            {
                using (StreamReader reader = new StreamReader(stream))
                {
                    return await reader.ReadToEndAsync();
                }
            }
        }

        public async Task<List<Price>> GetPricesAsync()
        {
            return await Task.FromResult(new List<Price>());
            //string flux = await ReadFluxAsync("prices");

            //List<FluxTable> tables = await Client.GetQueryApi().QueryAsync(flux, Settings.Org);

            //return tables.SelectMany(table =>
            //    table.Records.Select(record =>
            //        new Price
            //        {
            //            Time = record.GetTime()?.ToDateTime(),
            //            Buy = (float)record.get("Buy"),
            //            Sell = (float)record.GetField("Sell")
            //        }));
        }
    }
}
