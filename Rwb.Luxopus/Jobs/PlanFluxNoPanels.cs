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
    /// Plan for 'flux' tariff https://octopus.energy/smart/flux/ when the panels are fucked.
    /// </para>
    /// <para>Buy at low and sell at high.</para>
    /// </summary>
    public class PlanFluxNoPanels : PlanFlux
    {
        private readonly IInfluxWriterService _InfluxWriter;
        private readonly IBatteryService _Batt;
        private readonly IOctopusService _Octopus;
        private readonly IAtService _At;
        private readonly ILuxService _Lux;
        private readonly BatteryUsageProfileService _BatteryUsageProfile;

        public PlanFluxNoPanels(ILogger<LuxMonitor> logger,
            IInfluxQueryService influxQuery,
            IInfluxWriterService influxWriter,
            ILuxopusPlanService plan, IEmailService email,
            IBatteryService batt,
            IOctopusService octopus,
            IAtService at,
            ILuxService lux,
            BatteryUsageProfileService batteryUsageProfile
            )
            : base(logger, influxQuery, plan, email)
        {
            _InfluxWriter = influxWriter;
            _Batt = batt;
            _Octopus = octopus;
            _At = at;
            _Lux = lux;
            _BatteryUsageProfile = batteryUsageProfile;
        }

        protected override async Task WorkAsync(CancellationToken cancellationToken)
        {
            try
            {
                DateTime t0 = DateTime.UtcNow.AddHours(-1);
                //Plan? current = PlanService.Load(t0);
                StringBuilder notes = new StringBuilder();

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

                (int bcSince, int bcPeriod) = (0, 100);
                try
                {
                    Dictionary<string, string> settings = await _Lux.GetSettingsAsync();
                    (_, bcSince, bcPeriod) = _Lux.GetBatteryCalibration(settings);
                }
                catch
                {
                    notes.AppendLine($"*** Failed to get battery calibration info. ***");
                }

                foreach (PeriodPlan p in plan.Plans.Where(z => z.Start >= plan.Current.Start))
                {
                    switch (GetFluxCase(plan, p))
                    {
                        case FluxCase.Peak:
                            p.Action = new PeriodAction()
                            {
                                ChargeFromGrid = 0,
                                DischargeToGrid = _Batt.BatteryMinimumLimit,
                            };

                            if (bcSince > bcPeriod - 3)
                            {
                                p.Action.DischargeToGrid = 100 - _Batt.MaxCharge * 3;
                                notes.AppendLine($"    *** Discharging overridden from {100} to {p.Action.DischargeToGrid}. ***");
                            }

                            break;
                        case FluxCase.Daytime:
                            p.Action = new PeriodAction()
                            {
                                ChargeFromGrid = 0,
                                DischargeToGrid = 100,
                            };

                            break;
                        case FluxCase.Evening:
                            p.Action = new PeriodAction()
                            {
                                ChargeFromGrid = 0,
                                DischargeToGrid = 100
                            };

                            break;
                        case FluxCase.Low:
                            p.Action = new PeriodAction()
                            {
                                ChargeFromGrid = 100,
                                DischargeToGrid = 100
                            };
                            break;
                        case FluxCase.Zero:
                            notes.AppendLine();
                            notes.AppendLine($"-- {p.Start.ToString("dd MMM HH:mm")} | Zero | Buy: {p.Buy.ToString("0.00")} | Sell: {p.Sell.ToString("0.00")}. --");
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
