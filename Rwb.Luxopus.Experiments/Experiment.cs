using InfluxDB.Client.Core.Flux.Domain;
using Microsoft.Extensions.Logging;
using Rwb.Luxopus.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Experiments
{
    internal abstract class Experiment
    {
        protected readonly ILogger<Experiment> _Logger;
        protected readonly IInfluxQueryService _InfluxQuery;
        protected readonly IInfluxWriterService _InfluxWriter;

        public Experiment(ILogger<Experiment> logger, IInfluxQueryService influxQuery, IInfluxWriterService influxWriter)
        {
            _Logger = logger;
            _InfluxQuery = influxQuery;
            _InfluxWriter = influxWriter;
        }


        public class Datum
        {
            public DateTime Time;
            public double? Cloud;
            public double? Daylen;
            public double? Elevation;
            public long? Generation;
            public double? Uvi;

            public virtual bool IsComplete
            {
                get
                {
                    return Cloud.HasValue && Daylen.HasValue
                        && Elevation.HasValue && Generation.HasValue && Uvi.HasValue;
                }
            }

            public virtual double[] Input
            {
                get
                {
                    return new[]
            {
                Convert.ToDouble(Cloud.Value), Convert.ToDouble(Daylen.Value), Convert.ToDouble(Elevation.Value), Convert.ToDouble(Uvi.Value)
            };
                }
            }

            public virtual double[] Output { get { return new[] { Convert.ToDouble(Generation.Value) }; } }
        }

        public class DatumWithSolcast : Datum
        {
            public long? Batt;
            public double? Solcast;

            public override bool IsComplete
            {
                get
                {
                    return base.IsComplete && Batt.HasValue && Solcast.HasValue;
                }
            }

            public override double[] Input
            {
                get
                {
                    return new[]
            {
                Cloud.Value, Convert.ToDouble(Daylen.Value), Elevation.Value,
                Solcast.Value, Uvi.Value
            };
                }
            }

            public double[] Output { get { return new[] { Convert.ToDouble(Generation.Value) }; } }
        }

        public abstract Task RunAsync();

        protected async Task<List<Datum>> LoadData()
        {
            FluxTable fluxData = (await _InfluxQuery.QueryAsync(Query.PredictionData2, DateTime.Now)).Single();
            return fluxData.Records.Select(z => new Datum()
            {
                Time = z.GetValue<DateTime>("_time"),
                Cloud = z.GetValue<double?>("cloud"),
                Daylen = z.GetValue<double?>("daylen"),
                Elevation = z.GetValue<double?>("elevation"),
                //Generation = z.GetValue<long?>("generation"),
                Generation = z.GetValue<long?>("burst"),
                Uvi = z.GetValue<double?>("uvi"),
            }).ToList();
        }

        protected async Task<List<DatumWithSolcast>> LoadDataWithSolcast()
        {
            FluxTable fluxData = (await _InfluxQuery.QueryAsync(Query.PredictionData, DateTime.Now)).Single();
            return fluxData.Records.Select(z => new DatumWithSolcast()
            {
                Time = z.GetValue<DateTime>("_time"),
                Batt = z.GetValue<long?>("batt_level"),
                Cloud = z.GetValue<double?>("cloud"),
                Daylen = z.GetValue<long?>("daylen"),
                Elevation = z.GetValue<double?>("elevation"),
                Generation = z.GetValue<long?>("generation"),
                Solcast = z.GetValue<double?>("solcast"),
                Uvi = z.GetValue<double?>("uvi"),
            }).ToList();
        }


        // influx delete --org mini31 --bucket solar --start 2023-01-01T00:00:00Z --stop 2023-11-16T00:00:00Z --predicate "_measurement=\"prediction\"" --token $token
        /*
        from(bucket: "solar")
        |> range(start: -2mo, stop: today())
        |> filter(fn: (r) => r["_measurement"] == "prediction" or r["_measurement"] == "daily")
        |> filter(fn: (r) => r["_field"] == "PredictionFromMultivariateLinearRegression" or r["_field"] == "generation")
        |> aggregateWindow(every: 1d, fn: first, createEmpty: false)
        |> yield(name: "mean")
        */
    }
}
