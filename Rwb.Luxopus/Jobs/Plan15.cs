using Accord.Genetic;
using Accord.MachineLearning.Boosting.Learners;
using InfluxDB.Client.Core.Flux.Domain;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using RestSharp;
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

        protected override async Task WorkAsync(CancellationToken cancellationToken)
        {
            //DateTime t0 = new DateTime(2023, 03, 31, 17, 00, 00);
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

            // When is it economical to buy?
            foreach (PeriodPlan p in plan.Plans.Where(z => z.Buy < _ExportPrice * 0.89M))
            {
                p.Action = new PeriodAction()
                {
                    ChargeFromGrid = 100,
                    DischargeToGrid = 100
                };
            }

            // Forecast.
            DateTime tForecast = DateTime.Today.ToUniversalTime().AddHours(24 + 2);
            double generationPrediction = (double)(await InfluxQuery.QueryAsync(Query.PredictionToday, tForecast)).Single().Records[0].Values["_value"];
            double battPrediction = _Batt.CapacityKiloWattHoursToPercent(generationPrediction);

            // Usage.
            List<FluxTable> bupH = await InfluxQuery.QueryAsync(Query.HourlyBatteryUse, t0);
            BatteryUsageProfile bup = new BatteryUsageProfile(bupH);
            double powerRequired = bup.GetKwkh(tForecast.DayOfWeek, tForecast.AddHours(3 /* Hack to start after charge from grid. */).Hour, 23);
            int battRequired = _Batt.CapacityKiloWattHoursToPercent(powerRequired);

            // TODO: have we bought enough to use?
            double boughtEstimate = plan.Plans.Where(z => z.Action != null & z.Action!.ChargeFromGrid > 0).Count() * 2 * 3.4;
            while(boughtEstimate < powerRequired)
            {
                PeriodPlan? q = plan.Plans.Where(z => z.Action == null && z.Start.Hour < 10).OrderBy(z => z.Buy).FirstOrDefault();
                if( q == null) { break; }
                q.Action = new PeriodAction()
                {
                    ChargeFromGrid = 100,
                    DischargeToGrid = 100
                };
                boughtEstimate += 0.5 * 3.4;
            }

            // Free.
            double freeKw = plan.Plans.FutureFreeHoursBeforeNextDischarge(plan.Current!) * 3.2;
            if (freeKw > 0)
            {
                notes.AppendLine($"Free: {freeKw:0.0}kW.");
            }

            notes.AppendLine($"Predicted generation of {generationPrediction:0.0}kWH ({battPrediction:0}%).");
            double generationMedianForMonth = (double)(await InfluxQuery.QueryAsync(Query.GenerationMedianForMonth, DateTime.UtcNow)).Single().Records[0].Values["_value"];
            generationMedianForMonth = generationMedianForMonth / 10.0;
            if (generationPrediction > generationMedianForMonth)
            {
                generationPrediction = (generationPrediction + generationMedianForMonth) / 2.0;
                battPrediction = _Batt.CapacityKiloWattHoursToPercent(generationPrediction);
                notes.AppendLine($"Predicted generation of {generationPrediction:0.0}kWH ({battPrediction:0}%) adjusted towards monthly median of {generationMedianForMonth}kWH.");
            }
            notes.AppendLine($"Predicted use of {powerRequired:0.0}kW ({battRequired:0}%).");

            // Discharge:
            foreach (PeriodPlan p in plan.Plans)
            {
                if(p.Action != null) { continue; }

                // Discharge to make space to buy.
                PeriodPlan? nextBuy = plan.Plans.FirstOrDefault(z => z.Start > p.Start && p.Action != null && p.Action.ChargeFromGrid > 0);
                if (nextBuy != null)
                {
                    p.Action = GetDischargeAction(_Batt.BatteryMinimumLimit + battRequired);
                    continue;
                }

                // Discharge to make space to import free electricity.
                PeriodPlan? nextFree = plan.GetNextRun(p, z => z.Buy <= 0).Item1.FirstOrDefault();
                if (nextFree != null)
                {
                    p.Action = GetDischargeAction(_Batt.BatteryMinimumLimit + battRequired);
                    continue;
                }

                // Discharge to make space for burst generation that can't be exported immediately.
                if(generationPrediction > _Batt.CapacityPercentToKiloWattHours(50) && p.Start.Hour < 9)
                {
                    p.Action = GetDischargeAction(_Batt.BatteryMinimumLimit + battRequired);
                }
            }

            bool batteryConditioningRequired = false;
            try
            {
                Dictionary<string, string> settings = await _Lux.GetSettingsAsync();
                (_, int bcSince, int bcPeriod) = _Lux.GetBatteryCalibration(settings);
                if ((bcSince > bcPeriod - 3))
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


            foreach (PeriodPlan p in plan.Plans.Where(z => z.Action == null))
            {
                p.Action = batteryConditioningRequired ? new PeriodAction()
                {
                    ChargeFromGrid = 0,
                    DischargeToGrid = 100
                } : GetDischargeAction(_Batt.BatteryMinimumLimit + battRequired);
            }

            PlanService.Save(plan);
            _Email.SendPlanEmail(plan, notes.ToString());
        }

        private static PeriodAction GetDischargeAction(int dischargeTarget)
        {
            return new PeriodAction()
            {
                ChargeFromGrid = 0,
                DischargeToGrid = dischargeTarget
            };
        }
    }
}
