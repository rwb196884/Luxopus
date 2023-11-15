using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Services
{
    public static class JsonQueryExtensions
    {
        public static IEnumerable<JsonElement> GetArray(this JsonElement source, string propertyName)
        {
            return (new List<JsonElement>() { source }).GetArray(propertyName);
        }

        public static IEnumerable<JsonElement> GetArray(this IEnumerable<JsonElement> source, string propertyName)
        {
            return source.SelectMany(z => z.EnumerateObject().Single(y => y.Name == propertyName).Value.EnumerateArray());
        }

        public static IEnumerable<JsonElement> GetProperty(this IEnumerable<JsonElement> source, string propertyName)
        {
            return source.Select(z => z.EnumerateObject().Single(z => z.Name == propertyName).Value);
        }

        public static IEnumerable<JsonElement> GetArrayWhere(this IEnumerable<JsonElement> source, string propertyName, string conditionPropertyName, string conditionPropertyValue)
        {
            return source.GetArray(propertyName).Where(z => z.EnumerateObject().Single(z => z.Name == conditionPropertyName).Value.GetString() == conditionPropertyValue);
        }

        public static IEnumerable<string> GetPropertValueAsString(this IEnumerable<JsonElement> source, string propertyName)
        {
            return source.GetProperty(propertyName).Select(z => z.GetString());
        }

        public static DateTime? GetDate(this JsonProperty p)
        {
            if (p.Value.ValueKind == JsonValueKind.String)
            {
                return DateTime.Parse(p.Value.GetString());
            }
            return null;
        }
    }

    public class OctopusSettings : Settings
    {
        public string ApiKey { get; set; }
        public string BaseAddress { get; set; }
        public string AccountNumber { get; set; }

        //public string TimeZone { get; set; } // Not needeed because Octopus data is in format yyyy-MM-ddTHH:MM:sszzz
        //public string Mapn { get; set; }

        // Comma separated list of additional tariffs to get prices for.
        public string AdditionalTariffs { get; set; }
    }

    public enum TariffType
    {
        Import,
        Export
    }

    public class TariffCode
    {
        public string Code { get; set; }
        public DateTime ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }
        public TariffType TariffType { get; set; }

        public override string ToString()
        {
            return $"{Code} {ValidFrom.ToString("dd MMM HH:mm")} {ValidTo?.ToString("dd MMM HH:mm") ?? "->"}";
        }
    }

    public class MeterReading
    {
        public DateTime IntervalStart { get; set; }
        public DateTime IntervalEnd { get; set; }
        public decimal Consumption { get; set; }

        public override string ToString()
        {
            return $"{IntervalStart.ToString("dd MMM HH:mm")} {Consumption.ToString("0.00")}";
        }
    }

    public class Price
    {
        public decimal Pence { get; set; }
        public DateTime ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }

        public override string ToString()
        {
            return $"{ValidFrom.ToString("dd MMM HH:mm")} {Pence.ToString("0.00")}";
        }
    }

    public interface IOctopusService
    {
        Task<IEnumerable<string>> GetElectricityMeterPoints();
        Task<IEnumerable<string>> GetElectricityMeters(string mapn);
        Task<IEnumerable<MeterReading>> GetElectricityMeterReadings(string mapn, string serialNumber, DateTime from, DateTime to);
        Task<IEnumerable<TariffCode>> GetElectricityTariffs();
        Task<TariffCode> GetElectricityCurrentTariff(TariffType tariffType, DateTime at);
        Task<IEnumerable<Price>> GetElectricityPrices(string product, string tariff, DateTime from, DateTime to);

        Task<IEnumerable<string>> GetGasMeterPoints();
        Task<IEnumerable<string>> GetGasMeters(string mprn);
        Task<IEnumerable<MeterReading>> GetGasMeterReadings(string mprn, string serialNumber, DateTime from, DateTime to);
        Task<IEnumerable<TariffCode>> GetGasTariffs();
        Task<IEnumerable<Price>> GetGasPrices(string product, string tariff, DateTime from, DateTime to);
    }

    public class OctopusService : Service<OctopusSettings>, IOctopusService
    {
        private const string DateFormat = "yyyy-MM-ddTHH:MM:ss"; // API doesn't accept zzz but does accept a Z on the end.
        public OctopusService(ILogger<OctopusService> logger, IOptions<OctopusSettings> settings) : base(logger, settings) { }

        public override bool ValidateSettings()
        {
            bool ok = true;

            if (string.IsNullOrEmpty(Settings.ApiKey))
            {
                Logger.LogError("Setting Octopus.ApiKey is required.");
                ok = false;
            }

            return ok;
        }

        public async Task<IEnumerable<string>> GetElectricityMeterPoints()
        {
            //if (!string.IsNullOrEmpty(Settings.Mapn))
            //{
            //    return Settings.Mapn.Split(",");
            //}

            string account = await GetAccount();
            using (JsonDocument j = JsonDocument.Parse(account))
            {
                // And I thought jq was bad... Thanks MS.
                var a = j.RootElement.GetArray("properties").ToList();
                var b = a.GetArray("electricity_meter_points").ToList();
                var c = b.GetPropertValueAsString("mpan").ToList();
                return c.ToList();
            }
        }

        public async Task<IEnumerable<string>> GetElectricityMeters(string mpan)
        {
            string account = await GetAccount();
            using (JsonDocument j = JsonDocument.Parse(account))
            {
                // And I thought jq was bad... Thanks MS.
                return j.RootElement
                    .GetArray("properties")
                    .GetArrayWhere("electricity_meter_points", "mpan", mpan)
                    .GetArray("meters")
                    .GetPropertValueAsString("serial_number")
                    .ToList();
            }
        }

        public async Task<IEnumerable<TariffCode>> GetElectricityTariffs()
        {
            string account = await GetAccount();
            using (JsonDocument j = JsonDocument.Parse(account))
            {
                // And I thought jq was bad... Thanks MS.
                var a = j.RootElement.GetArray("properties").ToList();
                var b = a.GetArray("electricity_meter_points").ToList();
                var c = b.GetArray("agreements").ToList();
                List<TariffCode> meterTariffs = c.Select(z =>
                {
                    var p = z.EnumerateObject();
                    string tariffCode = p.Single(z => z.Name == "tariff_code").Value.GetString()!;
                    bool tariffIsAdditional = Settings.AdditionalTariffs.ToLower().Contains(tariffCode.ToLower());

                    return new TariffCode()
                    {
                        Code = tariffCode,
                        ValidFrom = tariffIsAdditional ? DateTime.UtcNow.AddYears(-1) : p.Single(z => z.Name == "valid_from").GetDate()!.Value.ToUniversalTime(),
                        // Do not limit additional tariffs to a date range.
                        ValidTo = tariffIsAdditional ? null : p.Single(z => z.Name == "valid_to").GetDate()?.ToUniversalTime(),
                        TariffType = tariffCode.Contains("OUTGOING") || tariffCode.Contains("EXPORT") ? TariffType.Export : TariffType.Import
                    };
                }).ToList();

                List<string> additionalTariffs = Settings.AdditionalTariffs.Split(',')
                    .Distinct()
                    .Where(z => !meterTariffs.Any(y => y.Code.ToLower() == z.ToLower()))
                    .ToList();

                return meterTariffs.Union(additionalTariffs.Select(z => new TariffCode()
                {
                    Code = z,
                    ValidFrom = DateTime.UtcNow.AddYears(-1),
                    ValidTo = null
                }));
            }
        }

        public async Task<TariffCode> GetElectricityCurrentTariff(TariffType tariffType, DateTime at)
        {
            return (await GetElectricityTariffs())
                    .Where(z => z.ValidFrom >= at && (!z.ValidTo.HasValue || z.ValidTo >= at) && z.TariffType == tariffType)
                    .Single();
        }

        public async Task<IEnumerable<Price>> GetElectricityPrices(string product, string tariff, DateTime from, DateTime to)
        {
            List<Price> prices = new List<Price>();
            if (to <= from) { return prices; }
            using (HttpClient httpClient = GetHttpClient())
            {
                HttpResponseMessage response = await httpClient.GetAsync($"/v1/products/{product}/electricity-tariffs/{tariff}/standard-unit-rates/?period_from={from.ToString(DateFormat)}Z&period_to={to.ToString(DateFormat)}Z");
                response.EnsureSuccessStatusCode();
                string? json = await response.Content.ReadAsStringAsync();
                while (json != null)
                {
                    string? next = null;
                    using (JsonDocument j = JsonDocument.Parse(json))
                    {
                        prices.AddRange(
                            j.RootElement.GetArray("results").Select(z =>
                            {
                                var p = z.EnumerateObject();
                                return new Price()
                                {
                                    Pence = p.Single(z => z.Name == "value_inc_vat").Value.GetDecimal(),
                                    ValidFrom = p.Single(z => z.Name == "valid_from").GetDate()!.Value.ToUniversalTime(),
                                    ValidTo = p.Single(z => z.Name == "valid_to").GetDate()?.ToUniversalTime()
                                };


                            })
                        );
                        next = j.RootElement.EnumerateObject().Single(z => z.Name == "next").Value.GetString();
                    }
                    json = await GetNextPage(next);
                }
            }
            return prices;
        }


        public async Task<IEnumerable<MeterReading>> GetElectricityMeterReadings(string mpan, string serialNumber, DateTime from, DateTime to)
        {
            List<MeterReading> consumption = new List<MeterReading>();
            using (HttpClient httpClient = GetHttpClient())
            {
                // Fucking slash.
                HttpResponseMessage response = await httpClient.GetAsync($"/v1/electricity-meter-points/{mpan}/meters/{serialNumber}/consumption/?period_from={from.ToString(DateFormat)}Z&period_to={to.ToString(DateFormat)}Z");
                response.EnsureSuccessStatusCode();
                string? json = await response.Content.ReadAsStringAsync();
                while (json != null)
                {
                    string? next = null;
                    using (JsonDocument j = JsonDocument.Parse(json))
                    {
                        consumption.AddRange(
                            j.RootElement.GetArray("results").Select(z =>
                            {
                                var p = z.EnumerateObject();
                                try
                                {
                                    return new MeterReading()
                                    {
                                        IntervalStart = p.Single(z => z.Name == "interval_start").GetDate()!.Value.ToUniversalTime(),
                                        IntervalEnd = p.Single(z => z.Name == "interval_end").GetDate()!.Value.ToUniversalTime(),
                                        Consumption = p.Single(z => z.Name == "consumption").Value.GetDecimal(),
                                    };
                                }
                                catch
                                {
                                    return null;
                                }
                            })
                            .Where(z => z != null)
                            .Cast<MeterReading>() // From MeterReading?
                        );
                        next = j.RootElement.EnumerateObject().Single(z => z.Name == "next").Value.GetString();
                    }
                    json = await GetNextPage(next);
                }
            }
            return consumption;
        }

        public async Task<IEnumerable<string>> GetGasMeterPoints()
        {
            string account = await GetAccount();
            using (JsonDocument j = JsonDocument.Parse(account))
            {
                // And I thought jq was bad... Thanks MS.
                return j.RootElement.GetArray("properties").ToList()
                .GetArray("gas_meter_points").ToList()
                .GetPropertValueAsString("mprn").ToList()
                .ToList();
            }
        }

        public async Task<IEnumerable<string>> GetGasMeters(string mprn)
        {
            string account = await GetAccount();
            using (JsonDocument j = JsonDocument.Parse(account))
            {
                return j.RootElement
                    .GetArray("properties")
                    .GetArrayWhere("gas_meter_points", "mprn", mprn)
                    .GetArray("meters")
                    .GetPropertValueAsString("serial_number")
                    .ToList();
            }
        }

        public async Task<IEnumerable<TariffCode>> GetGasTariffs()
        {
            string account = await GetAccount();
            using (JsonDocument j = JsonDocument.Parse(account))
            {
                // And I thought jq was bad... Thanks MS.
                var a = j.RootElement.GetArray("properties").ToList();
                var b = a.GetArray("gas_meter_points").ToList();
                var c = b.GetArray("agreements").ToList();
                return c.Select(z =>
                {
                    JsonElement.ObjectEnumerator p = z.EnumerateObject();
                    return new TariffCode()
                    {
                        Code = p.Single(z => z.Name == "tariff_code").Value.GetString()!,
                        ValidFrom = p.Single(z => z.Name == "valid_from").GetDate()!.Value.ToUniversalTime(),
                        ValidTo = p.Single(z => z.Name == "valid_to").GetDate()?.ToUniversalTime()
                    };
                }).ToList();
            }
        }


        public async Task<IEnumerable<Price>> GetGasPrices(string product, string tariff, DateTime from, DateTime to)
        {
            List<Price> prices = new List<Price>();
            if (to <= from) { return prices; }
            using (HttpClient httpClient = GetHttpClient())
            {
                HttpResponseMessage response = await httpClient.GetAsync($"/v1/products/{product}/gas-tariffs/{tariff}/standard-unit-rates?period_from={from.ToString(DateFormat)}&period_to={to.ToString(DateFormat)}");
                response.EnsureSuccessStatusCode();
                string? json = await response.Content.ReadAsStringAsync();
                while (json != null)
                {
                    string? next = null;
                    using (JsonDocument j = JsonDocument.Parse(json))
                    {
                        prices.AddRange(
                            j.RootElement.GetArray("results").Select(z =>
                            {
                                JsonElement.ObjectEnumerator p = z.EnumerateObject();
                                return new Price()
                                {
                                    Pence = p.Single(z => z.Name == "value_inc_vat").Value.GetDecimal(),
                                    ValidFrom = p.Single(z => z.Name == "valid_from").GetDate()!.Value.ToUniversalTime(),
                                    ValidTo = p.Single(z => z.Name == "valid_to").GetDate()?.ToUniversalTime()
                                };
                            })
                        );
                        next = j.RootElement.EnumerateObject().Single(z => z.Name == "next").Value.GetString();
                    }
                    json = await GetNextPage(next);
                }
            }
            return prices;
        }

        public async Task<IEnumerable<MeterReading>> GetGasMeterReadings(string mprn, string serialNumber, DateTime from, DateTime to)
        {
            List<MeterReading> consumption = new List<MeterReading>();
            using (HttpClient httpClient = GetHttpClient())
            {
                HttpResponseMessage response = await httpClient.GetAsync($"/v1/gas-meter-points/{mprn}/meters/{serialNumber}/consumption/?period_from={from.ToString(DateFormat)}&period_to={to.ToString(DateFormat)}");
                response.EnsureSuccessStatusCode();
                string? json = await response.Content.ReadAsStringAsync();
                while (json != null)
                {
                    string? next = null;
                    using (JsonDocument j = JsonDocument.Parse(json))
                    {
                        consumption.AddRange(
                            j.RootElement.GetArray("results").Select(z =>
                            {
                                var p = z.EnumerateObject();
                                return new MeterReading()
                                {
                                    IntervalStart = p.Single(z => z.Name == "interval_start").GetDate()!.Value.ToUniversalTime(),
                                    IntervalEnd = p.Single(z => z.Name == "interval_end").GetDate()!.Value.ToUniversalTime(),
                                    Consumption = p.Single(z => z.Name == "consumption").Value.GetDecimal(),
                                };
                            })
                        );
                        next = j.RootElement.EnumerateObject().Single(z => z.Name == "next").Value.GetString();
                    }
                    json = await GetNextPage(next);
                }
            }
            return consumption;
        }

        private HttpClient GetHttpClient()
        {
            // By default the handler has AllowAutoRediret = true but it still fucks up.
            HttpClient client = new HttpClient()
            {
                BaseAddress = new Uri(Settings.BaseAddress)
            };
            client.DefaultRequestHeaders.Add("Authorization", "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes($"{Settings.ApiKey}:")));
            client.DefaultRequestHeaders.Add("User-Agent", "cunt");
            return client;
        }

        private async Task<string> GetAccount()
        {
            using (HttpClient httpClient = GetHttpClient())
            {
                HttpResponseMessage response = await httpClient.GetAsync($"/v1/accounts/{Settings.AccountNumber}/");
                // Omitting the trailing slash results in a 401.
                // However, wget gets a 301 which it follows.
                response.EnsureSuccessStatusCode();
                string json = await response.Content.ReadAsStringAsync();
                return json;
            }
            return await Task.FromResult((string)null);
        }

        private async Task<string?> GetNextPage(string? url)
        {
            if (string.IsNullOrEmpty(url)) { return null; }
            using (HttpClient httpClient = GetHttpClient())
            {
                HttpResponseMessage response = await httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();
                string json = await response.Content.ReadAsStringAsync();
                return json;
            }
        }
    }
}
