using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Services
{
    public interface IGenerationForecastService
    {
        /// <summary>
        /// Forecast generation for day.
        /// </summary>
        /// <param name="for"></param>
        /// <returns></returns>
        Task<int> GetForecastAsync(DateTime @for);

        /// <summary>
        /// Actual generation at time.
        /// </summary>
        /// <param name="at"></param>
        /// <returns></returns>
        Task<int> GetActualAsync(DateTime at);

    }

    public class GenerationForecastSettings : Settings { }


    public class GenerationForecastService : Service<GenerationForecastSettings>, IGenerationForecastService
    {
        private readonly IInfluxQueryService _Query;
        public GenerationForecastService(ILogger<GenerationForecastService> logger, IOptions<GenerationForecastSettings> settings, IInfluxQueryService query) : base(logger, settings)
        {
            _Query = query;
        }

        public override bool ValidateSettings()
        {
            return true;
        }

        public async Task<int> GetForecastAsync(DateTime @for)
        {
            (DateTime _, int f) = (await _Query.QueryAsync(Query.SolcastToday, @for)).First().FirstOrDefault<int> ();
            return f;
        }

        public async Task<int> GetActualAsync(DateTime at)
        {
            return 0;
        }

        private List<(DateTime, double)> GetGenerationProfileAsync()
        {
            return new List<(DateTime, double)>();
        }

        private async Task<(DateTime, double)> Generation(DateTime now)
        {
            // TODO: a prediction!
            // Historical data shows relative strenght each hour over the day -- divide the daily forecast total.
            // And use total generated so far vs. forecast to estimate whether forecast is over or under, and adjust.
            string query = @$"
from(bucket: ""solar"")
  |> range(start: {now.AddHours(-2):yyyy-MM-ddTHH:00:00Z}, stop: now())
  |> filter(fn: (r) => r[""_measurement""] == ""inverter"" and r[""_field""] == ""generation"")
  |> filter(fn: (r) => r._value > 0)
  |> median()
  |> map(fn: (r) => ({{r with _time: {now:yyyy-MM-ddT00:00:00Z}}}))
";
            return (await _Query.QueryAsync(query)).First().FirstOrDefault<double>();
        }
    }
}
