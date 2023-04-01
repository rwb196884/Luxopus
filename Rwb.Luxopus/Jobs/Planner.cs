using InfluxDB.Client.Core.Flux.Domain;
using Rwb.Luxopus.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Jobs
{
    public class HalfHourPlan : ElectricityPrice
    {
        // Base class: date, buy price, sell price.

        public PeriodAction Action { get; set; }

        public PeriodState ExpectedStartState { get; set; }
        public PeriodState ExpectedEndState { get; set; }

        public PeriodState ActualStartState { get; set; }
        public PeriodState ActualEndState { get; set; }

        public HalfHourPlan(ElectricityPrice e)
        {
            Start = e.Start;
            Buy = e.Buy;
            Sell = e.Sell;
        }

        public override string ToString()
        {
            return $"{Start.ToString("dd MMM HH:mm")} {Sell.ToString("00.0")}-{Buy.ToString("00.0")}";
        }
    }

    public class PeriodAction
    {
        public bool ChargeFromGrid { get; set; }
        public bool ChargeFromGeneration { get; set; }
        public bool DischargeToGrid { get; set; }
    }

    public class PeriodState
    {
        public Boolean Generating { get; set; }
        public Boolean Importing { get; set; }
        public Boolean Exporting { get; set; }
        public Boolean Storing { get; set; }
    }

    /// <summary>
    /// <para>
    /// Buy/sell strategy
    /// </para>
	/// <para>
	/// Probably heavily biassed to the UK market with lots of hard-coded assumptions.
	/// </para>
    /// </summary>
    public class Planner : Job
    {
        private readonly IInfluxQueryService _InfluxQuery;

        public Planner(ILogger<LuxMonitor> logger, IInfluxQueryService influxQuery) : base(logger)
        {
            _InfluxQuery = influxQuery;
        }

        public override async Task RunAsync(CancellationToken cancellationToken)
        {
            // Make a plan! (And write it down.)
            DateTime day = DateTime.Now;

            // First collect some data.

            // How much battery does the house use over night?
            int battForNight = 30;



            // TO DO:
            // * integrate usage (lq, med, uq) to estimate typical energy use by time of day.
            // * integrate batt charge/discharge to estimate how much energy is a percent.
            // * 'night' and 'day' depend on time of year and time zone daylight saving.

            // How much battery does the house use throgh the day?
            int battForDay = 40; // 262/210 * 30% from Solar dashboard.

            // How fast does the battery discharge to the grid? (Percent per half hour.)
            int battDischargePerHalfHour = 20;

            // Current battery level.
            int batteryLevelNow = await GetBatteryLevel();

            // Prices.
            List<ElectricityPrice> prices = await _InfluxQuery.GetPricesAsync(DateTime.Now);
            decimal pEveningSellMax = 19;
            decimal pNightBuyMin = 18;
            decimal pMorningSellMax = 14;
            decimal pDaytimeMedianSell = 12;
            decimal pAfternoonBuyMin = 20;

            // Charging forecast.
            int batteryForecast = 120;



            /*
             * 	# run at about 4pm
	battNightUse="30"
	battDayUse="40" # 262/210 * 30% from Solar dashboard.
	battDischargePerHalfHour="10" # ~3.6kW@90% | 15 mins: 93 to 
	battSoc="0"
	eveningSellMax="20"
	overnightBuyMin="15"
	morningSellMax="15"
	forecast="0" # Estimated addition to battery percent.
	
	# Do we need battery for tomorrow daytime?
	dischargeBattTarget=5
	if [ "$forecast" -gt 0 ]l then
		dischargeBattTarget=40
	fi
	
	# Set PM discharge.
	luxDischargeToGrid $dischargeStart $discahrgeEnd $dischargeBattTarget
	
	# Should we sell more now and buy back over night for the morning and tomorrow?
	# Yes if the over night buy price 
	
	# Do we want to sell tomorrow morning?
	if [ "$morningSellMax" -gt "$overnightBuyMin" * 1.2 ]; then
	
	fi
	
	chargeBattTarget="99"
	if [ "$overnightBuyMin" -le 0 ]; then
		chargeBattTarget="100"
	fi;
	
	if [ "$forecast" -lt 10 ]l then
		chargeBattTarget=40
	fi
	
	# Set over night charge
	luxChargeFromGrid $chargeStart $chargeEnd $chargeBattTarget
	luxBattChargeRateSet 100
	
	# Set morning discharge. Aim for full by PM.
	
	# Solcast runs at 7am so it might get a better prediction.
	
	
	# Send SMS.

			*/
        }

        private async Task<int> GetBatteryLevel()
        {
            string flux = $@"
from(bucket:""{_InfluxQuery.Bucket}"")
  |> range(start: -15m, stop: now())
  |> filter(fn: (r) => r[""_measurement""] == ""inverter"" and r[""_field""] == ""level"")
  |> last()
";
            List<FluxTable> q = await _InfluxQuery.QueryAsync(flux);
            if (q.Count > 0 && q[0].Records.Count > 0)
            {
                object o = q[0].Records[0].Values["_time"];
                return (int)o;
            }
            return 0;
        }
    }
}
