using InfluxDB.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Luxopus.Services
{
    internal interface IInfluxWriterService
    {
        Task WriteAsync(LineDataBuilder lineData);
    }

    internal class InfluxWriterService : InfluxService, IInfluxWriterService
    {
        public InfluxWriterService(ILogger<InfluxWriterService> logger, IOptions<InfluxDBSettings> settings) : base(logger, settings) { }

        public async Task WriteAsync(LineDataBuilder lineData)
        {
            IWriteApiAsync w = Client.GetWriteApiAsync();
            string[] lines = lineData.GetLineData();
            await w.WriteRecordsAsync(lines, InfluxDB.Client.Api.Domain.WritePrecision.S, "cstest", Settings.Org);
        }
    }

    internal class LineDataBuilder
    {
        private readonly List<string> _Lines;

        public LineDataBuilder()
        {
            _Lines = new List<string>();
        }

        public void Add(string measurement, string fieldKey, object fieldValue)
        {
            Add(measurement, new Dictionary<string, string>(), fieldKey, fieldValue, DateTime.Now);
        }

        public void Add(string measurement, string fieldKey, object fieldValue, DateTime time )
        {
            Add(measurement, new Dictionary<string, string>(), fieldKey, fieldValue, time);
        }

        public void Add(string measurement, IEnumerable<KeyValuePair<string, string>> tags, string fieldKey, object fieldValue, DateTime time)
        {
            string tagString = "";
            if(tags.Count() > 0)
            {
                tagString = "," + string.Join(", ", tags.Select(z => $"{z.Key}={z.Value}"));
            }
            _Lines.Add($"{measurement}{tagString} {fieldKey}={fieldValue} {time.ToUnix()}");
        }

        public string[] GetLineData()
        {
            return _Lines.ToArray();
        }
    }
}
