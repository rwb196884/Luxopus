using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Services
{
    public class BatteryTargetInfo
    {
        public int BatteryLevelStart { get; set; }
        public int BatteryLevelCurrent { get; set; }
        public int BatteryLevelEnd { get; set; }
        public int BatteryTarget { get; set; }

        public int HeadroomTotal { get; set; }
        public int HeadroomScaled { get; set; }

        public double PredictionKWh { get; set; }
        public int PredictionBatteryPercent { get; set; }

        public DateTime GenerationStart { get; set; }
        public DateTime GenerationEnd { get; set; }

        public DateTime Start { get; set; }
        public DateTime End { get; set; }

        public double HoursToCharge { get; set; }

        public double ChargeNeededkWH { get; set; }
        public double ChargeRateNeededkW { get; set; }
        public int ChargeRateNeededPercent { get; set; }

        public double ChargeNeededHkWH { get; set; }
        public double ChargeRateNeededHkW { get; set; }
        public int ChargeRateNeededHPercent { get; set; }

        public string TargetDescription { get { return $"{BatteryTarget}%"; } }

        public string ChargeDescription
        {
            get
            {
                if(BatteryLevelCurrent >= BatteryTarget + HeadroomScaled)
                {
                    return "Ahead of target and headroom full.";
                }
                else if( BatteryLevelCurrent > BatteryTarget)
                {
                    return $"{ChargeNeededHkWH:0.0}kWh needed to get from {BatteryLevelCurrent}% to {BatteryLevelEnd + HeadroomScaled}% in {HoursToCharge:0.0} hours until {End:HH:mm} (mean rate {ChargeRateNeededHkW:0.0}kW -> {ChargeRateNeededHPercent}%).";
                }
                return $"{ChargeNeededkWH:0.0}kWh needed to get from {BatteryLevelCurrent}% to {BatteryLevelEnd}% in {HoursToCharge:0.0} hours until {End:HH:mm} (mean rate {ChargeRateNeededkW:0.0}kW -> {ChargeRateNeededPercent}%).";
            }
        }
    }

    public class BatteryTargetService
    {
        private readonly ILogger _Logger;
        private readonly IInfluxQueryService _InfluxQuery;
        private readonly IBatteryService _Batt;
        //private readonly ILuxopusPlanService _Plans;
        private readonly GenerationProfileService _GenerationProfileService;

        public BatteryTargetService(
            ILogger<BatteryTargetService> logger, IInfluxQueryService influxQuery, IBatteryService batt/*, ILuxopusPlanService plans*/, GenerationProfileService generationProfileService)
        {
            _Logger = logger;
            _InfluxQuery = influxQuery;
            _Batt = batt;
            _GenerationProfileService = generationProfileService;
            //_Plans = plans;
        }

        private int DefaultBatteryLevelEnd
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
            if (battLevelEnd == 101)
            {
                battLevelEnd = DefaultBatteryLevelEnd;
            }

            BatteryTargetInfo info = new BatteryTargetInfo();

            (int battStart, _) = await _InfluxQuery.GetBatteryStartLevelAsync();
            info.BatteryLevelStart = battStart;

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

            info.Start = gStart > plan.Current.Start ? gStart : plan.Current.Start;

            int battLevelStart = await _InfluxQuery.GetBatteryLevelAsync(plan.Current.Start);
            DateTime nextPlanCheck = DateTime.UtcNow.StartOfHalfHour().AddMinutes(30);

            info.End = (gEnd < plan.Next!.Start ? gEnd : plan.Next!.Start);//.AddHours(generationMax > 3700 && DateTime.UtcNow < plan.Next.Start.AddHours(-2) ? 0 : -1);

            info.BatteryLevelEnd = battLevelEnd;

            info.HeadroomTotal = 100 - info.BatteryLevelEnd;
            info.HeadroomScaled = Scale.Apply(info.Start, info.End, DateTime.UtcNow, 0, info.HeadroomTotal, ScaleMethod.Linear);

            info.HoursToCharge = ((info.GenerationEnd < plan.Next.Start ? info.GenerationEnd : plan.Next.Start) - DateTime.UtcNow).TotalHours;

            // To target.
            int powerRequiredPercent = info.BatteryLevelEnd - info.BatteryLevelCurrent;
            powerRequiredPercent = powerRequiredPercent < 0 ? 5 : powerRequiredPercent;
            info.ChargeNeededkWH = _Batt.CapacityPercentToKiloWattHours(powerRequiredPercent);

            info.ChargeRateNeededkW = info.ChargeNeededkWH / info.HoursToCharge;
            info.ChargeRateNeededPercent = _Batt.TransferKiloWattsToPercent(info.ChargeRateNeededkW);

            // To headroom scaled.
            powerRequiredPercent = info.BatteryLevelEnd + info.HeadroomScaled - info.BatteryLevelCurrent;
            powerRequiredPercent = powerRequiredPercent < 0 ? 5 : powerRequiredPercent;
            info.ChargeNeededHkWH = _Batt.CapacityPercentToKiloWattHours(powerRequiredPercent);

            info.ChargeRateNeededHkW = info.ChargeNeededHkWH / info.HoursToCharge;
            info.ChargeRateNeededHPercent = _Batt.TransferKiloWattsToPercent(info.ChargeRateNeededHkW);

            // Calculate target from generation profile.
            info.BatteryTarget = await _GenerationProfileService.TargetAsync(info.Start, info.End, nextPlanCheck, info.BatteryLevelStart, info.BatteryLevelEnd);

            return info;
        }
    }
}
