using Microsoft.Extensions.Logging;
using Rwb.Luxopus.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Experiments
{
    internal class DayTypeClassification : Experiment
    {
        class DatumForDayType : Datum
        {
            public long? Import;
            public long? Export;

            public override double[] Output { get { return new[] { Convert.ToDouble(Generation.Value) }; } }
        }

        public DayTypeClassification(ILogger<DayTypeClassification> logger, IInfluxQueryService influxQuery, IInfluxWriterService influxWriter)
            : base(logger, influxQuery, influxWriter) { }

        public override async Task RunAsync()
        {
            List<Datum> data = await LoadData();
        }

    }
}
