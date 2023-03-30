using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Luxopus.Services
{

    internal class SolcastSettings : Settings
    {
        public string BaseAddress { get; set; }
        public string ApiKey { get; set; }
        public string SiteId { get; set; }
    }

    internal interface ISolcastService
    {
        async Task GetEstimatedActuals();
        async Task GetForecasts();
    }

    internal class SolcastService : Service<SolcastSettings>, ISolcastService
    {
        public SolcastService(ILogger<SolcastService> logger, IOptions<SolcastSettings> settings) : base(logger, settings) { }

        public override bool ValidateSettings()
        {
            bool ok = true;

            if (string.IsNullOrEmpty(Settings.BaseAddress))
            {
                Logger.LogError("Setting Solcast.BaseAddress is required.");
                ok = false;
            }
            if (string.IsNullOrEmpty(Settings.ApiKey))
            {
                Logger.LogError("Setting Solcast.ApiKey is required.");
                ok = false;
            }
            if (string.IsNullOrEmpty(Settings.SiteId))
            {
                Logger.LogError("Setting Solcast.SiteId is required.");
                ok = false;
            }

            return ok;
        }

        public async Task GetEstimatedActuals()
        {
            using (HttpClient httpClient = GetHttpClient())
            {
                HttpResponseMessage response = await httpClient.GetAsync($"/rooftop_sites/{Settings.SiteId}/estimated_actuals?format=json");
                response.EnsureSuccessStatusCode();
                string? json = await response.Content.ReadAsStringAsync();
                using (JsonDocument j = JsonDocument.Parse(json))
                {

                }
            }
        }
        public async Task GetForecasts()
        {
            using (HttpClient httpClient = GetHttpClient())
            {
                HttpResponseMessage response = await httpClient.GetAsync($"/rooftop_sites/{Settings.SiteId}/forecasts?format=json");
                response.EnsureSuccessStatusCode();
                string? json = await response.Content.ReadAsStringAsync();
                using (JsonDocument j = JsonDocument.Parse(json))
                {

                }
            }
        }

        private HttpClient GetHttpClient()
        {
            // By default the handler has AllowAutoRediret = true but it still fucks up.
            HttpClient client = new HttpClient()
            {
                BaseAddress = new Uri(Settings.BaseAddress)
            };
            client.DefaultRequestHeaders.Add("Authorization", "Bearer " + Settings.ApiKey);
            return client;
        }
    }
}
