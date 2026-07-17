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
        private Dictionary<int, double> _GenerationProfile;

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
                    z =>z.GetValue<int>("h"),
                    z =>z.GetValue<double>("_value")
                    );
                _Computed = DateTime.Now;
            }
        }

        private double Minutes(DateTime target)
        {
            return _GenerationProfile[target.Hour] * Convert.ToDouble(target.Minute) / 60.0;
        }


        private double Sum(DateTime start, DateTime finish)
        {
            double h = _GenerationProfile.Where(z => z.Key >= start.Hour && z.Key < finish.Hour).Select(z => z.Value).Sum();
            double hBefore = Minutes(start);
            double hAfter = Minutes(finish);
            return h + hAfter - hBefore;
        }

        public async Task<int> TargetAsync(
            DateTime start, DateTime finish, DateTime target,
            int levelStart, int levelEnd)
        {
            await ComputeAsync();

            double t = Sum(start, finish);
            double tt = Sum(start, target);
            double tq = tt / t;
            return levelStart + Convert.ToInt32(tq * Convert.ToDouble(levelEnd - levelStart));
        }
    }
}