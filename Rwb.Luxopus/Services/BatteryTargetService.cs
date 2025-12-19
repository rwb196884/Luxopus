using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Services
{
    public class BatteryTargetInfo
    {
        public int BatteryLevelCurrent { get; set; }
        public int BatteryLevelEnd { get; set; }
        public ScaleMethod ScaleMethod { get; set; }
        public int BatteryTargetF { get; set; }
        public int BatteryTargetL { get; set; }
        public int BatteryTargetS { get; set; }
        public int BatteryTarget
        {
            get
            {
                switch (ScaleMethod)
                {
                    case ScaleMethod.Fast:
                        return BatteryTargetF;
                    case ScaleMethod.Linear:
                        return BatteryTargetL;
                    case ScaleMethod.Slow:
                        return BatteryTargetS;
                    default:
                        throw new NotImplementedException();
                }
            }
        }

        public double PredictionKWh { get; set; }
        public int PredictionBatteryPercent { get; set; }

        public DateTime GenerationStart { get; set; }
        public DateTime GenerationEnd { get; set; }

        public string TargetDescription {  get { return $"{BatteryTarget}% ({BatteryTargetS}% < {BatteryTargetL}% < {BatteryTargetF}%)"; } }
    }

    public class BatteryTargetService
    {
        private readonly ILogger _Logger;
        private readonly IInfluxQueryService _InfluxQuery;
        private readonly IBatteryService _Batt;
        private readonly ILuxopusPlanService _Plans;

        public BatteryTargetService(
            ILogger<BatteryTargetService> logger, IInfluxQueryService influxQuery, IBatteryService batt, ILuxopusPlanService plans)
        {
            _Logger = logger;
            _InfluxQuery = influxQuery;
            _Batt = batt;
            _Plans = plans;
        }

        public int DefaultBatteryLevelEnd
        {
            get
            {
                int battLevelEnd = _Batt.BatteryMinimumLimit + _Batt.CapacityKiloWattHoursToPercent(3 * 3.6) + 8;
                battLevelEnd = battLevelEnd > 100 ? 100 : battLevelEnd;
                return battLevelEnd;
            }
        }

        public async Task<BatteryTargetInfo> Compute(Plan plan, int battLevelEnd = 101)
        {
            if(battLevelEnd == 101)
            {
                battLevelEnd = DefaultBatteryLevelEnd;
            }

            BatteryTargetInfo info = new BatteryTargetInfo();

            info.BatteryLevelCurrent = await _InfluxQuery.GetBatteryLevelAsync(DateTime.UtcNow);

            (_, double prediction) = (await _InfluxQuery.QueryAsync(Query.PredictionToday, plan.Current.Start)).First().FirstOrDefault<double>();
            info.PredictionKWh = prediction / 10;
            info.PredictionBatteryPercent = _Batt.CapacityKiloWattHoursToPercent(info.PredictionKWh);

            DateTime gStart = DateTime.Today.AddHours(5); //sunrise;
            DateTime gEnd = DateTime.Today.AddHours(16); // sunset
            try
            {
                //(sunrise, _) = (await _InfluxQuery.QueryAsync(Query.Sunrise, currentPeriod.Start)).First().FirstOrDefault<long>();
                //(sunset, _) = (await _InfluxQuery.QueryAsync(Query.Sunset, currentPeriod.Start)).First().FirstOrDefault<long>();
                (gStart, _) = (await _InfluxQuery.QueryAsync(Query.StartOfGeneration, plan.Current.Start)).First().FirstOrDefault<double>();
                (gEnd, _) = (await _InfluxQuery.QueryAsync(Query.EndOfGeneration, plan.Current.Start)).First().FirstOrDefault<double>();
            }
            catch (Exception e)
            {
                _Logger.LogError(e, "Failed to query for sunrise and sunset / generation.");
            }
            info.GenerationStart = gStart;
            info.GenerationEnd = gEnd;

            (DateTime _, long generationMax) = //(DateTime.Now, 0);
    (await _InfluxQuery.QueryAsync(@$"
from(bucket: ""solar"")
  |> range(start: {plan.Current.Start.ToString("yyyy-MM-ddTHH:mm:00Z")}, stop: now())
  |> filter(fn: (r) => r[""_measurement""] == ""inverter"" and r[""_field""] == ""generation"")
  |> max()")).First().FirstOrDefault<long>();

            long generationRecentMax = (await _InfluxQuery.QueryAsync(@$"
from(bucket: ""solar"")
  |> range(start: -45m, stop: now())
  |> filter(fn: (r) => r[""_measurement""] == ""inverter"" and r[""_field""] == ""generation"")
  |> max()")
               ).First().Records.First().GetValue<long>();

            double generationRecentMean = (await _InfluxQuery.QueryAsync(@$"
from(bucket: ""solar"")
  |> range(start: -45m, stop: now())
  |> filter(fn: (r) => r[""_measurement""] == ""inverter"" and r[""_field""] == ""generation"")
  |> mean()")
               ).First().Records.First().GetValue<double>();

            double generationMeanDifference = (await _InfluxQuery.QueryAsync(@$"
from(bucket: ""solar"")
  |> range(start: -45m, stop: now())
  |> filter(fn: (r) => r[""_measurement""] == ""inverter"" and r[""_field""] == ""generation"")
  |> difference()
  |> mean()")
               ).First().Records.First().GetValue<double>();

            // Get fully charged before the discharge period.
            DateTime tBattChargeFrom = gStart > plan.Current.Start ? gStart : plan.Current.Start;

            int battLevelStart = await _InfluxQuery.GetBatteryLevelAsync(plan.Current.Start);
            DateTime nextPlanCheck = DateTime.UtcNow.StartOfHalfHour().AddMinutes(30);

            int battLevelTargetF = Scale.Apply(tBattChargeFrom, (gEnd < plan.Next.Start ? gEnd : plan.Next.Start).AddHours(generationMax > 3700 && DateTime.UtcNow < plan.Next.Start.AddHours(-2) ? 0 : -1), nextPlanCheck, battLevelStart, battLevelEnd, ScaleMethod.Fast);
            int battLevelTargetL = Scale.Apply(tBattChargeFrom, (gEnd < plan.Next.Start ? gEnd : plan.Next.Start).AddHours(generationMax > 3700 && DateTime.UtcNow < plan.Next.Start.AddHours(-2) ? 0 : -1), nextPlanCheck, battLevelStart, battLevelEnd, ScaleMethod.Linear);
            int battLevelTargetS = Scale.Apply(tBattChargeFrom, (gEnd < plan.Next.Start ? gEnd : plan.Next.Start).AddHours(generationMax > 3700 && DateTime.UtcNow < plan.Next.Start.AddHours(-2) ? 0 : -1), nextPlanCheck, battLevelStart, battLevelEnd, ScaleMethod.Slow);

            ScaleMethod sm = ScaleMethod.Linear;
            if (info.BatteryLevelCurrent < battLevelTargetS && generationRecentMean < 1500)
            {
                sm = ScaleMethod.Fast;
            }
            else if (prediction < _Batt.CapacityPercentToKiloWattHours(90))
            {
                sm = ScaleMethod.Fast;
            }
            else if (generationRecentMean < 2000)
            {
                sm = ScaleMethod.Linear;
            }
            else if (prediction > _Batt.CapacityPercentToKiloWattHours(200) && generationRecentMean > 2500 && plan.Current.Start.Month >= 4 && plan.Current.Start.Month <= 8)
            {
                // High prediction / good day and summer: charge slowly.
                sm = ScaleMethod.Slow;
            }
            info.ScaleMethod = sm;
            info.BatteryTargetF = battLevelTargetF;
            info.BatteryTargetL = battLevelTargetL;
            info.BatteryTargetS = battLevelTargetS;
            info.BatteryLevelEnd = battLevelEnd;

            return info;
        }
    }
}
