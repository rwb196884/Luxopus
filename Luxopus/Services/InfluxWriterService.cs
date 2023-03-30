using InfluxDB.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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

        public void Add(string measurement, string fieldKey, object fieldValue, DateTime time)
        {
            Add(measurement, new Dictionary<string, string>(), fieldKey, fieldValue, time);
        }

        public void Add(string measurement, IEnumerable<KeyValuePair<string, string>> tags, string fieldKey, object fieldValue, DateTime time)
        {
            string tagString = "";
            if (tags.Count() > 0)
            {
                tagString = "," + string.Join(", ", tags.Select(z => $"{z.Key}={z.Value}"));
            }
            _Lines.Add($"{measurement}{tagString} {fieldKey}={fieldValue} {time.ToUnix()}");
        }

        public string[] GetLineData()
        {
            return _Lines.ToArray();
        }

        public void AddFromJson(string json, string measurement)
        {
            using (JsonDocument j = JsonDocument.Parse(json))
            {
                AddFromJson("", j.RootElement, measurement);
            }
        }

        // Untested -- and unused.
        private void AddFromJson(string name, JsonElement json, string measurement)
        {
            switch (json.ValueKind)
            {
                case JsonValueKind.String:
                    Add(measurement, name, "\"" + json.GetString() + "\"");
                    break;
                case JsonValueKind.Number:
                    Add(measurement, name, json.GetInt32());
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    Add(measurement, name, json.GetBoolean());
                    break;
                case JsonValueKind.Array:
                    int i = 0;
                    foreach (JsonElement e in json.EnumerateArray())
                    {
                        AddFromJson(name + "_" + i.ToString(), json, measurement);
                        i++;
                    }
                    break;
                case JsonValueKind.Object:
                    foreach (JsonProperty e in json.EnumerateObject())
                    {
                        AddFromJson(name + "_" + e.Name, e.Value, measurement);
                    }
                    break;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    break;
                default:
                    throw new NotImplementedException($"JsonValueKind {json.ValueKind}");
            }
        }
    }
}
