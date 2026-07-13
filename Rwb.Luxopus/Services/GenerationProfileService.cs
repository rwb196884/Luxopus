using InfluxDB.Client.Core.Flux.Domain;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Services
{
    public class GenerationProfileService
    {
        private readonly ILogger _Logger;
        private readonly IInfluxQueryService _InfluxQuery;

        private DateTime _Computed;
        private Dictionary<int, int> _GenerationProfile;

        public GenerationProfileService(
        ILogger<BatteryTargetService> logger, IInfluxQueryService influxQuery)
        {
            _Logger = logger;
            _InfluxQuery = influxQuery;
        }

        private async Task ComputeAsync()
        {
            if (_GenerationProfile == null || _Computed < DateTime.Now.AddHours(-20))
            {
                List<FluxTable> gp = await _InfluxQuery.QueryAsync(Query.GenerationProfile, DateTime.Today);
                _GenerationProfile = gp.First().Records.ToDictionary(
                    z => z.GetValue<int>("h"),
                    z => z.GetValue<int>("_value")
                    );
            }
        }

        public async Task<double> EstimateAsync(DateTime start, DateTime finish, double generationPredictionKwH)
        {
            await ComputeAsync();
            return Enumerable.Range(start.TimeOfDay.Hours, finish.TimeOfDay.Hours)
                .Sum()
                * generationPredictionKwH / 100.0;
        }
    }
}