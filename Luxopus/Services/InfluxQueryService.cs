using InfluxDB.Client;
using InfluxDB.Client.Core.Flux.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Luxopus.Services
{
    /// <summary>
    /// Seems not to be provided by InfluxDB.Client so we make on rather than demand the entire config and then do .GetValue("InfluxDB:Token");.
    /// </summary>
    internal class InfluxDBSettings : Settings
    {
        public string Token { get; set; }
        public string Server { get; set; }
        public string Org { get; set; }
        public string Bucket { get; set; }
    }

    internal abstract class InfluxService : Service<InfluxDBSettings>
    {
        protected readonly IInfluxDBClient Client;
        private bool disposedValue;

        protected InfluxService(ILogger<InfluxService> logger, IOptions<InfluxDBSettings> settings) : base(logger, settings)
        {
            Client = new InfluxDBClient(Settings.Server, Settings.Token); // TODO: DI.
        }

        public async Task<List<FluxTable>> QueryAsync(string flux)
        {
            return await Client.GetQueryApi().QueryAsync(flux, Settings.Org);
        }

        public string Bucket { get { return Settings.Bucket; } }

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
        string Bucket { get; }
        Task<List<FluxTable>> QueryAsync(string flux);
        Task<List<FluxTable>> QueryAsync(Query query, DateTime today);

        /// <summary>
        /// Get prices starting on the afternoon of the <paramref name="day"/> through to the end of the next day.
        /// </summary>
        /// <param name="day"></param>
        /// <returns></returns>
        Task<List<HalfHour>> GetPricesAsync(DateTime day);
    }

    public enum Query
    {
        EveningSellMax,
        OvernightMin,
        MorningSellMax,
        DytimeSellMedian,
        DaytimeBuyMin
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

        public async Task<List<FluxTable>> QueryAsync(Query query, DateTime today)
        {
            string flux = await ReadFluxAsync(query.ToString());
            flux = flux.Replace("bucket: \"solar\"", $"bucket: \"{Settings.Bucket}\"");
            flux = flux.Replace("today()", $"{today.ToString("yyyy-MM-ddZ")}T00:00:00");
            return await QueryAsync(flux);

        }

        public async Task<List<HalfHour>> GetPricesAsync(DateTime day)
        {
            string flux = $@"
import ""{Settings.Bucket}""

t0 = {day.ToString("yyyy-MM-ddTHH:mm:ss")}Z
t1 = date.add(d: 36h, to: t0)

from(bucket: ""solar"")
  |> range(start: t0, stop: t1)
  |> filter(fn: (r) => r[""_measurement""] == ""prices"" and r[""fuel""] == ""electricity"")
  |> keep(columns: [""_time"", ""_value"", ""type""])";

            List<FluxTable> q = await QueryAsync(flux);
            return q[0].Records.GroupBy(z => (DateTime)z.GetValueByKey("_time"))
                .Select(z => new HalfHour()
                {
                    Start = z.Key,
                    Buy = (decimal)(z.Single(z => (string)z.GetValueByKey("type") == "buy").GetValueByKey("_value")),
                    Sell = (decimal)(z.Single(z => (string)z.GetValueByKey("type") == "sell").GetValueByKey("_value"))
                })
                .ToList();
        }
    }

    internal class HalfHour
    {
        public DateTime Start { get; set; }
        public decimal Buy { get; set; }
        public decimal Sell { get; set; }
    }

    public static class FluxExtensions
    {
        public static T GetValue<T>(this FluxRecord record, string column = "_value")
        {
            object o = record.GetValueByKey(column);
            if (o.GetType() == typeof(Instant) && typeof(T) == typeof(DateTime))
            {
                return (T)(object)((Instant)o).ToDateTimeUtc();
            }
            return (T)o;
        }

        public static IEnumerable<(DateTime, T)> GetValues<T>(this FluxTable table)
        {
            return table.Records.Select(z => (
                z.GetValue<DateTime>("_time"),
                z.GetValue<T>("_value")
            ));
        }

        public static (DateTime, T) FirstOrDefault<T>(this FluxTable table)
        {
            if (table.Records.Count == 0)
            {
                return (new List<(DateTime, T)>()).FirstOrDefault();
            }

            return table.GetValues<T>().FirstOrDefault();
        }

        //public static T First<T>(this IEnumerable<FluxTable> table)
        //{
        //    if (table.Records.Count == 0)
        //    {
        //        return (new List<(DateTime, T)>()).FirstOrDefault();
        //    }

        //    return table.GetValues<T>().FirstOrDefault();
        //}
    }
}
