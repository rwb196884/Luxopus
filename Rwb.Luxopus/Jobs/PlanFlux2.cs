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
    /// Plan for 'flux' tariff https://octopus.energy/smart/flux/ when the batteries are just big enogh to saturate export at peak time.
    /// </para>
    /// </summary>
    public class PlanFlux2 : PlanFlux
    {
        private readonly IInfluxWriterService _InfluxWriter;
        private readonly IBatteryService _Batt;
        private readonly IOctopusService _Octopus;
        private readonly IAtService _At;
        private readonly ILuxService _Lux;

        public PlanFlux2(ILogger<LuxMonitor> logger,
            IInfluxQueryService influxQuery,
            IInfluxWriterService influxWriter,
            ILuxopusPlanService plan, IEmailService email,
            IBatteryService batt,
            IOctopusService octopus,
            IAtService at,
            ILuxService lux
            )
            : base(logger, influxQuery, plan, email)
        {
            _InfluxWriter = influxWriter;
            _Batt = batt;
            _Octopus = octopus;
            _At = at;
            _Lux = lux;
        }

        protected override async Task WorkAsync(CancellationToken cancellationToken)
        {
            try
            {
                DateTime t0 = DateTime.UtcNow.AddHours(-1);
                //Plan? current = PlanService.Load(t0);
                StringBuilder notes = new StringBuilder();

                List<FluxTable> bupH = await InfluxQuery.QueryAsync(Query.HourlyBatteryUse, t0);
                BatteryUsageProfile bup = new BatteryUsageProfile(bupH);

                DateTime start = t0.StartOfHalfHour().AddDays(-1);// Longest period is 5AM while 4PM (local).
                DateTime stop = (new DateTime(t0.Year, t0.Month, t0.Day, 21, 0, 0)).AddDays(1);
                TariffCode ti = await _Octopus.GetElectricityCurrentTariff(TariffType.Import, t0);
                TariffCode te = await _Octopus.GetElectricityCurrentTariff(TariffType.Export, t0);
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
                if (DateTime.UtcNow > pLast)
                {
                    // We're probably in the last period.
                    Logger.LogWarning($"Rescheduling PlanFlux2 at {tReschedule: yyyy-MM-dd HH:mm} because current period is the last.");
                    _At.Schedule(async () => await this.WorkAsync(CancellationToken.None), tReschedule);
                }

                Plan plan = new Plan(prices.Where(z => z.Start >= priceNow.Start.AddHours(-4)));

                PeriodPlan? next = null;

                int battDischargeableAtPeak = _Batt.CapacityKiloWattHoursToPercent(3 * 3.6);

                foreach (PeriodPlan p in plan.Plans)
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
                                        DischargeToGrid = _Batt.BatteryMinimumLimit,
                                    };
                                    break;
                                }
                            }

                            // TODO: user flag to keep back more. Use MQTT?

                            // How much do we need?
                            // Batt absolute min plus use until ~~morning generation~~ the low.
                            PeriodPlan? dischargeEnd = plan.Plans.GetNext(p);
                            PeriodPlan? low = plan.Plans.GetNext(p, z => GetFluxCase(plan, z) == FluxCase.Low);
                            int dischargeToGrid = 100 - battDischargeableAtPeak;
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
                                notes.AppendLine($"Peak: overnight low not found; using {dischargeToGrid}% = 100% minus maximum dischargeable {battDischargeableAtPeak}%.");
                            }

                            try
                            {
                                Dictionary<string, string> settings = await _Lux.GetSettingsAsync();
                                (_, int bcSince, int bcPeriod) = _Lux.GetBatteryCalibration(settings);
                                if ((bcSince > bcPeriod - 2))
                                {
                                    notes.AppendLine($"Battery calibration: {bcSince} / {bcPeriod}. *** Discharging overridden from {dischargeToGrid} to {100 - battDischargeableAtPeak}. ***");
                                    dischargeToGrid = 100 - battDischargeableAtPeak;
                                }
                            }
                            catch
                            {
                                notes.AppendLine($"*** Failed to get battery calibration info. ***");
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
                        //case FluxCase.Evening:
                        //    // Continue to discharge in BST (ish).
                        //    next = plan.Plans.GetNext(p);
                        //    HalfHourPlan? previous = plan.Plans.GetPrevious(p);
                        //    if (p.Start.Month >= 5 && p.Start.Month <= 9
                        //        && next != null && GetFluxCase(plan, next) == FluxCase.Low
                        //        && previous != null && previous.Action != null && previous!.Action!.DischargeToGrid < 100)
                        //    {
                        //        p.Action = new PeriodAction()
                        //        {
                        //            ChargeFromGrid = 0,
                        //            DischargeToGrid = previous.Action.DischargeToGrid,
                        //        };
                        //    }
                        //    else
                        //    {
                        //        // Same as daytime.
                        //        p.Action = new PeriodAction()
                        //        {
                        //            ChargeFromGrid = 0,
                        //            DischargeToGrid = 100,
                        //        };
                        //    }
                        //    break;
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
                                PeriodPlan? peak = plan.Plans.FirstOrDefault(z => z.Start > p.Start && GetFluxCase(plan, z) == FluxCase.Peak);
                                if (next != null && peak != null)
                                {
                                    double generationPrediction = (double)(await InfluxQuery.QueryAsync(Query.PredictionToday, p.Start)).Single().Records[0].Values["_value"] / 10.0;
                                    double battPrediction = _Batt.CapacityKiloWattHoursToPercent(generationPrediction);
                                    notes.AppendLine($"Low: Predicted generation of {generationPrediction:0.0}kWH ({battPrediction:0}%).");
                                    double generationMedianForMonth = (double)(await InfluxQuery.QueryAsync(Query.GenerationMedianForMonth, DateTime.UtcNow)).Single().Records[0].Values["_value"] / 10.0;
                                    generationMedianForMonth = generationMedianForMonth / 10.0;
                                    if (generationPrediction > generationMedianForMonth)
                                    {
                                        generationPrediction = (generationPrediction + generationMedianForMonth) / 2.0;
                                        battPrediction = _Batt.CapacityKiloWattHoursToPercent(generationPrediction);
                                        notes.AppendLine($"Low: Predicted generation of {generationPrediction:0.0}kWH ({battPrediction:0}%) adjusted towards monthly median of {generationMedianForMonth}kWH.");
                                    }
                                    powerRequired = bup.GetKwkh(p.Start.DayOfWeek, plan.Plans.GetNext(p).Start.Hour, peak.Start.Hour);
                                    battRequired = _Batt.CapacityKiloWattHoursToPercent(powerRequired);
                                    notes.AppendLine($"     Predicted        use of {powerRequired:0.0}kW ({battRequired:0}%).");

                                    double freeKw = plan.Plans.FutureFreeHoursBeforeNextDischarge(p) * 3.2;
                                    if (freeKw > 0)
                                    {
                                        notes.AppendLine($"    Free: {freeKw:0.0}kW.");
                                    }

                                    double powerAvailableForBatt = generationPrediction - powerRequired + freeKw;

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

                                    bool buyToSellAtPeak = false;
                                    if (peak != null && next != null)
                                    {
                                        decimal solarSoldImmediately = next.Sell;
                                        decimal profitOnBoughtAndSold = (peak.Sell * 0.89M - p.Buy);
                                        decimal totalBuyToSell = solarSoldImmediately + profitOnBoughtAndSold;
                                        if (totalBuyToSell > peak.Sell)
                                        {
                                            buyToSellAtPeak = true;
                                            notes.AppendLine($"     store and sell {peak.Sell:0.000} < (buy and sell {totalBuyToSell:0.000} = ({peak.Sell:0.00} * 0.89 - {p.Buy:0.00})) + (sell immediately {solarSoldImmediately:0.00})  therefore buy to sell at peak.");
                                            // Need to keep battery space for generation over 3.6kW that would otherwise be clipped.
                                            // Plan should specify charge last.
                                        }
                                        else
                                        {
                                            notes.AppendLine($"     store and sell {peak.Sell:0.000} >= (buy and sell {totalBuyToSell:0.000} = ({peak.Sell:0.00} * 0.89 - {p.Buy:0.00})) + (sell immediately {solarSoldImmediately:0.00})  therefore do not buy to sell at peak.");
                                            // Make sure that all solar goes to battery.
                                            // Therefore be cautious about how much to buy.
                                        }
                                    }

                                    double predictedGenerationToBatt = powerAvailableForBatt > 0 ? _Batt.CapacityKiloWattHoursToPercent(powerAvailableForBatt) : 0;
                                    notes.AppendLine($"     Generation prediction factor: {(predictedGenerationToBatt / battDischargeableAtPeak).ToString("0.0")}");
                                    notes.AppendLine($"     Power to batt: {powerAvailableForBatt:0.0}kW ({predictedGenerationToBatt:0}%).");
                                    if (predictedGenerationToBatt > battDischargeableAtPeak * 2)
                                    {
                                        notes.AppendLine($"     Generation prediction is high.");
                                        if (predictedGenerationToBatt > battDischargeableAtPeak * 3 && generationPrediction > generationMedianForMonth)
                                        {
                                            notes.AppendLine($"       Charge from grid overidden from {chargeFromGrid:0}% to {(buyToSell ? 21 : 13)}%.");
                                            chargeFromGrid = _Batt.BatteryMinimumLimit;
                                        }
                                        else if (chargeFromGrid > (buyToSell ? 34 : 21))
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
                                        notes.AppendLine($"     Generation prediction is low (factor {(predictedGenerationToBatt / battDischargeableAtPeak).ToString("0.0")}): charge to {_Batt.BatteryMinimumLimit + battDischargeableAtPeak}%. ");
                                        chargeFromGrid = _Batt.BatteryMinimumLimit + battDischargeableAtPeak;
                                    }
                                    else
                                    {
                                        chargeFromGrid = battDischargeableAtPeak - Convert.ToInt32(predictedGenerationToBatt);
                                        chargeFromGrid = chargeFromGrid < 8 ? 8 : chargeFromGrid;
                                        chargeFromGrid = (generationPrediction < 34) && (chargeFromGrid < 13) ? 13 : chargeFromGrid;
                                        if (!buyToSellAtPeak && chargeFromGrid > 100 - battDischargeableAtPeak && predictedGenerationToBatt > battDischargeableAtPeak)
                                        {
                                            notes.AppendLine($"     chargeFromGrid: {chargeFromGrid:0}% reduced to {100 - battDischargeableAtPeak}% because all generation must go to battery.");
                                            chargeFromGrid = 100 - battDischargeableAtPeak;
                                        }
                                        notes.AppendLine($"     chargeFromGrid: {chargeFromGrid:0}%.");
                                        int battLevelEnd = _Batt.BatteryMinimumLimit + battDischargeableAtPeak + 8;
                                        battLevelEnd = battLevelEnd > 100 ? 100 : battLevelEnd;
                                        if (chargeFromGrid > battLevelEnd)
                                        {
                                            notes.AppendLine($"     chargeFromGrid limited to {battLevelEnd} = min ({_Batt.BatteryMinimumLimit}) + peak dischargeable ({battDischargeableAtPeak}) + 8%.");
                                            chargeFromGrid = battLevelEnd;
                                        }
                                    }
                                }
                            }
                            catch (Exception e)
                            {
                                Logger.LogError(e, "Failed to execute cloud query.");
                            }

                            try
                            {
                                Dictionary<string, string> settings = await _Lux.GetSettingsAsync();
                                (_, int bcSince, int bcPeriod) = _Lux.GetBatteryCalibration(settings);
                                if ((bcSince > bcPeriod - 3))
                                {
                                    notes.AppendLine($"Battery calibration: {bcSince} / {bcPeriod}. *** Charging overridden from {chargeFromGrid} to 100. ***");
                                    chargeFromGrid = 100;
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

                            p.Action = new PeriodAction()
                            {
                                ChargeFromGrid = Convert.ToInt32(chargeFromGrid),
                                DischargeToGrid = 100,
                                //BatteryChargeRate = 100,
                                //BatteryGridDischargeRate = 0,
                            };
                            break;
                        case FluxCase.Zero:
                            p.Action = new PeriodAction()
                            {
                                ChargeFromGrid = 100,
                                DischargeToGrid = 100,
                                //BatteryChargeRate = 100,
                                //BatteryGridDischargeRate = 0,
                            };
                            break;
                    }
                }

                // check for discharge, z, charge.
                if (DateTime.Today.Month >= 5 && DateTime.Today.Month <= 9)
                {
                    foreach (PeriodPlan p1 in plan.Plans.Where(z => Plan.DischargeToGridCondition(z)))
                    {
                        PeriodPlan? p2 = plan.Plans.GetNext(p1);
                        PeriodPlan? p3 = plan.Plans.GetNext(p2);
                        if (p2 != null && GetFluxCase(plan, p2) == FluxCase.Daytime && p3 != null && Plan.ChargeFromGridCondition(p3))
                        {
                            DateTime gEnd = p1.Start;
                            try
                            {
                                (gEnd, _) = (await InfluxQuery.QueryAsync(Query.EndOfGeneration, p1.Start)).First().FirstOrDefault<double>();
                            }
                            catch { }
                            if (gEnd > p1.Start)
                            {
                                p2.Action.DischargeToGrid = p3.Action.ChargeFromGrid;
                            }
                        }
                    }
                }

                PlanService.Save(plan);
                Email.SendPlanEmail(plan, notes.ToString());
            }
            catch (Exception e)
            {
                Logger.LogError("PlanFlux2 failed; rescheduling.");
                _At.Schedule(async () => await this.WorkAsync(CancellationToken.None), DateTime.Now.AddMinutes(2));
            }
        }
    }
}
