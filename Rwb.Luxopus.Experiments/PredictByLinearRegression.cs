using Accord.MachineLearning.Bayes;
using Accord.Statistics.Models.Regression.Linear;
using Microsoft.Extensions.Logging;
using Rwb.Luxopus.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Experiments
{
    internal class PredictByLinearRegression : Experiment
    {
        public PredictByLinearRegression(ILogger<PredictByLinearRegression> logger, IInfluxQueryService influxQuery, IInfluxWriterService influxWriter)
            : base(logger, influxQuery, influxWriter) { }

        public override async Task RunAsync()
        {
            await RunLinearRegressionAsync();
            await RunLinearRegressionAsyncWithSolcast();
        }

        public async Task RunLinearRegressionAsync()
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
            // 60%, 64
            _Logger.LogDebug($"Error under: {100 * nMinus / testData.Count()} mean {errorPlus / Convert.ToDouble(errorMinus):0.0}");
            // 39%, 2
        }

        public async Task RunLinearRegressionAsyncWithSolcast()
        {
            List<DatumWithSolcast> data = await LoadDataWithSolcast();
            OrdinaryLeastSquares ols = new OrdinaryLeastSquares();
            IEnumerable<DatumWithSolcast> trainingData = data.Where(z => z.IsComplete /*&& z.Time < new DateTime(2023, 9, 1)*/);
            double[][] inputs = trainingData.Select(z => z.Input).ToArray();
            double[][] outputs = trainingData.Select(z => z.Output).ToArray();
            MultivariateLinearRegression regression = ols.Learn(inputs, outputs);
            double errorPlus = 0;
            int nPlus = 0;
            double errorMinus = 0;
            int nMinus = 0;
            IEnumerable<DatumWithSolcast> testData = data.Where(z => z.IsComplete /*&& z.Time >= new DateTime(2023, 9, 1)*/);
            foreach (DatumWithSolcast testDatum in testData)
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

        public async Task RunNaiveBayesAsync()
        {
            List<DatumWithSolcast> data = await LoadDataWithSolcast();

            NaiveBayes bayes = new NaiveBayes(classes: 2, symbols: new[] { 0, 100 });
            NaiveBayesLearning learner = new NaiveBayesLearning()
            {
                Model = bayes
            };

            //NaiveBayesLearning<NormalDistribution> learner = new NaiveBayesLearning<NormalDistribution>();
            IEnumerable<DatumWithSolcast> trainingData = data.Where(z => z.IsComplete && z.Time < new DateTime(2023, 9, 1));
            int[][] inputs = trainingData.Select(z => z.Input.Select(z => Convert.ToInt32(z)).ToArray()).ToArray();
            int[][] outputs = trainingData.Select(z => z.Output.Select(z => z > 150 ? 100 : 0).ToArray()).ToArray();
            var nb = learner.Learn(inputs, outputs);

            foreach (DatumWithSolcast testDatum in data.Where(z => z.IsComplete && z.Time >= new DateTime(2023, 9, 1)))
            {
                int[] input = testDatum.Input.Select(z => Convert.ToInt32(z)).ToArray();
                int output = nb.Decide(input);
                _Logger.LogDebug($"Prediction: {output:#,##0}, Actual: {testDatum.Output[0]:#,##0}");
            }
        }


        public async Task GenerateLinearRegressionPredictionsAsync()
        {
            LineDataBuilder ldb = new LineDataBuilder();

            List<Datum> data = await LoadData();
            DateTime t = (new DateTime(2023, 3, 1, 16, 0, 0)).ToUniversalTime();
            while (t < DateTime.Now.AddDays(-2))
            {
                OrdinaryLeastSquares ols = new OrdinaryLeastSquares();
                IEnumerable<Datum> trainingData = data.Where(z => z.IsComplete && z.Time < t);
                if (!trainingData.Any()) { t = t.AddDays(1); continue; }
                double[][] inputs = trainingData.Select(z => z.Input).ToArray();
                double[][] outputs = trainingData.Select(z => z.Output).ToArray();
                MultivariateLinearRegression regression = ols.Learn(inputs, outputs);

                Datum td = data.Where(z => z.IsComplete && z.Time >= t).OrderBy(z => z.Time).First();
                double[] prediction = regression.Transform(td.Input);
                ldb.Add("prediction", "MultivariateLinearRegression", prediction[0], t);

                t = t.AddDays(1);
            }
            await _InfluxWriter.WriteAsync(ldb);
        }

    }
}
