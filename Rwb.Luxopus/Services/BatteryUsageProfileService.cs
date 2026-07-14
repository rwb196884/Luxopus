using InfluxDB.Client.Core.Flux.Domain;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Services
{
    public class BatteryUsageProfileService
    {
        private readonly ILogger _Logger;
        private readonly IInfluxQueryService _InfluxQuery;

        private DateTime _Computed;
        private Dictionary<DayOfWeek, Dictionary<int, double>> _DailyHourly;

        public BatteryUsageProfileService(
ILogger<BatteryTargetService> logger, IInfluxQueryService influxQuery)
        {
            _Logger = logger;
            _InfluxQuery = influxQuery;
        }

        private async Task ComputeAsync()
        {
            if (_DailyHourly == null || _Computed < DateTime.Now.AddHours(-20))
            {
                List<FluxTable> bupH = await _InfluxQuery.QueryAsync(Query.HourlyBatteryUse, DateTime.Now);
                _DailyHourly = bupH.Single().Records.GroupBy(z => z.GetValue<long>("d"))
                .ToDictionary(
                    z => (DayOfWeek)Convert.ToInt32(z.Key),
                    z => z.ToDictionary(
                        y => Convert.ToInt32(y.GetValue<long>("h")),
                        y => y.GetValue<double>("_value")
                    )
                );
                _Computed = DateTime.Now;
            }
        }

        public async Task<double> GetKwkhAsync(DayOfWeek day, int hourFrom, int hourTo)
        {
            await ComputeAsync();

            if (hourFrom < hourTo)
            {
                return _DailyHourly
                    .Single(z => z.Key == day).Value
                    .Where(z => z.Key >= hourFrom && z.Key < hourTo).Select(z => z.Value).Sum() / 1000.0;
            }
            return _DailyHourly
                .Single(z => z.Key == day).Value
                .Where(z => z.Key >= hourFrom || z.Key < hourTo).Select(z => z.Value).Sum() / 1000.0;
        }

        // TODO: disaggregate by day of week.
    }
}
