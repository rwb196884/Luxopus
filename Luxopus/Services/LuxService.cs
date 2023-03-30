using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace Luxopus.Services
{
    internal class LuxSettings : Settings
    {
        public string Username { get; set; }
        public string Password { get; set; }
        public string Station { get; set; }
        public string BaseAddress { get; set; }
    }

    internal interface ILuxService
    {
        Task<string> GetInverterRuntimeAsync();
        Task<string> GetInverterEnergyInfoAsync();
    }

    internal class LuxService : Service<LuxSettings>, ILuxService, IDisposable
    {
        private const string GetInverterRuntimePath = "/WManage/api/inverter/getInverterRuntime";
        private const string GetInverterEnergyInfoPath = "/WManage/api/inverter/getInverterEnergyInfo";

        private readonly CookieContainer _CookieContainer;
        private readonly HttpClientHandler _Handler;
        private readonly HttpClient _Client;
        private bool disposedValue;

        public LuxService(ILogger<LuxService> logger, IOptions<LuxSettings> settings) : base(logger, settings)
        {
            _CookieContainer = new CookieContainer();
            _Handler = new HttpClientHandler() { CookieContainer = _CookieContainer };
            _Client = new HttpClient(_Handler) { BaseAddress = new Uri(Settings.BaseAddress) };
        }

        public override bool ValidateSettings()
        {
            bool ok = true;
            if (string.IsNullOrEmpty(Settings.Username))
            {
                Logger.LogError("Setting Lux.Token is required.");
                ok = false;
            }
            if (string.IsNullOrEmpty(Settings.Password))
            {
                Logger.LogError("Setting Lux.Password is required.");
                ok = false;
            }
            if (string.IsNullOrEmpty(Settings.Station))
            {
                Logger.LogError("Setting Lux.Station is required.");
                ok = false;
            }
            if (string.IsNullOrEmpty(Settings.BaseAddress))
            {
                Logger.LogError("Setting Lux.BaseAddress is required.");
                ok = false;
            }
            return ok;
        }

        private async Task<HttpResponseMessage> PostAsync(string path, KeyValuePair<string, string>[] formData)
        {
                FormUrlEncodedContent content = new FormUrlEncodedContent(formData);
                return await _Client.PostAsync(path, content);
        }

        private async Task LoginAsync()
        {
            HttpResponseMessage result = await PostAsync("/WManage/web/login", new[]
            {
                    new KeyValuePair<string, string>("account", Settings.Username),
                    new KeyValuePair<string, string>("password", Settings.Password),
                });
            result.EnsureSuccessStatusCode();
        }

        public async Task<string> GetInverterRuntimeAsync()
        {
            while (true)
            {
                HttpResponseMessage r = await PostAsync(GetInverterRuntimePath, new[]
                {
                    new KeyValuePair<string, string>("serialNum", Settings.Station),
                });

                if (r.StatusCode == HttpStatusCode.Unauthorized)
                {
                    await LoginAsync();
                }
                else
                {
                    return await r.Content.ReadAsStringAsync();
                }
            }
        }

        public async Task<string> GetInverterEnergyInfoAsync()
        {
            while (true)
            {
                HttpResponseMessage r = await PostAsync(GetInverterEnergyInfoPath, new[]
                {
                    new KeyValuePair<string, string>("serialNum", Settings.Station),
                });

                if (r.StatusCode == HttpStatusCode.Unauthorized)
                {
                    await LoginAsync();
                }
                else
                {
                    return await r.Content.ReadAsStringAsync();
                }
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects)
                    _Client.Dispose();
                    _Handler.Dispose();
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                disposedValue = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~LuxService()
        // {
        //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
