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
        DateTime ToUtc(DateTime localTime);
        DateTime ToLocal(DateTime utcTime);

        Task<string> GetInverterRuntimeAsync();
        Task<string> GetInverterEnergyInfoAsync();

        //Task<int> GetBatteryLevelAsync();

        Task<Dictionary<string, string>> GetSettingsAsync();

        (bool enabled, DateTime start, DateTime stop, int batteryLimitPercent) GetChargeFromGrid(Dictionary<string, string> settings);
        (bool enabled, DateTime start, DateTime stop, int batteryLimitPercent) GetDischargeToGrid(Dictionary<string, string> settings);
        int GetBatteryChargeRate(Dictionary<string, string> settings);
        //int GetBatteryDischargeRate(Dictionary<string, string> settings);
        //int GetBatteryGridDischargeRate(Dictionary<string, string> settings);

        Task SetChargeFromGridStartAsync(DateTime start);
        Task SetChargeFromGridStopAsync(DateTime stop);
        Task SetChargeFromGridLevelAsync(int batteryLimitPercent);

        Task SetDischargeToGridStartAsync(DateTime start);
        Task SetDischargeToGridStopAsync(DateTime stop);
        Task SetDischargeToGridLevelAsync(int batteryLimitPercent);

        Task SetBatteryChargeRateAsync(int batteryChargeRatePercent);
        //Task SetBatteryDischargeRateAsync(int batteryDischargeRatePercent);
        //Task SetBatteryGridDischargeRateAsync(int batteryDischargeRatePercent);
    }

    public class LuxService : Service<LuxSettings>, ILuxService, IDisposable
    {
        private const string GetInverterRuntimePath = "/WManage/api/inverter/getInverterRuntime";
        private const string GetInverterEnergyInfoPath = "/WManage/api/inverter/getInverterEnergyInfo";
        const string UrlToWrite = "/WManage/web/maintain/remoteSet/write";
        const string UrlToWriteTime = "/WManage/web/maintain/remoteSet/writeTime";
        const string UrlToWriteFunction = "/WManage/web/maintain/remoteSet/functionControl";

        private string _InverterRuntimeCache;
        private DateTime _InverterRuntimeCacheDate;

        private readonly CookieContainer _CookieContainer;
        private readonly HttpClientHandler _Handler;
        private readonly HttpClient _Client;
        private bool disposedValue;

        public DateTime ToUtc(DateTime localTime)
        {
            DateTimeZone ntz = DateTimeZoneProviders.Tzdb[Settings.TimeZone];
            Offset o = ntz.GetUtcOffset(Instant.FromDateTimeOffset(localTime));
            DateTime u = localTime.AddTicks(-1 * o.Ticks);
            DateTime v = DateTime.SpecifyKind(u, DateTimeKind.Utc);
            return v;
        }

        public DateTime ToLocal(DateTime utcTime)
        {
            DateTimeZone ntz = DateTimeZoneProviders.Tzdb[Settings.TimeZone];
            Offset o = ntz.GetUtcOffset(Instant.FromDateTimeOffset(utcTime));
            DateTime u = utcTime.AddTicks(o.Ticks);
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
            HttpResponseMessage r = null;
            while (true)
            {
                r = await _Client.PostAsync(path, content);
                if (r.StatusCode == HttpStatusCode.Unauthorized)
                {
                    await LoginAsync();
                }
                else
                {
                    break;
                }
            }
            r.EnsureSuccessStatusCode();
            return r;
        }

        private async Task LoginAsync()
        {
            HttpResponseMessage result = await PostAsync("/WManage/web/login", new Dictionary<string, string>()
            {
                    {"account", Settings.Username },
                    { "password", Settings.Password }
            });
        }

        public async Task<string> GetInverterRuntimeAsync()
        {
            if (_InverterRuntimeCache != null && _InverterRuntimeCacheDate > DateTime.UtcNow.AddMinutes(-5))
            {
                return _InverterRuntimeCache;
            }

            HttpResponseMessage r = await PostAsync(GetInverterRuntimePath, new Dictionary<string, string>()
                {
                    { "serialNum", Settings.Station },
                });
            {
                string inverterRuntime = await r.Content.ReadAsStringAsync();
                _InverterRuntimeCache = inverterRuntime;
                _InverterRuntimeCacheDate = DateTime.UtcNow;
                return inverterRuntime;
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
                        if ((new string[] { "valueFrame", "success" }).Contains(p.Name)) { continue; }
                        if (settings.ContainsKey(p.Name))
                        {
                            string existingValue = settings[p.Name];
                            Logger.LogWarning($"Duplicate setting '{p.Name}'. Values: '{existingValue}', '{p.Value.ToString()}'.");
                        }
                        else
                        {
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
            int lim = int.Parse(settings["HOLD_AC_CHARGE_SOC_LIMIT"]);

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
            DateTime t = DateTime.Parse($"{relativeTo.ToString("yyyy-MM-dd")}T{hours.ToString("00")}:{minutes.ToString("00")}:00");
            t = DateTime.SpecifyKind(t, DateTimeKind.Local);
            DateTime u = ToUtc(t);
            return u < DateTime.UtcNow ? u.AddDays(1) : u; // The next time that the time will happen.
        }

        public (bool enabled, DateTime start, DateTime stop, int batteryLimitPercent) GetDischargeToGrid(Dictionary<string, string> settings)
        {
            bool enabled = settings["FUNC_FORCED_DISCHG_EN"].ToUpper() == "TRUE";
            int startH = int.Parse(settings["HOLD_FORCED_DISCHARGE_START_HOUR"]);
            int startM = int.Parse(settings["HOLD_FORCED_DISCHARGE_START_MINUTE"]);
            int endH = int.Parse(settings["HOLD_FORCED_DISCHARGE_END_HOUR"]);
            int endM = int.Parse(settings["HOLD_FORCED_DISCHARGE_END_MINUTE"]);
            int lim = int.Parse(settings["HOLD_FORCED_DISCHG_SOC_LIMIT"]);

            DateTime t = DateTime.Parse(settings["inverterRuntimeDeviceTime"]);
            t = DateTime.SpecifyKind(t, DateTimeKind.Local);

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

        public int GetBatteryGridDischargeRate(Dictionary<string, string> settings)
        {
            //return int.Parse(settings["HOLD_DISCHG_POWER_PERCENT_CMD"]);
            return int.Parse(settings["HOLD_FORCED_DISCHG_POWER_CMD"]);
        }
        public int GetBatteryDischargeRate(Dictionary<string, string> settings)
        {
            return int.Parse(settings["HOLD_DISCHG_POWER_PERCENT_CMD"]);
        }

        public async Task<int> GetBatteryLevelAsync()
        {
            using (JsonDocument j = JsonDocument.Parse(await GetInverterRuntimeAsync()))
            {
                return j.RootElement.EnumerateObject().Single(z => z.Name == "soc").Value.GetInt32();
            }
        }

        public async Task SetChargeFromGridStartAsync(DateTime start)
        {
            DateTime localStart = ToLocal(start);
            await PostAsync(UrlToWriteTime, GetTimeParams("HOLD_AC_CHARGE_START_TIME", localStart));
        }

        public async Task SetChargeFromGridStopAsync(DateTime stop)
        {
            DateTime localStop = ToLocal(stop);
            await PostAsync(UrlToWriteTime, GetTimeParams("HOLD_AC_CHARGE_END_TIME", localStop));
        }

        public async Task SetChargeFromGridLevelAsync(int batteryLimitPercent)
        {
            bool enable = batteryLimitPercent > 0 && batteryLimitPercent <= 100;
            await PostAsync(UrlToWriteFunction, GetFuncParams("FUNC_AC_CHARGE", enable));
            await PostAsync(UrlToWrite, GetHoldParams("HOLD_AC_CHARGE_SOC_LIMIT", batteryLimitPercent.ToString()));
        }

        public async Task SetDischargeToGridStartAsync(DateTime start)
        {
            DateTime localStart = ToLocal(start);
            await PostAsync(UrlToWriteTime, GetTimeParams("HOLD_FORCED_DISCHARGE_START_TIME", localStart));
        }

        public async Task SetDischargeToGridStopAsync(DateTime stop)
        {
            DateTime localStop = ToLocal(stop);
            await PostAsync(UrlToWriteTime, GetTimeParams("HOLD_FORCED_DISCHARGE_END_TIME", localStop));
        }

        public async Task SetDischargeToGridLevelAsync(int batteryLimitPercent)
        {
            bool enable = batteryLimitPercent > 0 && batteryLimitPercent < 100;
            await PostAsync(UrlToWriteFunction, GetFuncParams("FUNC_FORCED_DISCHG_EN", enable));
            if (enable)
            {
                await PostAsync(UrlToWrite, GetHoldParams("HOLD_FORCED_DISCHG_SOC_LIMIT", batteryLimitPercent.ToString()));
            }
        }

        public async Task SetBatteryChargeRateAsync(int batteryChargeRatePercent)
        {
            int rate = batteryChargeRatePercent < 0 || batteryChargeRatePercent > 100 ? 90 : batteryChargeRatePercent;
            await PostAsync(UrlToWrite, GetHoldParams("HOLD_CHARGE_POWER_PERCENT_CMD", rate.ToString()));
        }

        public async Task SetBatteryDischargeRateAsync(int batteryDischargeRatePercent)
        {
            int rate = batteryDischargeRatePercent < 0 || batteryDischargeRatePercent > 100 ? 90 : batteryDischargeRatePercent;
            await PostAsync(UrlToWrite, GetHoldParams("HOLD_DISCHG_POWER_PERCENT_CMD", rate.ToString()));
        }
        public async Task SetBatteryGridDischargeRateAsync(int batteryDischargeRatePercent)
        {
            int rate = batteryDischargeRatePercent < 0 || batteryDischargeRatePercent > 100 ? 90 : batteryDischargeRatePercent;
            await PostAsync(UrlToWrite, GetHoldParams("HOLD_FORCED_DISCHG_POWER_CMD", rate.ToString())); // !
        }

        private Dictionary<string, string> GetHoldParams(string holdParam, string valueText)
        {
            return GetParams(new Dictionary<string, string>()
            {
                { "holdParam", holdParam},
                { "valueText", valueText}
            });
        }
        private Dictionary<string, string> GetFuncParams(string funcParam, bool enable)
        {
            return GetParams(new Dictionary<string, string>()
            {
                { "functionParam", funcParam},
                { "enable", enable ? "true" : "false"}
            });
        }

        private Dictionary<string, string> GetTimeParams(string timeParam, DateTime t)
        {
            return GetParams(new Dictionary<string, string>()
            {
                { "timeParam", timeParam},
                { "hour", t.ToString("HH")},
                { "minute", t.ToString("mm")}
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
