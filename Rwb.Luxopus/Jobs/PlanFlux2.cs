using Accord.Statistics.Models.Regression.Linear;
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
    public class BatteryUsageProfile
    {
        private readonly Dictionary<DayOfWeek, Dictionary<int, double>> _DailyHourly;

        public BatteryUsageProfile(IEnumerable<FluxTable> hourly)
        {
            _DailyHourly = hourly.Single().Records.GroupBy(z => z.GetValue<long>("d"))
            .ToDictionary(
                z => (DayOfWeek)Convert.ToInt32(z.Key),
                z => z.ToDictionary(
                    y => Convert.ToInt32(y.GetValue<long>("h")),
                    y => y.GetValue<double>("_value")
                )
            );
        }

        public double GetKwkh(DayOfWeek day, int hourFrom, int hourTo)
        {
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


    /*
       https://forum.octopus.energy/t/using-the-flux-tariff-with-solar-with-battery-check-my-working/7510/22

    The inverter is not efficient enough to make it economic to buy electricity to sell.
    Besides losses across the inverter (which is likely to be asymmatrical)
    there are chemical losses in the battery.

    ...so...
    Sell all at peak.
    Buy back to use after peak.
    Buy at low to get through to morning and cover any demand tomorrow that can't be satisfied from generation.

     */

    /// <summary>
    /// <para>
    /// Plan for 'flux' tariff https://octopus.energy/smart/flux/
    /// </para>
    /// </summary>
    public class PlanFlux2 : PlanFlux
    {
        private readonly IInfluxWriterService _InfluxWriter;
        private readonly IBatteryService _Batt;
        private readonly IOctopusService _Octopus;
        private readonly IAtService _At;

        public PlanFlux2(ILogger<LuxMonitor> logger,
            IInfluxQueryService influxQuery,
            IInfluxWriterService influxWriter,
            ILuxopusPlanService plan, IEmailService email,
            IBatteryService batt,
            IOctopusService octopus,
            IAtService at)
            : base(logger, influxQuery, plan, email)
        {
            _InfluxWriter = influxWriter;
            _Batt = batt;
            _Octopus = octopus;
            _At = at;
        }

        protected override async Task WorkAsync(CancellationToken cancellationToken)
        {
            DateTime t0 = DateTime.UtcNow.AddHours(-3);
            //Plan? current = PlanService.Load(t0);
            StringBuilder notes = new StringBuilder();

            List<FluxTable> bupH = await InfluxQuery.QueryAsync(Query.HourlyBatteryUse, t0);
            BatteryUsageProfile bup = new BatteryUsageProfile(bupH);

            DateTime start = t0.StartOfHalfHour().AddDays(-1);
            DateTime stop = (new DateTime(t0.Year, t0.Month, t0.Day, 21, 0, 0)).AddDays(1);
            TariffCode ti = await _Octopus.GetElectricityCurrentTariff(TariffType.Import, start);
            TariffCode te = await _Octopus.GetElectricityCurrentTariff(TariffType.Export, start);
            List<ElectricityPrice> prices = await InfluxQuery.GetPricesAsync(start, stop, ti.Code, te.Code);

            // Time to reschedule.
            DateTime tReschedule = DateTime.Now;
            if (tReschedule.Minute < 30)
            {
                tReschedule = tReschedule.AddMinutes(38 - tReschedule.Minute);
            }
            else
            {
                tReschedule = tReschedule.AddHours(1).AddMinutes(8 - tReschedule.Minute);
            }

            // Find the current period: the last period that starts before t0.
            ElectricityPrice? priceNow = prices.Where(z => z.Start < t0).OrderByDescending(z => z.Start).FirstOrDefault();
            if (priceNow == null)
            {
                Logger.LogError($"No current price; rescheduling at {tReschedule: yyyy-MM-dd HH:mm}.");
                _At.Schedule(async () => await this.WorkAsync(CancellationToken.None), tReschedule);
                return;
            }

            ElectricityPrice? priceNext = prices.Where(z => z.Start > priceNow.Start).OrderBy(z => z.Start).FirstOrDefault();
            if (priceNext == null)
            {
                Logger.LogError($"No future prices; rescheduling at {tReschedule: yyyy-MM-dd HH:mm}.");
                _At.Schedule(async () => await this.WorkAsync(CancellationToken.None), tReschedule);
                return;
            }

            DateTime pLast = prices.OrderBy(z => z.Start).Last().Start;
            if (DateTime.Now > pLast.ToLocalTime())
            {
                // We're probably in the last period.
                Logger.LogWarning($"Rescheduling PlanFlux2 at {tReschedule: yyyy-MM-dd HH:mm} because current period is the last.");
                _At.Schedule(async () => await this.WorkAsync(CancellationToken.None), tReschedule);
            }

            Plan plan = new Plan(prices.Where(z => z.Start >= priceNow.Start));

            HalfHourPlan? next = null;

            foreach (HalfHourPlan p in plan.Plans)
            {
                switch (GetFluxCase(plan, p))
                {
                    case FluxCase.Peak:
                        next = plan.Plans.GetNext(p);
                        if (next != null)
                        {
                            if (next.Buy < p.Sell)
                            {
                                notes.Append($"Next buy {next.Buy:0.0} < current sell {p.Sell:0.0} therefore discharge all.");
                                p.Action = new PeriodAction()
                                {
                                    ChargeFromGrid = 0,
                                    DischargeToGrid = 8,
                                };
                                break;
                            }
                        }

                        // TODO: user flag to keep back more. Use MQTT?

                        // How much do we need?
                        // Batt absolute min plus use until ~~morning generation~~ the low.
                        HalfHourPlan? dischargeEnd = plan.Plans.GetNext(p);
                        HalfHourPlan? low = plan.Plans.GetNext(p, z => GetFluxCase(plan, z) == FluxCase.Low);
                        int dischargeToGrid = 21;
                        if (dischargeEnd != null && low != null)
                        {
                            double hours = (low.Start - dischargeEnd.Start).TotalHours;
                            double kWh = bup.GetKwkh(t0.DayOfWeek, dischargeEnd.Start.Hour, low.Start.Hour);
                            int percentForUse = _Batt.CapacityKiloWattHoursToPercent(kWh);
                            dischargeToGrid = _Batt.BatteryMinimumLimit + percentForUse;

                            notes.AppendLine($"Peak:    hours to low: {hours:0.0}");
                            notes.AppendLine($"Peak:     kWh for use: {kWh:0.0} = bup.GetKwkh({t0.DayOfWeek}, {dischargeEnd.Start.Hour}, {low.Start.Hour})");
                            notes.AppendLine($"Peak:   percentForUse: {percentForUse}");
                            notes.AppendLine($"Peak: dischargeToGrid: {dischargeToGrid} (used)");
                        }
                        else
                        {
                            notes.AppendLine($"Peak: overnight low not found; AdjustLimit value is used.");
                        }

                        p.Action = new PeriodAction()
                        {
                            ChargeFromGrid = 0,
                            DischargeToGrid = Convert.ToInt32(dischargeToGrid), // We can buy back cheaper before the low. On-grid cut-off is 5. 
                            // TODO: get prices to check that ^^ is true.
                            // TODO: aim for batt at 6% (cutoff is 5%) at 2AM.
                            // MUST NOT buy during peak threfore leave some.
                            //BatteryChargeRate = 0,
                            //BatteryGridDischargeRate = 100,
                            // Selling at peak for 34p * 0.9 = 30p. Day rate to buy is 33p. Therefore MUST NOT BUT before low.
                            // Need about 20% to get over night, therefore estimate 10% to get to low.
                        };
                        break;
                    case FluxCase.Daytime:
                        p.Action = new PeriodAction()
                        {
                            ChargeFromGrid = 0,
                            DischargeToGrid = 100,
                            //BatteryChargeRate = 75,
                            //BatteryGridDischargeRate = 100,
                        };
                        break;
                    case FluxCase.Evening:
                        // Continue to discharge in BST (ish).
                        next = plan.Plans.GetNext(p);
                        HalfHourPlan? previous = plan.Plans.GetPrevious(p);
                        if (p.Start.Month >= 5 && p.Start.Month <= 9
                            && next != null && next.Buy < p.Sell 
                            && previous != null && previous.Action != null && previous!.Action!.DischargeToGrid < 100)
                        {
                            p.Action = new PeriodAction()
                            {
                                ChargeFromGrid = 0,
                                DischargeToGrid = previous.Action.DischargeToGrid,
                            };
                        }
                        else
                        {
                            // Same as daytime.
                            p.Action = new PeriodAction()
                            {
                                ChargeFromGrid = 0,
                                DischargeToGrid = 100,
                            };
                        }
                        break;
                    case FluxCase.Low:
                        // How much do we want?
                        next = plan.Plans.GetNext(p);
                        DateTime startOfGeneration = DateTime.UtcNow.Date.AddHours(10).AddDays(-1);
                        try
                        {
                            (startOfGeneration, _) = (await InfluxQuery.QueryAsync(Query.StartOfGeneration, t0)).First().FirstOrDefault<double>();
                        }
                        catch (Exception e)
                        {
                            Logger.LogError(e, "Could not get start of generation yesterday for making flux low plan.");
                        }

                        // Night time power use is generally below 200w.
                        // Aim for end of flux low to 10AM.
                        double powerRequired = 0.2 * (10 - (next?.Start.Hour ?? p.Start.Hour + 3));
                        if ((next?.Start.Hour ?? p.Start.Hour + 3) < startOfGeneration.Hour)
                        {
                            powerRequired = bup.GetKwkh(t0.DayOfWeek, (next?.Start.Hour ?? p.Start.Hour + 3), startOfGeneration.Hour + (startOfGeneration.Minute > 21 ? 1 : 0));
                        }
                        int battRequired = _Batt.CapacityKiloWattHoursToPercent(powerRequired);
                        notes.AppendLine($"Low: AdjustLimit      battRequired {battRequired}%");
                        notes.AppendLine($"     AdjustLimit startOfGeneration {startOfGeneration:HH:mm} ");
                        notes.AppendLine($"     AdjustLimit     powerRequired {powerRequired:0.0}kWh = bup.GetKwkh({t0.DayOfWeek}, {(next?.Start.Hour ?? p.Start.Hour + 3)}, {startOfGeneration.Hour + (startOfGeneration.Minute > 21 ? 1 : 0)})");

                        int chargeFromGrid = _Batt.BatteryMinimumLimit + battRequired;
                        notes.AppendLine($"     chargeFromGrid {_Batt.BatteryMinimumLimit + battRequired} = BatteryAbsoluteMinimum {_Batt.BatteryMinimumLimit} + battRequired {battRequired} (used)");

                        DateTime tForecast = p.Start;
                        if (tForecast.Hour > 12)
                        {
                            tForecast = tForecast.AddDays(1);
                        }
                        try
                        {
                            /*
                            FluxRecord weather = (await InfluxQuery.QueryAsync(Query.Weather, tForecast)).First().Records.Single();
                            double cloud = weather.GetValue<double>("cloud");
                            double daylen = weather.GetValue<double>("daylen");
                            int forecast = Convert.ToInt32(weather.GetValue<double>("forecast"));
                            double uvi = weather.GetValue<double>("uvi");
                            double solcast = weather.GetValue<double>("solcast");

                            notes.AppendLine($"Weather: cloud: {cloud:0} | daylen: {daylen:0} | forecast: {_Weather.GetForecastDescription(forecast)} | uvi: {uvi: 0.0} | solcast: {solcast:#,##0}");

                            if (cloud > 90 && chargeFromGrid < 21)
                            {
                                // If we think there won't be much generation then buy enough to get through the day.
                                // Buy ~17 and sell ~26
                                // therefore want to get the battey full by the end of the day.
                                // When cloud > 90 median generation is about 16kWH, mean 17kWH.
                                // Battery capacity is about 8kWH.
                                notes.AppendLine($"Cloud forecast of {cloud:##0}% therefore charge to {chargeFromGrid} increased to 34.");
                                chargeFromGrid = 34;
                            }
                            else if (chargeFromGrid > 21)
                            {
                                notes.AppendLine($"Charge from grid of {chargeFromGrid} overridden to 21.");
                                chargeFromGrid = 21;
                                // Hack.
                            }
                            */
                            // !
                            HalfHourPlan? peak = plan.Plans.FirstOrDefault(z => z.Start > p.Start && GetFluxCase(plan, z) == FluxCase.Peak);
                            if (next != null && peak != null)
                            {
                                double generationPrediction = await GenerationPredictionFromMultivariateLinearRegression(tForecast);
                                LineDataBuilder ldb = new LineDataBuilder();
                                ldb.Add("prediction", "MultivariateLinearRegression", generationPrediction * 10, tForecast);

                                // Do some future predictions too.
                                double tomorrow = await GenerationPredictionFromMultivariateLinearRegression(tForecast.AddDays(1));
                                ldb.Add("prediction", "MultivariateLinearRegression", tomorrow * 10, tForecast.AddDays(1));
                                tomorrow = await GenerationPredictionFromMultivariateLinearRegression(tForecast.AddDays(3));
                                ldb.Add("prediction", "MultivariateLinearRegression", tomorrow * 10, tForecast.AddDays(2));
                                tomorrow = await GenerationPredictionFromMultivariateLinearRegression(tForecast.AddDays(3));
                                ldb.Add("prediction", "MultivariateLinearRegression", tomorrow * 10, tForecast.AddDays(3));

                                await _InfluxWriter.WriteAsync(ldb);

                                // Back to today...
                                double battPrediction = _Batt.CapacityKiloWattHoursToPercent(generationPrediction);
                                powerRequired = bup.GetKwkh(p.Start.DayOfWeek, plan.Plans.GetNext(p).Start.Hour, peak.Start.Hour);
                                battRequired = _Batt.CapacityKiloWattHoursToPercent(powerRequired);

                                notes.AppendLine($"Low: Predicted generation of {generationPrediction:0.0}kW ({battPrediction:0}%).");
                                notes.AppendLine($"     Predicted        use of {powerRequired:0.0}kW ({battRequired:0}%).");

                                double powerAvailableForBatt = generationPrediction - powerRequired;

                                if (powerAvailableForBatt < 0)
                                {
                                    // Not enough generation. Charge to 89%.
                                    notes.AppendLine("     Generation prediction is very low ({powerAvailableForBatt:#,##0}kWh): charge to 89%.");
                                    chargeFromGrid = 89;
                                }
                                //else if(burstPrediction < 3000 && chargeFromGrid < 66)
                                //{
                                //    notes.AppendLine($"     Burst prediction is low ({burstPrediction:#,##0}kW): charge to 89%.");
                                //    chargeFromGrid = 89;
                                //}
                                else
                                {
                                    bool buyToSell = false;
                                    if (next != null && p.Buy / next.Sell < 0.89M)
                                    {
                                        // 1 unit gets inverted once on the way in and again on the way out,
                                        // So there's only 1 * _Batt.Efficiency * _Batt.Efficiency left.
                                        // Therefore require buy < e * e * sell, i.e., buy / sell < ee. Plus battery wear.
                                        // What in import efficiency is different to export efficiency? Query for it.
                                        notes.AppendLine($"Fill your boots! Buy: {p.Buy:0.00}, Sell: {next.Sell:0.00}, quotient {100M * p.Buy / next.Sell:0.0}% < {100M * 0.89M:0}%.");
                                        buyToSell = true;
                                    }

                                    double predictedGenerationToBatt = _Batt.CapacityKiloWattHoursToPercent(powerAvailableForBatt);
                                    if (predictedGenerationToBatt > 200)
                                    {
                                        notes.AppendLine("     Generation prediction is high.");
                                        if (chargeFromGrid > (buyToSell ? 34 : 21))
                                        {
                                            notes.AppendLine($"       Charge from grid overidden from {chargeFromGrid:0}% to {(buyToSell ? 34 : 21)}%.");
                                            chargeFromGrid = buyToSell ? 34 : 21;
                                        }
                                        else if (chargeFromGrid < (buyToSell ? 21 : 13))
                                        {
                                            notes.AppendLine($"       Charge from grid overidden from {chargeFromGrid:0}% to {(buyToSell ? 21 : 13)}%.");
                                            chargeFromGrid = buyToSell ? 21 : 13;
                                        }
                                    }
                                    else if (predictedGenerationToBatt < 10)
                                    {
                                        notes.AppendLine("     Generation prediction is low: charge to 90%. ");
                                        chargeFromGrid = 89;
                                    }
                                    else
                                    {
                                        notes.AppendLine($"     Power to batt: {powerAvailableForBatt:0.0}kW ({predictedGenerationToBatt:0}%).");
                                        chargeFromGrid = 100 - Convert.ToInt32(predictedGenerationToBatt);
                                        notes.AppendLine($"     chargeFromGrid: {chargeFromGrid:0}%.");
                                        if (chargeFromGrid > 89)
                                        {
                                            notes.AppendLine($"     chargeFromGrid limited to 89%.");
                                            chargeFromGrid = 89;
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Logger.LogError(e, "Failed to execute cloud query.");
                        }

                        p.Action = new PeriodAction()
                        {
                            ChargeFromGrid = Convert.ToInt32(chargeFromGrid),
                            DischargeToGrid = 100,
                            //BatteryChargeRate = 100,
                            //BatteryGridDischargeRate = 0,
                        };
                        break;
                }
            }

            PlanService.Save(plan);
            Email.SendPlanEmail(plan, notes.ToString());
        }

        #region RegressionModel
        class Datum2
        {
            public DateTime Time;
            public double? Cloud;
            public double? Daylen;
            public double? Elevation;
            public long? Generation;
            public double? Uvi;

            public bool IsComplete
            {
                get
                {
                    return Cloud.HasValue && Daylen.HasValue
                        && Elevation.HasValue && Generation.HasValue && Uvi.HasValue;
                }
            }

            public double[] Input
            {
                get
                {
                    return new[]
            {
                Convert.ToDouble(Cloud.Value), Convert.ToDouble(Daylen.Value), Convert.ToDouble(Elevation.Value), Convert.ToDouble(Uvi.Value)
            };
                }
            }

            public double[] Output { get { return new[] { Convert.ToDouble(Generation.Value) }; } }
        }

        private OrdinaryLeastSquares _OrdinaryLeastSquares;
        private MultivariateLinearRegression _MultivariateLinearRegression;
        private async Task<double> GenerationPredictionFromMultivariateLinearRegression(DateTime tForecast)
        {
            if (_OrdinaryLeastSquares == null || _MultivariateLinearRegression == null)
            {
                // Get daat.
                FluxTable fluxData = (await InfluxQuery.QueryAsync(Query.PredictionData2, DateTime.Now)).Single();
                List<Datum2> data = fluxData.Records.Select(z => new Datum2()
                {
                    Time = z.GetValue<DateTime>("_time"),
                    Cloud = z.GetValue<double?>("cloud"),
                    Daylen = z.GetValue<double?>("daylen"),
                    Elevation = z.GetValue<double?>("elevation"),
                    Generation = z.GetValue<long?>("generation"),
                    Uvi = z.GetValue<double?>("uvi"),
                }).ToList();

                // Build model.
                _OrdinaryLeastSquares = new OrdinaryLeastSquares();
                IEnumerable<Datum2> trainingData = data.Where(z => z.IsComplete /*&& z.Time < new DateTime(2023, 9, 1)*/);
                double[][] inputs = trainingData.Select(z => z.Input).ToArray();
                double[][] outputs = trainingData.Select(z => z.Output).ToArray();
                _MultivariateLinearRegression = _OrdinaryLeastSquares.Learn(inputs, outputs);
            }

            // Use model. Apply the rescaling to the values.
            FluxRecord weather = (await InfluxQuery.QueryAsync(Query.Weather, tForecast)).First().Records.Single();
            double cloud = Math.Floor(weather.GetValue<double>("cloud") / 10.0);
            double daylen = Math.Floor(weather.GetValue<double>("daylen") * 60 * 60 / 1000.0);
            double uvi = Math.Floor(weather.GetValue<double>("uvi") * 10.0);
            double elevation = Math.Floor(weather.GetValue<double>("elevation")); // Hack in query in case of not full day of data.

            double[] prediction = _MultivariateLinearRegression.Transform(new double[] { cloud, daylen, elevation, uvi });
            return prediction[0] / 10.0;
        }
        #endregion
    }
}
