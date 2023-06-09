﻿using InfluxDB.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Services
{
    public interface IInfluxWriterService
    {
        Task WriteAsync(LineDataBuilder lineData);
    }

    public class InfluxWriterService : InfluxService, IInfluxWriterService
    {
        public InfluxWriterService(ILogger<InfluxWriterService> logger, IOptions<InfluxDBSettings> settings) : base(logger, settings) { }

        public async Task WriteAsync(LineDataBuilder lineData)
        {
            string[] lines = lineData.GetLineData();
            if (lines.Length == 0) { return; }

            IWriteApiAsync w = Client.GetWriteApiAsync();
            int n = 100;
            if(lines.Length > 500)
            {
                n = 50;
            }
            // Do in batches because of shitty timeout.
            Logger.LogInformation($"Writing {lines.Length} lines in {lines.Length / n + 1} batches.");
            for (int i = 0; i <= lines.Length / n; i++)
            {
                while (true)
                {
                    try
                    {
                        await w.WriteRecordsAsync(lines.Skip(i * n).Take(n).ToArray(), InfluxDB.Client.Api.Domain.WritePrecision.S, Settings.Bucket, Settings.Org);
                        if (i > 1)
                        {
                            Thread.Sleep(500);
                            if (i % 8 == 0)
                            {
                                Thread.Sleep(3000); // It's very shit.
                            }
                        }
                        Logger.LogDebug($"Written page {1+i} of {1 + lines.Length / n} to InfluxDB.");
                        break;
                    }
                    catch (Exception e)
                    {
                        if (e.Message.ToLower().Contains("timeout"))
                        {
                            Logger.LogWarning($"Timeout writing page {1 + i} of {1 + lines.Length / n} to InfluxDB. Waiting to retry...");
                            Thread.Sleep(3000);
                        }
                        else
                        {
                            throw;
                        }
                    }
                }
            }
        }
    }

    public class LineDataBuilder
    {
        private readonly List<string> _Lines;

        public LineDataBuilder()
        {
            _Lines = new List<string>();
        }

        public override string ToString()
        {
            return $"{_Lines.Count} lines";
        }

        public void Add(string measurement, string fieldKey, object fieldValue)
        {
            Add(measurement, new Dictionary<string, string>(), fieldKey, fieldValue, DateTime.UtcNow);
        }

        public void Add(string measurement, string fieldKey, object fieldValue, DateTime time)
        {
            Add(measurement, new Dictionary<string, string>(), fieldKey, fieldValue, time);
        }

        public void Add(string measurement, IEnumerable<KeyValuePair<string, string>> tags, string fieldKey, object fieldValue, DateTime time)
        {
            if( time.Kind != DateTimeKind.Utc)
            {
                throw new NotImplementedException();
            }

            Instant i = Instant.FromDateTimeUtc(time);

            string cunt = "";
            if(fieldValue.GetType() == typeof(byte) || fieldValue.GetType() == typeof(short) || fieldValue.GetType() == typeof(int) || fieldValue.GetType() == typeof(long))
            {
                cunt = "i";
            }

            string tagString = "";
            if (tags.Count() > 0)
            {
                tagString = "," + string.Join(",", tags.Select(z => $"{z.Key}={z.Value}"));
            }
            _Lines.Add($"{measurement}{tagString} {fieldKey}={fieldValue}{cunt} {i.ToUnixTimeSeconds()}");
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
