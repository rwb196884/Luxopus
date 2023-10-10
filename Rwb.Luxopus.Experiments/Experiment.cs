using Accord.Collections;
using Accord.MachineLearning.Bayes;
using Accord.Neuro;
using Accord.Neuro.Learning;
using Accord.Statistics.Distributions.Univariate;
using Accord.Statistics.Models.Regression.Linear;
using InfluxDB.Client.Core.Flux.Domain;
using Microsoft.Extensions.Logging;
using Rwb.Luxopus.Jobs;
using Rwb.Luxopus.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Experiments
{
    internal class Experiment
    {
        private readonly ILogger<Experiment> _Logger;
        private readonly IInfluxQueryService _InfluxQuery;
        private readonly IInfluxWriterService _InfluxWriter;

        public Experiment(ILogger<Experiment> logger, IInfluxQueryService influxQuery, IInfluxWriterService influxWriter)
        {
            _Logger = logger;
            _InfluxQuery = influxQuery;
            _InfluxWriter = influxWriter;
        }

        class Datum
        {
            public DateTime Time;
            public long? Batt;
            public double? Cloud;
            public long? Daylen;
            public double? Elevation;
            public long? Generation;
            public double? Solcast;
            public double? Uvi;

            public bool IsComplete
            {
                get
                {
                    return Batt.HasValue && Cloud.HasValue && Daylen.HasValue
                        && Elevation.HasValue && Generation.HasValue && Solcast.HasValue && Uvi.HasValue;
                }
            }

            public double[] Input
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

        public async Task RunAsync()
        {
            //await RunNeuralNetAsync();
            //await RunLinearRegressionAsync();
            //await RunLinearRegressionAsyncWithSolcast();
            await GenerateLinearRegressionPredictionsAsync();
        }

        private async Task<List<Datum>> LoadData()
        {
            FluxTable fluxData = (await _InfluxQuery.QueryAsync(Query.PredictionData, DateTime.Now)).Single();
            return fluxData.Records.Select(z => new Datum()
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

        private async Task<List<Datum2>> LoadData2()
        {
            FluxTable fluxData = (await _InfluxQuery.QueryAsync(Query.PredictionData2, DateTime.Now)).Single();
            return fluxData.Records.Select(z => new Datum2()
            {
                Time = z.GetValue<DateTime>("_time"),
                Cloud = z.GetValue<double?>("cloud"),
                Daylen = z.GetValue<double?>("daylen"),
                Elevation = z.GetValue<double?>("elevation"),
                Generation = z.GetValue<long?>("generation"),
                Uvi = z.GetValue<double?>("uvi"),
            }).ToList();
        }

        public async Task RunNeuralNetAsync()
        {
            List<Datum2> data = await LoadData2();
            ActivationNetwork network = new ActivationNetwork(new SigmoidFunction(),
                4, // inputs: cloud, daylen, elevation, uvi
                16,
                1);
            BackPropagationLearning teacher = new BackPropagationLearning(network);

            IEnumerable<Datum2> trainingData = data.Where(z => z.IsComplete && z.Time < new DateTime(2023, 9, 1));
            double[][] inputs = trainingData.Select(z => z.Input).ToArray();
            double[][] outputs = trainingData.Select(z => z.Output).ToArray();
            double error = double.MaxValue;
            int iteration = 0;
            while (error > 0.1)
            {
                error = teacher.RunEpoch(inputs, outputs);
                iteration++;
                _Logger.LogDebug($"Learning iteration {iteration} has error {error:#,##0.000}");
            }

            foreach (Datum2 testDatum in data.Where(z => z.IsComplete && z.Time >= new DateTime(2023, 9, 1)))
            {
                double[] output = network.Compute(testDatum.Input);
                _Logger.LogDebug($"Prediction: {output[0]:#,##0}, Actual: {testDatum.Output[0]:#,##0}");
            }
        }

        public async Task RunLinearRegressionAsync()
        {
            List<Datum2> data = await LoadData2();
            OrdinaryLeastSquares ols = new OrdinaryLeastSquares();
            IEnumerable<Datum2> trainingData = data.Where(z => z.IsComplete /*&& z.Time < new DateTime(2023, 9, 1)*/);
            double[][] inputs = trainingData.Select(z => z.Input).ToArray();
            double[][] outputs = trainingData.Select(z => z.Output).ToArray();
            MultivariateLinearRegression regression = ols.Learn(inputs, outputs);
            double errorPlus = 0;
            int nPlus = 0;
            double errorMinus = 0;
            int nMinus = 0;
            IEnumerable<Datum2> testData = data.Where(z => z.IsComplete /*&& z.Time >= new DateTime(2023, 9, 1)*/);
            foreach (Datum2 testDatum in testData)
            {
                double[] output = regression.Transform(testDatum.Input);
                if (output[0] > testDatum.Output[0])
                {
                    errorPlus += output[0] - testDatum.Output[0];
                    nPlus++;
                }
                else if (output[0] < testDatum.Output[0])
                {
                    errorMinus += testDatum.Output[0] - output[0];
                    nMinus++;
                }
                // _Logger.LogDebug($"Prediction: {output[0]:#,##0}, Actual: {testDatum.Output[0]:#,##0}");
            }
            _Logger.LogDebug($"Error over: {100 * nPlus / testData.Count()} mean {errorPlus / Convert.ToDouble(nPlus):0.0}");
            // 60%, 64
            _Logger.LogDebug($"Error under: {100 * nMinus / testData.Count()} mean {errorPlus / Convert.ToDouble(errorMinus):0.0}");
            // 39%, 2
        }
        public async Task RunLinearRegressionAsyncWithSolcast()
        {
            List<Datum> data = await LoadData();
            OrdinaryLeastSquares ols = new OrdinaryLeastSquares();
            IEnumerable<Datum> trainingData = data.Where(z => z.IsComplete /*&& z.Time < new DateTime(2023, 9, 1)*/);
            double[][] inputs = trainingData.Select(z => z.Input).ToArray();
            double[][] outputs = trainingData.Select(z => z.Output).ToArray();
            MultivariateLinearRegression regression = ols.Learn(inputs, outputs);
            double errorPlus = 0;
            int nPlus = 0;
            double errorMinus = 0;
            int nMinus = 0;
            IEnumerable<Datum> testData = data.Where(z => z.IsComplete /*&& z.Time >= new DateTime(2023, 9, 1)*/);
            foreach (Datum testDatum in testData)
            {
                double[] output = regression.Transform(testDatum.Input);
                if (output[0] > testDatum.Output[0])
                {
                    errorPlus += output[0] - testDatum.Output[0];
                    nPlus++;
                }
                else if (output[0] < testDatum.Output[0])
                {
                    errorMinus += testDatum.Output[0] - output[0];
                    nMinus++;
                }
                // _Logger.LogDebug($"Prediction: {output[0]:#,##0}, Actual: {testDatum.Output[0]:#,##0}");
            }
            _Logger.LogDebug($"Error over: {100 * nPlus / testData.Count()} mean {errorPlus / Convert.ToDouble(nPlus):0.0}");
            // 48%, 60
            _Logger.LogDebug($"Error under: {100 * nMinus / testData.Count()} mean {errorPlus / Convert.ToDouble(errorMinus):0.0}");
            // 51%, 1
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
        public async Task GenerateLinearRegressionPredictionsAsync()
        {
            LineDataBuilder ldb = new LineDataBuilder();

            List<Datum2> data = await LoadData2();
            DateTime t = (new DateTime(2023, 3, 1, 16, 0, 0)).ToUniversalTime();
            while (t < DateTime.Now.AddDays(-2))
            {
                OrdinaryLeastSquares ols = new OrdinaryLeastSquares();
                IEnumerable<Datum2> trainingData = data.Where(z => z.IsComplete && z.Time < t);
                if (!trainingData.Any()) { t = t.AddDays(1); continue; }
                double[][] inputs = trainingData.Select(z => z.Input).ToArray();
                double[][] outputs = trainingData.Select(z => z.Output).ToArray();
                MultivariateLinearRegression regression = ols.Learn(inputs, outputs);

                Datum2 td = data.Where(z => z.IsComplete && z.Time >= t).OrderBy(z => z.Time).First();
                double[] prediction = regression.Transform(td.Input);
                ldb.Add("prediction", "MultivariateLinearRegression", prediction[0], t);

                t = t.AddDays(1);
            }
            await _InfluxWriter.WriteAsync(ldb);
        }

        public async Task RunNaiveBayesAsync()
        {
            List<Datum> data = await LoadData();

            NaiveBayes bayes = new NaiveBayes(classes: 2, symbols: new[] { 0, 100 });
            NaiveBayesLearning learner = new NaiveBayesLearning()
            {
                Model = bayes
            };

            //NaiveBayesLearning<NormalDistribution> learner = new NaiveBayesLearning<NormalDistribution>();
            IEnumerable<Datum> trainingData = data.Where(z => z.IsComplete && z.Time < new DateTime(2023, 9, 1));
            int[][] inputs = trainingData.Select(z => z.Input.Select(z => Convert.ToInt32(z)).ToArray()).ToArray();
            int[][] outputs = trainingData.Select(z => z.Output.Select(z => z > 150 ? 100 : 0).ToArray()).ToArray();
            var nb = learner.Learn(inputs, outputs);

            foreach (Datum testDatum in data.Where(z => z.IsComplete && z.Time >= new DateTime(2023, 9, 1)))
            {
                int[] input = testDatum.Input.Select(z => Convert.ToInt32(z)).ToArray();
                int output = nb.Decide(input);
                _Logger.LogDebug($"Prediction: {output:#,##0}, Actual: {testDatum.Output[0]:#,##0}");
            }
        }
    }
}
