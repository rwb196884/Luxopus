using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Permissions;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Luxopus.Services
{
    internal static class JsonQueryExtensions
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
    }

    internal class OctopusSettings : Settings
    {
        public string ApiKey { get; set; }
        public string BaseAddress { get; set; }
        public string AccountNumber { get; set; }

        public string Mapn { get; set; }
    }

    internal class TariffCode
    {
        public string Code { get; set; }
        public DateTime ValidFrom { get; set; }
        public DateTime ValidTo { get; set; }
    }

    internal class MeterReading
    {
        public DateTime Time { get; set; }
        public int Valie { get; set; }
    }

    internal class Price
    {
        public float Pence { get; set; }
        public DateTime ValidFrom { get; set; }
        public DateTime ValidTo { get; set; }
    }

    internal interface IOctopusService
    {
        Task<IEnumerable<string>> GetElectricityMeterPoints();
        Task<IEnumerable<string>> GetElectricityMeters(string mapn);
        Task<IEnumerable<MeterReading>> GetElectricityMeterReadings(string mapn, string serialNumber, DateTime from, DateTime to);
        Task<IEnumerable<TariffCode>> GetElectricityTariffs();
        Task<IEnumerable<Price>> GetElectricityPrices(string product, string tariff, DateTime from, DateTime to);

        Task<IEnumerable<string>> GetGasMeterPoints();
        Task<IEnumerable<string>> GetGasMeters(string mprn);
        Task<IEnumerable<MeterReading>> GetGasMeterReadings(string mprn, string serialNumber, DateTime from, DateTime to);
        Task<IEnumerable<TariffCode>> GetGasTariffs();
        Task<IEnumerable<Price>> GetGasPrices(string product, string tariff, DateTime from, DateTime to);
    }

    internal class OctopusService : Service<OctopusSettings>, IOctopusService
    {
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
            if (!string.IsNullOrEmpty(Settings.Mapn))
            {
                return Settings.Mapn.Split(",");
            }

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
                return c.Select(z =>
                {
                    var p = z.EnumerateObject();
                    return new TariffCode()
                    {
                        Code = p.Single(z => z.Name == "tariff_code").Value.GetString(),
                        ValidFrom = DateTime.Parse(p.Single(z => z.Name == "valid_from").Value.GetString()),
                        ValidTo = DateTime.Parse(p.Single(z => z.Name == "valid_to").Value.GetString())
                    };
                }).ToList();
            }
        }

        public async Task<IEnumerable<Price>> GetElectricityPrices(string product, string tariff, DateTime from, DateTime to)
        {

        }


        public async Task<IEnumerable<MeterReading>> GetElectricityMeterReadings(string mapn, string serialNumber, DateTime from, DateTime to)
        {
            return await Task.FromResult(new List<MeterReading>());
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
                    var p = z.EnumerateObject();
                    return new TariffCode()
                    {
                        Code = p.Single(z => z.Name == "tariff_code").Value.GetString(),
                        ValidFrom = DateTime.Parse(p.Single(z => z.Name == "valid_from").Value.GetString()),
                        ValidTo = DateTime.Parse(p.Single(z => z.Name == "valid_to").Value.GetString())
                    };
                }).ToList();
            }
        }


        public async Task<IEnumerable<Price>> GetGasPrices(string product, string tariff, DateTime from, DateTime to)
        {

        }

        public async Task<IEnumerable<MeterReading>> GetGasMeterReadings(string mprn, string seriaNumber, DateTime from, DateTime to)
        {
            return await Task.FromResult(new List<MeterReading>());
        }

        private HttpClient GetHttpClient()
        {
            // By default the handler has AllowAutoRediret = true but it still fucks up.
            HttpClient client = new HttpClient()
            {
                BaseAddress = new Uri(Settings.BaseAddress)
            };
            client.DefaultRequestHeaders.Add("Authorization", "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes($"{Settings.ApiKey}:")));
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

        private void HandlePages() { }

    }
}
