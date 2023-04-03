using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using static NodaTime.TimeZones.ZoneEqualityComparer;

namespace Rwb.Luxopus.Services
{
    public class LuxSettings : Settings
    {
        public string Username { get; set; }
        public string Password { get; set; }
        public string Station { get; set; }
        public string BaseAddress { get; set; }
        public string TimeZone { get; set; }
    }

    public interface ILuxService
    {
        Task<string> GetInverterRuntimeAsync();
        Task<string> GetInverterEnergyInfoAsync();

        //Task<int> GetBatteryLevelAsync();

        Task<Dictionary<string, string>> GetSettingsAsync();

        (bool enabled, DateTime start, DateTime stop, int batteryLimitPercent) GetChargeFromGrid(Dictionary<string, string> settings);
        (bool enabled, DateTime start, DateTime stop, int batteryLimitPercent) GetDishargeToGrid(Dictionary<string, string> settings);
        int GetBatteryChargeRate(Dictionary<string, string> settings);

        Task SetChargeFromGridAsync(DateTime start, DateTime stop, int batteryLimitPercent);
        Task SetDishargeToGridAsync(DateTime start, DateTime stop, int batteryLimitPercent);
        Task SetBatteryChargeRate(int batteryChargeRatePercent);
        Task ResetAsync();
    }

    public class LuxService : Service<LuxSettings>, ILuxService, IDisposable
    {
        private const string GetInverterRuntimePath = "/WManage/api/inverter/getInverterRuntime";
        private const string GetInverterEnergyInfoPath = "/WManage/api/inverter/getInverterEnergyInfo";
        const string UrlToWrite = "/WManage/web/maintain/remoteSet/write";
        const string UrlToWriteTime = "/WManage/web/maintain/remoteSet/writeTime";

        private string _InverterRuntimeCache;
        private DateTime _InverterRuntimeCacheDate;

        private readonly CookieContainer _CookieContainer;
        private readonly HttpClientHandler _Handler;
        private readonly HttpClient _Client;
        private bool disposedValue;

        private DateTime ToUtc(DateTime localTime)
        {
            DateTimeZone ntz = DateTimeZoneProviders.Tzdb[Settings.TimeZone];
            Offset o = ntz.GetUtcOffset(Instant.FromDateTimeOffset(localTime));
            DateTime u = localTime.AddTicks(-1 * o.Ticks);
            DateTime v = DateTime.SpecifyKind(u, DateTimeKind.Utc);
            return v;
        }

        private DateTime ToLocal(DateTime utcTime)
        {
            DateTimeZone ntz = DateTimeZoneProviders.Tzdb[Settings.TimeZone];
            Offset o = ntz.GetUtcOffset(Instant.FromDateTimeOffset(utcTime));
            DateTime u = utcTime.AddTicks( o.Ticks);
            DateTime v = DateTime.SpecifyKind(u, DateTimeKind.Local);
            return v;
        }

        public LuxService(ILogger<LuxService> logger, IOptions<LuxSettings> settings) : base(logger, settings)
        {
            _CookieContainer = new CookieContainer();
            _Handler = new HttpClientHandler() { CookieContainer = _CookieContainer };
            _Client = new HttpClient(_Handler) { BaseAddress = new Uri(Settings.BaseAddress) };
            _InverterRuntimeCache = null;
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

            try
            {
                DateTimeZone ntz = DateTimeZoneProviders.Tzdb[Settings.TimeZone];
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Setting Lux.TimeZone could not construct a DateTimeZone.");
                ok = false;
            }

            return ok;
        }

        private async Task<HttpResponseMessage> PostAsync(string path, IEnumerable<KeyValuePair<string, string>> formData)
        {
            FormUrlEncodedContent content = new FormUrlEncodedContent(formData);
            return await _Client.PostAsync(path, content);
        }

        private async Task LoginAsync()
        {
            HttpResponseMessage result = await PostAsync("/WManage/web/login", new Dictionary<string, string>()
            {
                    {"account", Settings.Username },
                    { "password", Settings.Password }
            });
            result.EnsureSuccessStatusCode();
        }

        public async Task<string> GetInverterRuntimeAsync()
        {
            if (_InverterRuntimeCache != null && _InverterRuntimeCacheDate > DateTime.UtcNow.AddMinutes(-5))
            {
                return _InverterRuntimeCache;
            }

            while (true)
            {
                HttpResponseMessage r = await PostAsync(GetInverterRuntimePath, new Dictionary<string, string>()
                {
                    { "serialNum", Settings.Station },
                });

                if (r.StatusCode == HttpStatusCode.Unauthorized)
                {
                    await LoginAsync();
                }
                else
                {
                    string inverterRuntime = await r.Content.ReadAsStringAsync();
                    _InverterRuntimeCache = inverterRuntime;
                    _InverterRuntimeCacheDate = DateTime.UtcNow;
                    return inverterRuntime;
                }
            }
        }

        public async Task<string> GetInverterEnergyInfoAsync()
        {
            while (true)
            {
                HttpResponseMessage r = await PostAsync(GetInverterEnergyInfoPath, new Dictionary<string, string>()
                {
                    { "serialNum", Settings.Station },
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

        public async Task<Dictionary<string, string>> GetSettingsAsync()
        {
            Dictionary<string, string> settings = new Dictionary<string, string>();
            foreach (int i in new int[] { 0, 40, 80, 120, 160 })
            {
                string json = null;
                while (true)
                {
                    HttpResponseMessage r = await PostAsync("/WManage/web/maintain/remoteRead/read", new Dictionary<string, string>()
                    {
                            {"inverterSn", Settings.Station },
                            { "startRegister", i.ToString() },
                            { "pointNumber", "40" }
                    });
                    if (r.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        await LoginAsync();
                    }
                    else
                    {
                        json = await r.Content.ReadAsStringAsync();
                        break;
                    }
                }
                using (JsonDocument j = JsonDocument.Parse(json))
                {
                    foreach (JsonProperty p in j.RootElement.EnumerateObject())
                    {
                        if (p.Name == "valueFrame") { continue; }
                        if (p.Name == "success") { continue; }
                        if (settings.ContainsKey(p.Name))
                        {
                        }
                        switch (p.Value.ValueKind)
                        {
                            case JsonValueKind.String:
                                settings.Add(p.Name, p.Value.GetString());
                                break;
                            case JsonValueKind.Number:
                                settings.Add(p.Name, p.Value.GetInt32().ToString());
                                break;
                            case JsonValueKind.True:
                            case JsonValueKind.False:
                                settings.Add(p.Name, p.Value.GetBoolean().ToString());
                                break;
                            case JsonValueKind.Null:
                            case JsonValueKind.Undefined:
                                break;
                            default:
                                throw new NotImplementedException($"JsonValueKind {p.Value.ValueKind}");
                        }
                    }
                }
            }
            return settings;
        }

        public (bool enabled, DateTime start, DateTime stop, int batteryLimitPercent) GetChargeFromGrid(Dictionary<string, string> settings)
        {
            bool enabled = settings["FUNC_AC_CHARGE"].ToUpper() == "TRUE";
            int startH = int.Parse(settings["HOLD_AC_CHARGE_START_HOUR"]);
            int startM = int.Parse(settings["HOLD_AC_CHARGE_START_MINUTE"]);
            int endH = int.Parse(settings["HOLD_AC_CHARGE_END_HOUR"]);
            int endM = int.Parse(settings["HOLD_AC_CHARGE_END_MINUTE"]);
            int lim = int.Parse(settings["HOLD_AC_CHARGE_END_BATTERY_SOC"]);

            DateTime t = DateTime.Parse(settings["inverterRuntimeDeviceTime"]);

            return (
                enabled,
                GetDate(startH, startM, t),
                GetDate(endH, endM, t),
                lim
                );
        }

        private DateTime GetDate(int hours, int minutes, DateTime relativeTo)
        {
            DateTime t = DateTime.Parse($"{relativeTo.ToString("yyyy-MM-dd")}T{hours.ToString("00")}:{minutes.ToString("00")}:00Z");
            return ToUtc(t);
        }

        public (bool enabled, DateTime start, DateTime stop, int batteryLimitPercent) GetDishargeToGrid(Dictionary<string, string> settings)
        {
            bool enabled = settings["FUNC_FORCED_DISCHG_EN"] == "TRUE";
            int startH = int.Parse(settings["HOLD_FORCED_DISCHARGE_START_HOUR"]);
            int startM = int.Parse(settings["HOLD_FORCED_DISCHARGE_START_MINUTE"]);
            int endH = int.Parse(settings["HOLD_FORCED_DISCHARGE_END_HOUR"]);
            int endM = int.Parse(settings["HOLD_FORCED_DISCHARGE_END_MINUTE"]);
            int lim = int.Parse(settings["HOLD_FORCED_DISCHG_SOC_LIMIT"]);

            DateTime t = DateTime.Parse(settings["inverterRuntimeDeviceTime"]);

            return (
                enabled,
                GetDate(startH, startM, t),
                GetDate(endH, endM, t),
                lim
                );
        }

        public int GetBatteryChargeRate(Dictionary<string, string> settings)
        {
            return int.Parse(settings["HOLD_CHARGE_POWER_PERCENT_CMD"]);
        }


        public async Task<int> GetBatteryLevelAsync()
        {
            using (JsonDocument j = JsonDocument.Parse(await GetInverterRuntimeAsync()))
            {
                return j.RootElement.EnumerateObject().Single(z => z.Name == "soc").Value.GetInt32();
            }
        }

        public async Task SetChargeFromGridAsync(DateTime start, DateTime stop, int batteryLimitPercent)
        {
            bool enable = start != stop && batteryLimitPercent >= 0 && batteryLimitPercent <= 100;

            DateTime localStart = ToLocal(start);
            DateTime localStop = ToLocal(stop);

            await PostAsync(UrlToWrite, GetEnableParams("FUNC_AC_CHARGE", enable));
            await PostAsync(UrlToWriteTime, GetTimeParams("HOLD_AC_CHARGE_START_TIME", localStart));
            await PostAsync(UrlToWriteTime, GetTimeParams("HOLD_AC_CHARGE_END_TIME", localStop));
            await PostAsync(UrlToWrite, GetHoldParams("HOLD_AC_CHARGE_SOC_LIMIT", batteryLimitPercent.ToString()));
        }

        public async Task SetDishargeToGridAsync(DateTime start, DateTime stop, int batteryLimitPercent)
        {
            bool enable = start != stop && batteryLimitPercent >= 0 && batteryLimitPercent <= 100;

            DateTime localStart = ToLocal(start);
            DateTime localStop = ToLocal(stop);

            await PostAsync(UrlToWrite, GetEnableParams("FUNC_FORCED_DISCHG_EN", enable));
            await PostAsync(UrlToWriteTime, GetTimeParams("HOLD_FORCED_DISCHARGE_START_TIME", localStart));
            await PostAsync(UrlToWriteTime, GetTimeParams("HOLD_FORCED_DISCHARGE_END_TIME", localStop));
            await PostAsync(UrlToWrite, GetHoldParams("HOLD_FORCED_DISCHG_SOC_LIMIT", batteryLimitPercent.ToString()));
        }

        public async Task SetBatteryChargeRate(int batteryChargeRatePercent)
        {
            int rate = batteryChargeRatePercent < 0 || batteryChargeRatePercent > 100 ? 90 : batteryChargeRatePercent;
            await PostAsync(UrlToWrite, GetHoldParams("HOLD_CHARGE_POWER_PERCENT_CMD", rate.ToString()));
        }

        private Dictionary<string, string> GetHoldParams(string holdParam, string valueText)
        {
            return GetParams(new Dictionary<string, string>()
            {
                { "holdParam", holdParam},
                { "valueText", valueText}
            });
        }
        private Dictionary<string, string> GetEnableParams(string holdParam, bool enable)
        {
            return GetParams(new Dictionary<string, string>()
            {
                { "holdParam", holdParam},
                { "enable", enable ? "true" : "false"}
            });
        }

        private Dictionary<string, string> GetTimeParams(string timeParam, DateTime t)
        {
            return GetParams(new Dictionary<string, string>()
            {
                { "holdParam", timeParam},
                { "hour", t.ToString("mm")},
                { "minute", t.ToString("HH")}
            });
        }


        private Dictionary<string, string> GetParams(Dictionary<string, string> others)
        {
            Dictionary<string, string> parameters = new Dictionary<string, string>()
            {
                {"inverterSn", Settings.Station },
                { "clientType", "WEB"},
                { "remoteSetType", "NORMAL"}
            };

            foreach (KeyValuePair<string, string> o in others)
            {
                parameters.Add(o.Key, o.Value);
            }

            return parameters;
        }

        public async Task ResetAsync()
        {
            Dictionary<string, string> settings = await GetSettingsAsync();
            (bool inEnabled, DateTime inStart, DateTime inStop, int inBatteryLimitPercent) = GetChargeFromGrid(settings);
            (bool outEnabled, DateTime outStart, DateTime outStop, int outBatteryLimitPercent) = GetDishargeToGrid(settings);
            int battChargeRate = GetBatteryChargeRate(settings);
            int battLevel = await GetBatteryLevelAsync();

            DateTime t0 = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, 0, 0, 0);
            if (outEnabled)
            {
                await SetDishargeToGridAsync(t0, t0, -1);
                Logger.LogWarning("Discharge to grid was turned on.");
            }

            if (inEnabled)
            {
                await SetChargeFromGridAsync(t0, t0, -1);
                Logger.LogWarning("Charge from grid was turned off.");
            }

            if (battLevel > 96)
            {
                if (battChargeRate != 1)
                {
                    await SetBatteryChargeRate(1);
                    Logger.LogWarning($"Battery charge rate was reset to 1% (level is {battLevel}) was {battChargeRate}.");
                }
            }
            else
            {
                if (battChargeRate != 99)
                {
                    await SetBatteryChargeRate(99);
                    Logger.LogWarning($"Battery charge rate was reset to 99% (level is {battLevel}) was {battChargeRate}.");
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
