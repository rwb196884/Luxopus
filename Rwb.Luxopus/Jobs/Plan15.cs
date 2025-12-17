using InfluxDB.Client.Core.Flux.Domain;
using Microsoft.Extensions.Logging;
using Rwb.Luxopus.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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
            int battPrediction = _Batt.CapacityKiloWattHoursToPercent(generationPrediction);
            notes.AppendLine($"Predicted generation of {generationPrediction:0.0}kWH ({battPrediction:0}%).");
            double generationMedianForMonth = (double)(await InfluxQuery.QueryAsync(Query.GenerationMedianForMonth, DateTime.UtcNow)).Single().Records[0].Values["_value"] / 10.0;
            if (generationPrediction > generationMedianForMonth)
            {
                generationPrediction = (generationPrediction + generationMedianForMonth) / 2.0;
                battPrediction = _Batt.CapacityKiloWattHoursToPercent(generationPrediction);
                notes.AppendLine($"Predicted generation of {generationPrediction:0.0}kWH ({battPrediction:0}%) adjusted towards monthly median of {generationMedianForMonth}kWH.");
            }

            int battLevel = await InfluxQuery.GetBatteryLevelAsync(DateTime.UtcNow);
            battLevel = battLevel < _Batt.BatteryMinimumLimit ? _Batt.BatteryMinimumLimit : battLevel;

            PeriodPlan current = plan.Current;
            foreach (PeriodPlan p in plan.Plans)
            {
                p.Action = new PeriodAction()
                {
                    ChargeFromGrid = 0,
                    DischargeToGrid = 100
                };
                if (p.Start < current.Start) { p.Battery = -1; }
            }
            current.Battery = battLevel;

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

            int battExportable = _Batt.CapacityKiloWattHoursToPercent(3.6 * (gEnd - gStart).TotalHours * 0.9);
            int battMax = 100;
            if (battPrediction > battExportable)
            {
                battMax -= battExportable - battPrediction;
            }

            foreach (PeriodPlan p in plan.Plans)
            {
                // Try to keep the battery full but with space for over generation.
                if (p.Buy < _ExportPrice * 0.85M)
                {
                    p.Action = new PeriodAction()
                    {
                        ChargeFromGrid = 100,
                        DischargeToGrid = 100
                    };
                }
                else if (p.Start < gEnd && battMax < 100)
                {
                    p.Action = new PeriodAction()
                    {
                        ChargeFromGrid = 0,
                        DischargeToGrid = battMax
                    };
                }
                else
                {
                    p.Action = new PeriodAction()
                    {
                        ChargeFromGrid = 0,
                        DischargeToGrid = 100
                    };
                }
            }
            Recompute(plan, generationPrediction);
            bool a = TryBuyMore(plan, gStart, gEnd, generationPrediction);
            bool aa = TryBuyLess(plan, gStart, gEnd, generationPrediction, battMax);
            bool b = TrySellMore(plan, gStart, gEnd, generationPrediction, battMax);
            while (a || aa || b)
            {
                a = TryBuyLess(plan, gStart, gEnd, generationPrediction, battMax);
                aa = TryBuyLess(plan, gStart, gEnd, generationPrediction, battMax);
                b = TrySellMore(plan, gStart, gEnd, generationPrediction, battMax);
            }

            /*
             * Keep the battery empty.
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

        Recompute(plan, generationPrediction);
        bool a = TryBuyMore(plan, gStart, gEnd, generationPrediction);
        bool b = TrySellMore(plan, gStart, gEnd, generationPrediction);
        while (a || b)
        {
            a = TryBuyMore(plan, gStart, gEnd, generationPrediction);
            b = TrySellMore(plan, gStart, gEnd, generationPrediction);
        }
        */

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
                PeriodPlan? q = plan.Plans.GetNext(p);
                if (q == null) { continue; }
                if (p.Action.DischargeToGrid < 100)
                {
                    p.Action.DischargeToGrid = q.Battery - 3 < _Batt.BatteryMinimumLimit ? _Batt.BatteryMinimumLimit : q.Battery - 3;
                }
                else if (p.Action.ChargeFromGrid > 0)
                {
                    p.Action.ChargeFromGrid = q.Battery;
                }
            }

            // Save.
            PlanService.Save(plan);
            _Email.SendPlanEmail(plan, notes.ToString());
        }

        private int GetBattLevelAfterPeriod(PeriodPlan p, double generationPrediction)
        {
            int halfHourBattPercentOut = _Batt.CapacityKiloWattHoursToPercent(0.5 * 3.6);
            int halfHourBattPercentIn = _Batt.CapacityKiloWattHoursToPercent(0.5 * 4);
            int halfHourUsePercent = _Batt.CapacityKiloWattHoursToPercent(0.5 * _BatteryUsageProfile.GetKwkh(p.Start.DayOfWeek, p.Start.Hour, p.Start.AddHours(1).Hour));

            int generationInPeriod = _Batt.CapacityKiloWattHoursToPercent((p.Start.Hour < 9 || p.Start.Hour > 16) ? 0 : generationPrediction / (2 * 12));

            if (p.Action.DischargeToGrid < 100)
            {
                int bo = p.Battery - halfHourBattPercentOut;// - halfHourUsePercent; The 3.6kW limit applies to export + consumption.
                if (bo < _Batt.BatteryMinimumLimit) { return _Batt.BatteryMinimumLimit; }
                if (bo < p.Action.DischargeToGrid) { bo = p.Action.DischargeToGrid - halfHourUsePercent; }
                return bo;
            }
            else if (p.Action.ChargeFromGrid > 0)
            {
                int bi = p.Battery + halfHourBattPercentIn - halfHourUsePercent; // Generation will contribute instead of grid, not in addition to, but use will reduce what's available for batt.
                if (bi > 100) { bi = 100; }
                if (bi > p.Action.ChargeFromGrid) { bi = p.Action.ChargeFromGrid; }
                return bi;
            }

            return p.Battery - halfHourUsePercent > _Batt.BatteryMinimumLimit ? p.Battery - halfHourUsePercent : _Batt.BatteryMinimumLimit;
        }

        private void Recompute(Plan plan, double generationPrediction)
        {
            PeriodPlan current = plan.Current;
            PeriodPlan? next = plan.Plans.GetNext(current);
            while (next != null)
            {
                next.Battery = GetBattLevelAfterPeriod(current, generationPrediction);
                current = next;
                next = plan.Plans.GetNext(current);
            }
        }

        /// <summary>
        /// Try buy more while keeping the battery as empty as possible: buy to use or to sell.
        /// </summary>
        /// <param name="plan"></param>
        /// <param name="gStart"></param>
        /// <param name="gEnd"></param>
        /// <param name="generationPrediction"></param>
        /// <returns></returns>
        private bool TryBuyMore(Plan plan, DateTime gStart, DateTime gEnd, double generationPrediction)
        {
            bool changes = false;
            PeriodPlan current = plan.Current; //.Plans.OrderBy(z => z.Start).First();
            int halfHourBattPercentOut = _Batt.CapacityKiloWattHoursToPercent(0.5 * 3.6);
            int halfHourBattPercentIn = _Batt.CapacityKiloWattHoursToPercent(0.5 * 4);

            decimal pBuy = plan.Plans.Where(z => z.Start >= current.Start && z.Action.ChargeFromGrid == 0)
                .Select(z => z.Buy)
                .Min();
            // Can buy to sell.
            while (pBuy < 15M * 0.89M)
            {
                foreach (PeriodPlan pb in plan.Plans.Where(z => z.Start >= current.Start && z.Buy == pBuy && z.Start >= current.Start))
                {
                    PeriodPlan? q = plan.Plans.GetPrevious(pb);
                    int battPrevious = 100;
                    int batt = q.Battery;

                    bool fcf = plan.Plans.Any(z => z.Start > pb.Start && z.Battery == 100);

                    if (!fcf && battPrevious + halfHourBattPercentIn < 100)
                    {
                        pb.Action.ChargeFromGrid = 100;
                        pb.Action.DischargeToGrid = 100;
                        changes = true;
                    }
                }
                Recompute(plan, generationPrediction);
                pBuy = plan.Plans.Where(z => z.Start >= current.Start && z.Action.ChargeFromGrid == 0 && z.Buy > pBuy)
                    .Select(z => z.Buy)
                    .Min();
            }

            // Need to buy to use.
            PeriodPlan? p = plan.Plans.Where(z => z.Start >= current.Start && z.Battery <= _Batt.BatteryMinimumLimit).OrderBy(z => z.Start).LastOrDefault();
            while (p != null)
            {
                PeriodPlan? q = plan.Plans.Where(z => z.Start >= plan.Current.Start && z.Start <= p.Start && z.Action.ChargeFromGrid < 100).OrderBy(z => z.Buy).First();
                if(q != null)
                {
                    q.Action.ChargeFromGrid += _Batt.CapacityKiloWattHoursToPercent(0.5 * _BatteryUsageProfile.GetKwkh(p.Start.DayOfWeek, p.Start.Hour, p.Start.AddHours(1).Hour));
                    if (q.Action.ChargeFromGrid > 100)
                    {
                        q.Action.ChargeFromGrid = 100;
                    }
                    else if(q.Action.ChargeFromGrid <= _Batt.BatteryMinimumLimit)
                    {
                        q.Action.ChargeFromGrid += _Batt.BatteryMinimumLimit;
                    }
                        changes = true;
                }
                else
                {
                    // Just buy to use in p.
                }
                    p = plan.Plans.Where(z => z.Start >= current.Start && z.Start < p.Start && z.Battery <= _Batt.BatteryMinimumLimit).OrderBy(z => z.Start).LastOrDefault();

                /*
                PeriodPlan p = plan.Plans.Where(z => z.Start >= current.Start && z.Action.DischargeToGrid < 100 && z.Action.DischargeToGrid > z.Battery).OrderBy(z => z.Start).First();
                int extraWanted = p.Action.DischargeToGrid - p.Battery;

                PeriodPlan? nextCheapest = plan.Plans.Where(z => z.Start  z.Start > p.Start && z.Buy < p.Buy && !plan.Plans.Any(y => y.Start > p.Start && y.Start < z.Start && y.Buy > p.Buy)).FirstOrDefault();
                if (nextCheapest != null)
                {
                    // Just buy directly to use; we can get it cheaper later.
                    p.Action.ChargeFromGrid = 0;
                    p.Action.DischargeToGrid = 100;
                }
                else
                {
                    // Buy now because it gets dearer later.
                    PeriodPlan? r = plan.Plans.Where(z => z.Start >= current.Start && z.Start < p.Start && z.Action.DischargeToGrid < 100 && !plan.Plans.Any(y => y.Start >= z.Start && y.Start < p.Start && y.Action.ChargeFromGrid > 0)).OrderBy(z => z.Start).FirstOrDefault();
                    if (r != null)
                    {
                        // Sell less in the past.
                        r.Action.DischargeToGrid = 100;
                    }
                    else
                    {
                        // Buy more in the past or now.
                        PeriodPlan? p100 = plan.Plans.Where(z => z.Start >= current.Start && z.Start < p.Start && z.Battery == 100).OrderBy(z => z.Start).LastOrDefault();
                        // Buy at the cheapest preceeding half hour.
                        PeriodPlan? q = plan.Plans.Where(z => z.Start >= current.Start && z.Start <= p.Start && z.Action.ChargeFromGrid < 100 && (p100 == null || z.Start > p100.Start)).OrderBy(z => z.Buy).FirstOrDefault();
                        if (q != null)
                        {
                            q.Action.ChargeFromGrid = Math.Max(q.Action.ChargeFromGrid, q.Battery) + extraWanted + 1;
                            if (q.Action.ChargeFromGrid > 100) { q.Action.ChargeFromGrid = 100; }
                            q.Action.DischargeToGrid = 100;
                        }
                        else
                        {
                            // No option but to consume from grid.
                            p.Action.ChargeFromGrid = 100;
                            p.Action.DischargeToGrid = 100;
                        }
                    }
                }
                */
                Recompute(plan, generationPrediction);
                changes = true;
            }

            return changes;
        }

        /// <summary>
        /// Try sell more to keep the battery empty.
        /// </summary>
        /// <param name="plan"></param>
        /// <param name="gStart"></param>
        /// <param name="gEnd"></param>
        /// <param name="generationPrediction"></param>
        /// <returns></returns>
        private bool TrySellMore(Plan plan, DateTime gStart, DateTime gEnd, double generationPrediction)
        {
            bool changes = false;
            DateTime last = plan.Plans.OrderBy(z => z.Start).Last().Start.AddHours(1);
            // Try sell less, actually, if the battery gets too low.
            while (plan.Plans.Any(z => z.Start < last && z.Action.DischargeToGrid < 100 && z.Battery < z.Action.DischargeToGrid))
            {
                PeriodPlan? p = plan.Plans.Where(z => z.Start < last && z.Action.DischargeToGrid < 100 && z.Battery < z.Action.DischargeToGrid).OrderBy(z => z.Start).Last();
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
                Recompute(plan, generationPrediction);
            }

            //foreach (PeriodPlan p in plan.Plans)
            //{
            //    // We can try to sell more if the battery estimate is above the discharge level.
            //    // 
            //}
            return changes;
        }

        /// <summary>
        /// When keeping the battery full there may not be enough space to buy when generation is high or prices are low.
        /// </summary>
        /// <param name="plan"></param>
        /// <param name="gStart"></param>
        /// <param name="gEnd"></param>
        /// <param name="generationPrediction"></param>
        /// <param name="battMax"></param>
        /// <returns></returns>
        private bool TryBuyLess(Plan plan, DateTime gStart, DateTime gEnd, double generationPrediction, int battMax)
        {
            bool changes = false;
            DateTime last = plan.Plans.OrderBy(z => z.Start).Last().Start.AddHours(1);
            PeriodPlan? p = plan.Plans
                //.Where(z => z.Start < last && z.Action.ChargeFromGrid > 0 && (z.Battery > battMax || (z.Battery >= battMax && (plan.Plans.GetNext(z)?.Battery ?? 0) >= z.Action.ChargeFromGrid)))
                .Where(z => z.Start > plan.Current.Start && z.Start < last && z.Action.ChargeFromGrid >= battMax && z.Battery >= battMax )
                .OrderBy(z => z.Start)
                .LastOrDefault();
            while (p != null)
            {
                PeriodPlan? q = plan.Plans.Where(z => z.Start > plan.Current.Start && z.Start < p.Start && z.Action.ChargeFromGrid > 0).OrderBy(z => z.Buy).Last();
                if (q != null)
                {
                    q.Action.ChargeFromGrid -= 10;
                    if (q.Action.ChargeFromGrid < 0)
                    {
                        q.Action.ChargeFromGrid = 0;
                    }
                }
                else
                {
                    p.Action.ChargeFromGrid = 0;
                }
                changes = true;
                Recompute(plan, generationPrediction);
                p = plan.Plans
                    .Where(z => z.Start > plan.Current.Start && z.Start < last && z.Action.ChargeFromGrid >= battMax && z.Battery >= battMax)
                    .OrderBy(z => z.Start)
                    .LastOrDefault();
            }
            return changes;
        }

        /// <summary>
        /// When keeping the battery full try to sell to make space if needed.
        /// </summary>
        /// <param name="plan"></param>
        /// <param name="gStart"></param>
        /// <param name="gEnd"></param>
        /// <param name="generationPrediction"></param>
        /// <param name="battMax"></param>
        /// <returns></returns>
        private bool TrySellMore(Plan plan, DateTime gStart, DateTime gEnd, double generationPrediction, int battMax)
        {
            bool changes = false;
            DateTime last = plan.Plans.OrderBy(z => z.Start).Last().Start.AddHours(1);
            PeriodPlan? p = plan.Plans
                .Where(z => z.Start < last && z.Action.ChargeFromGrid > 0 && (z.Battery > battMax || (z.Battery >= battMax && (plan.Plans.GetPrevious(z)?.Battery ?? 0) >= battMax)))
                .OrderBy(z => z.Start)
                .LastOrDefault();
            while (p != null)
            {
                PeriodPlan? q = plan.Plans.Where(z => z.Start < p.Start && z.Action.ChargeFromGrid == 0).LastOrDefault();
                if( q != null)
                {
                    q.Action.DischargeToGrid -= 10;
                    if (q.Action.ChargeFromGrid < 0)
                    {
                        q.Action.ChargeFromGrid = 0;
                    }

                    changes = true;
                }
                Recompute(plan, generationPrediction);
                p = plan.Plans
                    .Where(z => z.Start < p.Start && z.Action.ChargeFromGrid > 0 && (z.Battery > battMax || (z.Battery >= battMax && (plan.Plans.GetPrevious(z)?.Battery ?? 0) >= battMax)))
                    .OrderBy(z => z.Start)
                    .LastOrDefault();
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
