using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Services
{

    public class SolcastSettings : Settings
    {
        public string BaseAddress { get; set; }
        public string ApiKey { get; set; }
        public string SiteId { get; set; }
    }

    public interface ISolcastService
    {
        Task<string> GetForecasts();
        Task<string> GetEstimatedActuals();
    }

    public class SolcastService : Service<SolcastSettings>, ISolcastService
    {
        private readonly IInfluxWriterService _InfluxWrite;
        public SolcastService(ILogger<SolcastService> logger, IOptions<SolcastSettings> settings, IInfluxWriterService influxWrite) : base(logger, settings)
        {
            _InfluxWrite = influxWrite;
        }

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

        public async Task<string> GetForecasts()
        {
            for (int i = 0; i < 2; i++)
            {
                try
                {
                    using (HttpClient httpClient = GetHttpClient())
                    {
                        HttpResponseMessage response = await httpClient.GetAsync($"/rooftop_sites/{Settings.SiteId}/forecasts?format=json");
                        response.EnsureSuccessStatusCode();
                        return await response.Content.ReadAsStringAsync();
                    }
                }
                catch (Exception e)
                {
                    if (i < 1)
                    {
                        Logger.LogError(e, "Solcast.GetForecasts failed. Trying again...");
                    }
                    else
                    {
                        throw;
                    }
                }
            }
            return null;
        }

        public async Task<string> GetEstimatedActuals()
        {
            for(int i=0; i<2; i++) {
                try
                {
                    using (HttpClient httpClient = GetHttpClient())
                    {
                        HttpResponseMessage response = await httpClient.GetAsync($"/rooftop_sites/{Settings.SiteId}/estimated_actuals?format=json");
                        response.EnsureSuccessStatusCode();
                        return await response.Content.ReadAsStringAsync();
                    }
                }
                catch (Exception e)
                {
                    if (i < 1)
                    {
                        Logger.LogError(e, "Solcast.GetForecasts failed. Trying again...");
                    }
                    else
                    {
                        throw;
                    }
                }
            }
            return null;
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
