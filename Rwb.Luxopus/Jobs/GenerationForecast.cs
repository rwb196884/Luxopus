using Accord.Statistics.Models.Regression.Linear;
using InfluxDB.Client.Core.Flux.Domain;
using Microsoft.Extensions.Logging;
using Rwb.Luxopus.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Jobs
{

    public class GenerationForecast : Job
    {
        protected readonly IInfluxQueryService _InfluxQuery;
        protected readonly IInfluxWriterService _InfluxWriter;
        public GenerationForecast(ILogger<GenerationForecast> logger,
                        IInfluxQueryService influxQuery,
            IInfluxWriterService influxWriter) : base(logger)
        {
            _InfluxQuery = influxQuery;
            _InfluxWriter = influxWriter;
        }

        protected override async Task WorkAsync(CancellationToken cancellation)
        {
            DateTime tForecast = DateTime.Today.ToUniversalTime().AddHours(24 + 2);
            double generationPrediction = await GenerationPredictionFromMultivariateLinearRegression(tForecast);
            LineDataBuilder ldb = new LineDataBuilder();
            ldb.Add("prediction", "MultivariateLinearRegression", generationPrediction * 10, tForecast);

            // Do some future predictions too.
            double tomorrow = await GenerationPredictionFromMultivariateLinearRegression(tForecast.AddDays(1));
            ldb.Add("prediction", "MultivariateLinearRegression", tomorrow * 10, tForecast.AddDays(1));
            tomorrow = await GenerationPredictionFromMultivariateLinearRegression(tForecast.AddDays(2));
            ldb.Add("prediction", "MultivariateLinearRegression", tomorrow * 10, tForecast.AddDays(2));
            tomorrow = await GenerationPredictionFromMultivariateLinearRegression(tForecast.AddDays(3));
            ldb.Add("prediction", "MultivariateLinearRegression", tomorrow * 10, tForecast.AddDays(3));
            await _InfluxWriter.WriteAsync(ldb);
        }

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

        private async Task<double> GenerationPredictionFromMultivariateLinearRegression(DateTime tForecast)
        {
            // Get daat.
            FluxTable fluxData = (await _InfluxQuery.QueryAsync(Query.PredictionData2, DateTime.Now)).Single();
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
            OrdinaryLeastSquares ordinaryLeastSquares = new OrdinaryLeastSquares();
            IEnumerable<Datum2> trainingData = data.Where(z => z.IsComplete /*&& z.Time < new DateTime(2023, 9, 1)*/);
            double[][] inputs = trainingData.Select(z => z.Input).ToArray();
            double[][] outputs = trainingData.Select(z => z.Output).ToArray();
            MultivariateLinearRegression multivariateLinearRegression = ordinaryLeastSquares.Learn(inputs, outputs);

            // Use model. Apply the rescaling to the values.
            FluxRecord weather = (await _InfluxQuery.QueryAsync(Query.Weather, tForecast)).First().Records.Single();
            double cloud = Math.Floor(weather.GetValue<double>("cloud") / 10.0);
            double daylen = Math.Floor(weather.GetValue<double>("daylen") * 60 * 60 / 1000.0);
            double uvi = Math.Floor(weather.GetValue<double>("uvi") * 10.0);
            double elevation = Math.Floor(weather.GetValue<double>("elevation")); // Hack in query in case of not full day of data.

            double[] prediction = multivariateLinearRegression.Transform(new double[] { cloud, daylen, elevation, uvi });
            return prediction[0] / 10.0;
        }

    }
}
