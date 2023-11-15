using Accord.Neuro;
using Accord.Neuro.Learning;
using Microsoft.Extensions.Logging;
using Rwb.Luxopus.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Experiments
{
    internal class PredictByNeuralNet : Experiment
    {
        public PredictByNeuralNet(ILogger<PredictByNeuralNet> logger, IInfluxQueryService influxQuery, IInfluxWriterService influxWriter)
            : base(logger, influxQuery, influxWriter) { }

        public override async Task RunAsync()
        {
            List<Datum> data = await LoadData();
            ActivationNetwork network = new ActivationNetwork(new SigmoidFunction(),
                4, // inputs: cloud, daylen, elevation, uvi
                16,
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

    }
}
