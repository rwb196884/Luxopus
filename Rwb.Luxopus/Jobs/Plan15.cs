using InfluxDB.Client.Core.Flux.Domain;
using Microsoft.Extensions.Logging;
using Rwb.Luxopus.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Jobs
{
    /// <summary>
    /// <para>
    /// Plan for fixed export price -- 15p at the time of writing -- and agile import.
    /// </para>
    /// </summary>
    public class Plan15 : Planner
    {
        private const decimal _ExportPrice = 15M;

        /// <summary>
        /// Rate at which battery discharges to grid. TODO: estimate from historical data.
        /// Capacity 189 Ah * 55V ~ 10kWh so 1% is 100Wh
        /// Max charge ~ 4kW => 2.5 hours => 20% per half hour.
        /// 3kW => 3.5 hours => 15% per half hour.
        /// Capacity 315 Ah * 55V ~ 10kWh so 1% is 173Wh
        /// Max discharge to grid 3.6kW => => 1800Wh per half hour => 10% per half hour.
        /// It takes 9 half hours to fully charge the battery
        /// 
        /// TODO: estimate from data.
        /// </summary>
        const int BatteryDrainPerHalfHour = 10;

        /// <summary>
        /// Normal battery minimum allowed level. TODO: estimate from historical data.
        /// Power to house: day 9am--11pm: 250W, night 11pm--8am: 200W.
        /// If 1 battery percent is 100Wh then that's 2.5 resp. 2 percent per hour.
        /// 15 hours over night shoule be about 30% batt.
        /// </summary>
        const int BatteryMin = 30;

        private readonly ILuxService _Lux;
        private readonly IBatteryService _Batt;
        private readonly IEmailService _Email;
        private readonly IOctopusService _Octopus;

        public Plan15(
            ILogger<LuxMonitor> logger,
            ILuxService lux,
            IInfluxQueryService influxQuery,
            ILuxopusPlanService plan,
            IEmailService email,
            IBatteryService batt,
            IOctopusService octopus
            )
            : base(logger, influxQuery, plan)
        {
            _Lux = lux;
            _Email = email;
            _Batt = batt;
            _Octopus = octopus;
        }

        private List<(PeriodPlan, int)> _Plan;
        private BatteryUsageProfile _BatteryUsageProfile;

        protected override async Task WorkAsync(CancellationToken cancellationToken)
        {
            //DateTime t0 = new DateTime(2025, 12, 10, 09, 00, 00);
            DateTime t0 = DateTime.UtcNow.AddHours(-1);
            //Plan? current = PlanService.Load(t0);
            StringBuilder notes = new StringBuilder();

            DateTime start = t0.StartOfHalfHour();//.AddDays(-1);// Longest period is 5AM while 4PM (local).
            DateTime stop = (new DateTime(t0.Year, t0.Month, t0.Day, 21, 0, 0)).AddDays(1);
            TariffCode ti = await _Octopus.GetElectricityCurrentTariff(TariffType.Import, start);
            TariffCode te = await _Octopus.GetElectricityCurrentTariff(TariffType.Export, start);
            List<ElectricityPrice> prices = await InfluxQuery.GetPricesAsync(start, stop, ti.Code, te.Code);
            Plan plan = new Plan(prices);

            if (prices.Any(z => z.Sell > 0 && z.Sell != _ExportPrice))
            {
                // The 15p may have been set months and months ago!
                throw new Exception();
            }

            foreach (PeriodPlan p in plan.Plans)
            {
                p.Sell = _ExportPrice;
            }

            /*
            foreach(PeriodPlan p in plan.Plans)
            {
                if(p.Buy < _ExportPrice * 0.85M)
                {
                    p.Action = new PeriodAction()
                    {
                        ChargeFromGrid = 100,
                        DischargeToGrid = 100
                    };
                }
                else
                {
                    p.Action = GetDischargeAction(34);
                }
            }
            PlanService.Save(plan);
            _Email.SendPlanEmail(plan, notes.ToString());

            return;
            */

            // Generation prediction.
            DateTime tForecast = DateTime.Today.ToUniversalTime().AddHours(24 + 2);
            double generationPrediction = (double)(await InfluxQuery.QueryAsync(Query.PredictionToday, tForecast)).Single().Records[0].Values["_value"] / 10.0;
            double battPrediction = _Batt.CapacityKiloWattHoursToPercent(generationPrediction);
            notes.AppendLine($"Predicted generation of {generationPrediction:0.0}kWH ({battPrediction:0}%).");
            double generationMedianForMonth = (double)(await InfluxQuery.QueryAsync(Query.GenerationMedianForMonth, DateTime.UtcNow)).Single().Records[0].Values["_value"] / 10.0;
            if (generationPrediction > generationMedianForMonth)
            {
                generationPrediction = (generationPrediction + generationMedianForMonth) / 2.0;
                battPrediction = _Batt.CapacityKiloWattHoursToPercent(generationPrediction);
                notes.AppendLine($"Predicted generation of {generationPrediction:0.0}kWH ({battPrediction:0}%) adjusted towards monthly median of {generationMedianForMonth}kWH.");
            }

            PeriodPlan current = plan.Current;
            foreach (PeriodPlan p in plan.Plans)
            {
                p.Action = new PeriodAction()
                {
                    ChargeFromGrid = 0,
                    DischargeToGrid = 100
                };
            }

            // Discharge outside of generation time.
            DateTime gStart = DateTime.Today.AddHours(5); //sunrise;
            DateTime gEnd = DateTime.Today.AddHours(16); // sunset
            try
            {
                //(sunrise, _) = (await _InfluxQuery.QueryAsync(Query.Sunrise, currentPeriod.Start)).First().FirstOrDefault<long>();
                //(sunset, _) = (await _InfluxQuery.QueryAsync(Query.Sunset, currentPeriod.Start)).First().FirstOrDefault<long>();
                (gStart, _) = (await InfluxQuery.QueryAsync(Query.StartOfGeneration, current.Start)).First().FirstOrDefault<double>();
                (gEnd, _) = (await InfluxQuery.QueryAsync(Query.EndOfGeneration, current.Start)).First().FirstOrDefault<double>();
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Failed to query for sunrise and sunset / generation.");
            }

            // Usage estimate.
            List<FluxTable> bupH = await InfluxQuery.QueryAsync(Query.HourlyBatteryUse, t0);
            _BatteryUsageProfile = new BatteryUsageProfile(bupH);
            int battForUseToday = _Batt.CapacityKiloWattHoursToPercent(_BatteryUsageProfile.GetKwkh(gStart.DayOfWeek, gStart.Hour, gEnd.AddHours(1).Hour));

            foreach (PeriodPlan p in plan.Plans)
            {
                if (p.Start.TimeOfDay < gStart.TimeOfDay)
                {
                    p.Action = new PeriodAction()
                    {
                        ChargeFromGrid = 0,
                        DischargeToGrid = _Batt.BatteryMinimumLimit + battForUseToday
                    };
                }
                else if (p.Start.TimeOfDay > gStart.TimeOfDay)
                {
                    p.Action = new PeriodAction()
                    {
                        ChargeFromGrid = 0,
                        DischargeToGrid = _Batt.BatteryMinimumLimit + battForUseToday * (16 - (p.Start.Hour - 8)) / 16
                    };
                }
                else
                {
                    p.Action = new PeriodAction()
                    {
                        ChargeFromGrid = 0,
                        DischargeToGrid = 8
                    };
                }
            }

            int battLevel = await InfluxQuery.GetBatteryLevelAsync(DateTime.UtcNow);
            battLevel = battLevel < _Batt.BatteryMinimumLimit ? _Batt.BatteryMinimumLimit : battLevel;
            int halfHourBattPercentOut = _Batt.CapacityKiloWattHoursToPercent(0.5 * 3.6);
            int halfHourBattPercentIn = _Batt.CapacityKiloWattHoursToPercent(0.5 * 4);
            Recompute(plan, battLevel, generationPrediction);

            bool a = TryBuyMore(plan, gStart, gEnd, battLevel, generationPrediction);
            bool b = TrySellMore(plan, gStart, gEnd, battLevel, generationPrediction);
            while (a || b)
            {
                a = TryBuyMore(plan, gStart, gEnd, battLevel, generationPrediction);
                b = TrySellMore(plan, gStart, gEnd, battLevel, generationPrediction);
            }

            bool batteryConditioningRequired = false;
            try
            {
                Dictionary<string, string> settings = await _Lux.GetSettingsAsync();
                (_, int bcSince, int bcPeriod) = _Lux.GetBatteryCalibration(settings);
                if (bcSince > bcPeriod - 3)
                {
                    notes.AppendLine($"Battery calibration: {bcSince} / {bcPeriod}. *** Calibration is required. ***");
                    batteryConditioningRequired = true;
                }
                else
                {
                    notes.AppendLine($"Battery calibration: {bcSince} / {bcPeriod}.");
                }
            }
            catch
            {
                notes.AppendLine($"*** Failed to get battery calibration info. ***");
            }

            // Tidy.
            foreach (PeriodPlan p in plan.Plans)
            {
                (PeriodPlan? q, int qb) = _Plan.SingleOrDefault(z => z.Item1 == p);
                if (q != null)
                {
                    if (q.Action.DischargeToGrid < 100)
                    {
                        q.Action.DischargeToGrid = qb - 3 < _Batt.BatteryMinimumLimit ? _Batt.BatteryMinimumLimit : qb - 3;
                    }
                    else if (q.Action.ChargeFromGrid > 0)
                    {
                        q.Action.ChargeFromGrid = qb ;
                    }
                }
            }

            // Save.
            PlanService.Save(plan);
            _Email.SendPlanEmail(plan, notes.ToString());
        }

        private int GetBattLevelAfterPeriod(PeriodPlan p, int startLevel, double generationPrediction)
        {
            int halfHourBattPercentOut = _Batt.CapacityKiloWattHoursToPercent(0.5 * 3.6);
            int halfHourBattPercentIn = _Batt.CapacityKiloWattHoursToPercent(0.5 * 4);
            int halfHourUsePercent = _Batt.CapacityKiloWattHoursToPercent(0.5 * _BatteryUsageProfile.GetKwkh(p.Start.DayOfWeek, p.Start.Hour, p.Start.AddHours(1).Hour));
            int battLevelEnd = startLevel
                + (p.Action.ChargeFromGrid > 0 ? halfHourBattPercentIn : 0)
                - (p.Action.ChargeFromGrid == 0 ? halfHourUsePercent : 0)
                - (startLevel > p.Action.DischargeToGrid ? (startLevel - halfHourBattPercentOut < p.Action.DischargeToGrid ? startLevel - p.Action.DischargeToGrid : halfHourBattPercentOut) : 0);
            if (battLevelEnd < 5) { return 5; }
            if (battLevelEnd > 100) { return 100; }
            return battLevelEnd;
        }

        private void Recompute(Plan plan, int batteryLevel, double generationPrediction)
        {
            PeriodPlan start = plan.Plans.OrderBy(z => z.Start).First();// plan.Current
            _Plan = new List<(PeriodPlan, int)>() { (start, batteryLevel) };
            int b = batteryLevel;
            foreach (PeriodPlan p in plan.Plans.Where(z => z.Start > start.Start).OrderBy(z => z.Start))
            {
                b = GetBattLevelAfterPeriod(p, b, generationPrediction);
                _Plan.Add((p, b));
            }
        }

        private bool TryBuyMore(Plan plan, DateTime gStart, DateTime gEnd, int battLevel, double generationPrediction)
        {
            bool changes = false;
            PeriodPlan current = plan.Current; //.Plans.OrderBy(z => z.Start).First();
            int halfHourBattPercentOut = _Batt.CapacityKiloWattHoursToPercent(0.5 * 3.6);
            int halfHourBattPercentIn = _Batt.CapacityKiloWattHoursToPercent(0.5 * 4);
            decimal pBuy = _Plan.Where(z => z.Item1.Action.ChargeFromGrid == 0)
                .Select(z => z.Item1.Buy)
                .Min();
            // Can buy to sell.
            while (pBuy < 15M * 0.89M)
            {
                foreach (PeriodPlan p in plan.Plans.Where(z => z.Buy == pBuy && z.Start >= current.Start))
                {
                    PeriodPlan? q = plan.Plans.GetPrevious(p);
                    int battPrevious = 100;
                    if (q != null && _Plan.Any(z => z.Item1 == q))
                    {
                        battPrevious = _Plan.Single(z => z.Item1 == q).Item2;
                    }
                    int batt = _Plan.Single(z => z.Item1 == p).Item2;

                    bool fcf = _Plan.Any(z => z.Item1.Start > p.Start && z.Item2 == 100);

                    if (!fcf && battPrevious + halfHourBattPercentIn < 100)
                    {
                        p.Action.ChargeFromGrid = 100;
                        p.Action.DischargeToGrid = 100;
                        changes = true;
                    }
                }
                Recompute(plan, battLevel, generationPrediction);
                pBuy = _Plan.Where(z => z.Item1.Action.ChargeFromGrid == 0 && z.Item1.Buy > pBuy)
                    .Select(z => z.Item1.Buy)
                    .Min();
            }

            // Need to buy to use.
            while (_Plan.Any(z => z.Item1.Action.DischargeToGrid < 100 && z.Item1.Action.DischargeToGrid > z.Item2))
            {
                (PeriodPlan p, int pb) = _Plan.Where(z => z.Item1.Action.DischargeToGrid < 100 && z.Item1.Action.DischargeToGrid > z.Item2).OrderBy(z => z.Item1.Start).First();
                 
                PeriodPlan? r = plan.Plans.Where(z => z.Start < p.Start && z.Action.DischargeToGrid < 100 && !plan.Plans.Any(y => y.Start >= z.Start && y.Start < p.Start && y.Action.ChargeFromGrid > 0)).OrderBy(z => z.Start).FirstOrDefault();
                if (r != null)
                {
                    // Sell less.
                    r.Action.DischargeToGrid = 100;
                }
                else
                {
                    // Buy more.
                    (PeriodPlan? p100, _) = _Plan.Where(z => z.Item1.Start < p.Start && z.Item2 == 100).OrderBy(z => z.Item1.Start).LastOrDefault();
                    // Buy at the cheapest preceeding half hour.
                    PeriodPlan q = plan.Plans.Where(z => z.Start <= p.Start && z.Action.ChargeFromGrid == 0 && (p100 == null || z.Start > p100.Start)).OrderBy(z => z.Buy).First();

                    q.Action.ChargeFromGrid = 100;
                    q.Action.DischargeToGrid = 100;
                }
                Recompute(plan, battLevel, generationPrediction);
                changes = true;
            }

            return changes;
        }

        private bool TrySellMore(Plan plan, DateTime gStart, DateTime gEnd, int battLevel, double generationPrediction)
        {
            bool changes = false;
            DateTime last = plan.Plans.OrderBy(z => z.Start).Last().Start.AddHours(1);
            // Try sell less, actually, if the battery gets too low.
            while (_Plan.Any(z => z.Item1.Start < last && z.Item1.Action.DischargeToGrid < 100 && z.Item2 < z.Item1.Action.DischargeToGrid))
            {
                (PeriodPlan? p, int pb) = _Plan.Where(z => z.Item1.Start < last && z.Item1.Action.DischargeToGrid < 100 && z.Item2 < z.Item1.Action.DischargeToGrid).OrderBy(z => z.Item1.Start).Last();
                PeriodPlan? q = plan.Plans.GetPrevious(p);
                PeriodPlan? dischargeRunStart = q;
                while (q != null && q.Action.DischargeToGrid < 100)
                {
                    q = plan.Plans.GetPrevious(q);
                    if (q != null && q.Action.DischargeToGrid < 100) { dischargeRunStart = q; }
                }
                if (dischargeRunStart != null && dischargeRunStart.Action.DischargeToGrid < 100)
                {
                    dischargeRunStart.Action.DischargeToGrid = 100;
                    changes = true;
                }
                else
                {
                    last = p.Start;
                }
                Recompute(plan, battLevel, generationPrediction);
            }

            //foreach (PeriodPlan p in plan.Plans)
            //{
            //    // We can try to sell more if the battery estimate is above the discharge level.
            //    // 
            //}
            return changes;
        }
    }
}
