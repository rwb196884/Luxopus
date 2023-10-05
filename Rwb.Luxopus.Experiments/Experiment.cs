using Accord.Collections;
using Accord.MachineLearning.Bayes;
using Accord.Neuro;
using Accord.Neuro.Learning;
using Accord.Statistics.Distributions.Univariate;
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

        public Experiment(ILogger<Experiment> logger, IInfluxQueryService influxQuery)
        {
            _Logger = logger;
            _InfluxQuery = influxQuery;
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

        public async Task RunAsync()
        {
            await RunNeuralNetAsync();
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

        public async Task RunNeuralNetAsync()
        {
            List<Datum> data = await LoadData();
            ActivationNetwork network = new ActivationNetwork(new SigmoidFunction(),
                5, // inputs: cloud, daylen, elevation, solcast, uvi
                10, 
                1);
            BackPropagationLearning teacher = new BackPropagationLearning(network);

            IEnumerable<Datum> trainingData = data.Where(z => z.IsComplete && z.Time < new DateTime(2023, 9, 1));
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

            foreach (Datum testDatum in data.Where(z => z.IsComplete && z.Time >= new DateTime(2023, 9, 1)))
            {
                double[] output = network.Compute(testDatum.Input);
                _Logger.LogDebug($"Prediction: {output[0]:#,##0}, Actual: {testDatum.Output[0]:#,##0}");
            }
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
