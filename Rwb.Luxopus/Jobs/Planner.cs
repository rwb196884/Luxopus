using InfluxDB.Client.Core.Flux.Domain;
using Rwb.Luxopus.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace Rwb.Luxopus.Jobs
{
    public class HalfHourPlan : ElectricityPrice
    {
        // Base class: date, buy price, sell price.

        public int PredictedGeneration { get; set; }

        public PeriodAction? Action { get; set; }

        //public PeriodState ExpectedStartState { get; set; }
        //public PeriodState ExpectedEndState { get; set; }

        //public PeriodState ActualStartState { get; set; }
        //public PeriodState ActualEndState { get; set; }

        public HalfHourPlan(ElectricityPrice e)
        {
            Start = e.Start;
            Buy = e.Buy;
            Sell = e.Sell;
            Action = null;
        }

        public override string ToString()
        {
            return $"{Start.ToString("dd MMM HH:mm")} {Sell.ToString("00.0")}-{Buy.ToString("00.0")} {Action?.ToString() ?? "no action"}";
        }
    }

    public class PeriodAction
    {
        public bool ChargeFromGrid { get; set; }

        /// <summary>
        /// If false then generation will be sold.
        /// </summary>
        public bool ChargeFromGeneration { get; set; }

        /// <summary>
        /// Battery limit for discharge. Use 100 to disable discharge to grid.
        /// </summary>
        public int DischargeToGrid { get; set; }

        public override string ToString()
        {
            string a = "a:";

            if (ChargeFromGrid)
            {
                a += "(G>B)";
            }
            else
            {
                a += "(G|B)";
            }

            if (ChargeFromGeneration)
            {
                a += "(P>B)";
            }
            else
            {
                a += "(P|B)";
            }

            if (DischargeToGrid < 100)
            {
                a += $"(G<B @{DischargeToGrid})";
            }
            else
            {
                a += "(G|B)";
            }
            return a;
        }
    }

    /// <summary>
    /// <para>
    /// Buy/sell strategy
    /// </para>
    /// <para>
    /// Probably heavily biassed to the UK market with lots of hard-coded assumptions.
    /// </para>
    /// </summary>
    public abstract class Planner : Job
    {
        protected readonly IInfluxQueryService InfluxQuery;

        public Planner(ILogger<LuxMonitor> logger, IInfluxQueryService influxQuery) : base(logger)
        {
            InfluxQuery = influxQuery;
        }

        protected async Task<int> GetBatteryLevelAsync()
        {
            string flux = $@"
from(bucket:""{InfluxQuery.Bucket}"")
  |> range(start: -15m, stop: now())
  |> filter(fn: (r) => r[""_measurement""] == ""inverter"" and r[""_field""] == ""level"")
  |> last()
";
            List<FluxTable> q = await InfluxQuery.QueryAsync(flux);
            if (q.Count > 0 && q[0].Records.Count > 0)
            {
                object o = q[0].Records[0].Values["_time"];
                return (int)o;
            }
            return 0;
        }

        protected async Task<(decimal min, decimal lq, decimal median, decimal mean, decimal uq, decimal max)> GetSolcastFactorsAsync()
        {
            List<FluxTable> q = await InfluxQuery.QueryAsync(Query.SolcastFactors, DateTime.UtcNow);
            FluxRecord r = q[0].Records[0];
            return (
                r.GetValue<decimal>("min"),
                r.GetValue<decimal>("lq"),
                r.GetValue<decimal>("median"),
                r.GetValue<decimal>("mean"),
                r.GetValue<decimal>("uq"),
                r.GetValue<decimal>("max")
                );
        }

        protected async Task<decimal> GetSolcastTomorrowAsync(DateTime today)
        {
            List<FluxTable> q = await InfluxQuery.QueryAsync(Query.SolcastTomorrow, today);
            return q[0].Records[0].GetValue<decimal>();
        }
    }
}
