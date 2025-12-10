using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Rwb.Luxopus.Jobs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
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

        int GetBatteryChargeRate(Dictionary<string, string> settings);
        Task SetBatteryChargeRateAsync(int batteryChargeRatePercent);

        //int GetBatteryDischargeRate(Dictionary<string, string> settings);
        //Task SetBatteryDischargeRateAsync(int batteryDischargeRatePercent);

        bool GetChargeLast(Dictionary<string, string> settings);
        Task SetChargeLastAsync(bool enabled);

        LuxAction GetChargeFromGrid(Dictionary<string, string> settings);
        LuxAction GetDischargeToGrid(Dictionary<string, string> settings);

        Task<bool> SetChargeFromGrid(LuxAction current, LuxAction required);
        Task<bool> SetDischargeToGrid(LuxAction current, LuxAction required);

        /// <summary>
        /// Batteyr calibration info: enabled, days since last calibration, calibration period (days).
        /// </summary>
        /// <param name="settings"></param>
        /// <returns></returns>
        (bool, int, int) GetBatteryCalibration(Dictionary<string, string> settings);

        int KwhToBatt(int kWh);
        int BattToKwh(int batt);

        Task<(double today, double tomorrow)> Forecast();
    }

    public class LuxAction
    {
        public bool Enable { get; set; }
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public int Limit { get; set; }
        public int Rate { get; set; }

        public override string ToString()
        {
            return $"{(Enable ? "" : "OFF ")}{Start:HH:mm} to {End:HH:mm} limit {Limit}% rate {Rate}%.";
        }

        public LuxAction Clone()
        {
            return new LuxAction()
            {
                Enable = Enable,
                Start = Start,
                End = End,
                Limit = Limit,
                Rate = Rate
            };
        }

        public static LuxAction? NextCharge(Plan plan, LuxAction current)
        {
            LuxAction a = current.Clone();

            a.Enable = plan.Plans.Any(z => Plan.ChargeFromGridCondition(z));
            if (plan != null && a.Enable)
            {
                PeriodPlan currentPeriod = plan!.Current!;
                PeriodPlan? runFirst = plan.Plans.Where(z => z.Start >= currentPeriod.Start).OrderBy(z => z.Start).FirstOrDefault(z => Plan.ChargeFromGridCondition(z));
                if(runFirst == null) { return null; }
                a.Start = runFirst.Start;
                a.Limit = runFirst.Action!.ChargeFromGrid;

                (IEnumerable<PeriodPlan> run, PeriodPlan? next) = plan.GetNextRun(runFirst, Plan.ChargeFromGridCondition);
                a.End = (next?.Start ?? run.Last().Start.AddMinutes(30));

                // If we're charging now and started already then no change is needed.
                if (Plan.ChargeFromGridCondition(currentPeriod) && a.Start > currentPeriod.Start)
                {
                    a.Start = currentPeriod.Start;
                }

                a.Rate = a.Limit > 50 ? 90 : 40;
            }
            return a;
        }

        public static LuxAction? NextDisharge(Plan plan, LuxAction current)
        {
            LuxAction a = current.Clone();

            a.Enable = plan.Plans.Any(z => Plan.DischargeToGridCondition(z));
            if (plan != null && a.Enable)
            {
                PeriodPlan currentPeriod = plan!.Current!;
                PeriodPlan? runFirst = plan.Plans.Where(z => z.Start >= currentPeriod.Start).OrderBy(z => z.Start).FirstOrDefault(z => Plan.DischargeToGridCondition(z));
                if (runFirst == null) { return null; }
                a.Start = runFirst.Start;
                a.Limit = runFirst.Action!.DischargeToGrid;

                (IEnumerable<PeriodPlan> run, PeriodPlan? next) = plan.GetNextRun(runFirst, Plan.DischargeToGridCondition);
                a.End = (next?.Start ?? run.Last().Start.AddMinutes(30));

                // If we're charging now and started already then no change is needed.
                if (Plan.DischargeToGridCondition(currentPeriod) && a.Start > currentPeriod.Start)
                {
                    a.Start = currentPeriod.Start;
                }

                a.Rate = a.Limit < 50 ? 90 : 40;
            }
            return a;
        }
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

        private readonly BatterySettings _BatterySettings;

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

        public LuxService(ILogger<LuxService> logger, IOptions<LuxSettings> settings, IOptions<BatterySettings> batterySettings) : base(logger, settings)
        {
            _CookieContainer = new CookieContainer();
            _Handler = new HttpClientHandler() { CookieContainer = _CookieContainer };
            _Handler.ServerCertificateCustomValidationCallback = (message, cert, chain, sslPolicyErrors) =>
            {
                if (sslPolicyErrors == SslPolicyErrors.None)
                {
                    return true;   //Is valid
                }

                Logger.LogError($"Ignoring invlaid SSL certificate for {cert.Subject}");
                return true;
            };
            _Client = new HttpClient(_Handler)
            {
                BaseAddress = new Uri(Settings.BaseAddress),
                Timeout = TimeSpan.FromSeconds(15)
            };
            _InverterRuntimeCache = null;

            _BatterySettings = batterySettings.Value;
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
            int tries = 0;
            while (true)
            {
                try
                {
                    tries++;
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
                catch (Exception e)
                {
                    if (!e.Message.Contains("Timeout") || tries >= 8)
                    {
                        throw;
                    }
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

        //public async Task<int> GetBatteryLevelAsync()
        //{
        //    using (JsonDocument j = JsonDocument.Parse(await GetInverterRuntimeAsync()))
        //    {
        //        return j.RootElement.EnumerateObject().Single(z => z.Name == "soc").Value.GetInt32();
        //    }
        //}

        public async Task<Dictionary<string, string>> GetSettingsAsync()
        {
            Dictionary<string, string> settings = new Dictionary<string, string>();
            //foreach (int i in new int[] { 0, 40, 80, 120, 160 })
            foreach (int i in new int[] { 0, 127 })
            {
                string json = null;
                while (true)
                {
                    HttpResponseMessage r = await PostAsync("/WManage/web/maintain/remoteRead/read", new Dictionary<string, string>()
                    {
                            {"inverterSn", Settings.Station },
                            { "startRegister", i.ToString() },
                            //{ "pointNumber", "40" }
                            { "pointNumber", "127" }
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
                        if ((new string[] { "valueFrame", "success", "pointNumber", "startRegister", "inverterSn" }).Contains(p.Name)) { continue; }
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
                                    settings.Add(p.Name, p.Value.GetString() ?? string.Empty);
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

        private DateTime GetDate(int hours, int minutes, DateTime relativeTo)
        {
            DateTime t = DateTime.Parse($"{relativeTo.ToString("yyyy-MM-dd")}T{hours.ToString("00")}:{minutes.ToString("00")}:00");
            t = DateTime.SpecifyKind(t, DateTimeKind.Local);
            DateTime u = ToUtc(t);
            return u < DateTime.UtcNow ? u.AddDays(1) : u; // The next time that the time will happen.
        }

        public int GetBatteryChargeRate(Dictionary<string, string> settings)
        {
            return int.Parse(settings["HOLD_CHARGE_POWER_PERCENT_CMD"]);
        }

        public async Task SetBatteryChargeRateAsync(int batteryChargeRatePercent)
        {
            int rate = batteryChargeRatePercent < 0 || batteryChargeRatePercent > 100 ? 90 : batteryChargeRatePercent;
            await PostAsync(UrlToWrite, GetHoldParams("HOLD_CHARGE_POWER_PERCENT_CMD", rate.ToString()));
        }

        //public int GetBatteryDischargeRate(Dictionary<string, string> settings)
        //{
        //    return int.Parse(settings["HOLD_DISCHG_POWER_PERCENT_CMD"]);
        //}

        //public async Task SetBatteryDischargeRateAsync(int batteryDischargeRatePercent)
        //{
        //    int rate = batteryDischargeRatePercent < 0 || batteryDischargeRatePercent > 100 ? 90 : batteryDischargeRatePercent;
        //    await PostAsync(UrlToWrite, GetHoldParams("HOLD_DISCHG_POWER_PERCENT_CMD", rate.ToString())); // !
        //}

        public bool GetChargeLast(Dictionary<string, string> settings)
        {
            return bool.Parse(settings["FUNC_CHARGE_LAST"]);
        }

        public async Task SetChargeLastAsync(bool enabled)
        {
            await PostAsync(UrlToWriteFunction, GetFuncParams("FUNC_CHARGE_LAST", enabled));
        }

        public LuxAction GetChargeFromGrid(Dictionary<string, string> settings)
        {
            bool enabled = settings["FUNC_AC_CHARGE"].ToUpper() == "TRUE";
            int startH = int.Parse(settings["HOLD_AC_CHARGE_START_HOUR"]);
            int startM = int.Parse(settings["HOLD_AC_CHARGE_START_MINUTE"]);
            int endH = int.Parse(settings["HOLD_AC_CHARGE_END_HOUR"]);
            int endM = int.Parse(settings["HOLD_AC_CHARGE_END_MINUTE"]);
            int lim = int.Parse(settings["HOLD_AC_CHARGE_SOC_LIMIT"]);

            DateTime t = DateTime.Parse(settings["inverterRuntimeDeviceTime"]);

            return new LuxAction()
            {
                Enable = enabled,
                Start = GetDate(startH, startM, t),
                End = GetDate(endH, endM, t),
                Limit = lim,
                Rate = int.Parse(settings["HOLD_AC_CHARGE_POWER_CMD"])
            };
        }

        public async Task<bool> SetChargeFromGrid(LuxAction current, LuxAction required)
        {
            bool changes = false;

            if (current.Enable != required.Enable)
            {
                await PostAsync(UrlToWriteFunction, GetFuncParams("FUNC_AC_CHARGE", required.Enable));
                changes = true;
            }

            if (current.Start.TimeOfDay != required.Start.TimeOfDay)
            {
                DateTime localStart = ToLocal(required.Start);
                await PostAsync(UrlToWriteTime, GetTimeParams("HOLD_AC_CHARGE_START_TIME", localStart));
                changes = true;
            }

            if (current.End.TimeOfDay != required.End.TimeOfDay)
            {
                DateTime localStop = ToLocal(required.End);
                await PostAsync(UrlToWriteTime, GetTimeParams("HOLD_AC_CHARGE_END_TIME", localStop));
                changes = true;
            }

            if (current.Limit != required.Limit)
            {
                await PostAsync(UrlToWrite, GetHoldParams("HOLD_AC_CHARGE_SOC_LIMIT", required.Limit.ToString()));
                changes = true;
            }

            if (current.Rate != required.Rate)
            {
                int rate = required.Rate < 0 || required.Rate > 100 ? 90 : required.Rate;
                await PostAsync(UrlToWrite, GetHoldParams("HOLD_AC_CHARGE_POWER_CMD", rate.ToString()));
                changes = true;
            }

            return changes;
        }

        public LuxAction GetDischargeToGrid(Dictionary<string, string> settings)
        {
            bool enabled = settings["FUNC_FORCED_DISCHG_EN"].ToUpper() == "TRUE";
            int startH = int.Parse(settings["HOLD_FORCED_DISCHARGE_START_HOUR"]);
            int startM = int.Parse(settings["HOLD_FORCED_DISCHARGE_START_MINUTE"]);
            int endH = int.Parse(settings["HOLD_FORCED_DISCHARGE_END_HOUR"]);
            int endM = int.Parse(settings["HOLD_FORCED_DISCHARGE_END_MINUTE"]);
            int lim = int.Parse(settings["HOLD_FORCED_DISCHG_SOC_LIMIT"]);

            DateTime t = DateTime.Parse(settings["inverterRuntimeDeviceTime"]);
            t = DateTime.SpecifyKind(t, DateTimeKind.Local);

            return new LuxAction()
            {
                Enable = enabled,
                Start = GetDate(startH, startM, t),
                End = GetDate(endH, endM, t),
                Limit = lim,
                Rate = int.Parse(settings["HOLD_FORCED_DISCHG_POWER_CMD"])
            };
        }

        public async Task<bool> SetDischargeToGrid(LuxAction current, LuxAction required)
        {
            bool changes = false;

            if (current.Enable != required.Enable)
            {
                await PostAsync(UrlToWriteFunction, GetFuncParams("FUNC_FORCED_DISCHG_EN", required.Enable));
                changes = true;
            }

            if (current.Start.TimeOfDay != required.Start.TimeOfDay)
            {
                DateTime localStart = ToLocal(required.Start);
                await PostAsync(UrlToWriteTime, GetTimeParams("HOLD_FORCED_DISCHARGE_START_TIME", localStart));
                changes = true;
            }

            if (current.End.TimeOfDay != required.End.TimeOfDay)
            {
                DateTime localStop = ToLocal(required.End);
                await PostAsync(UrlToWriteTime, GetTimeParams("HOLD_FORCED_DISCHARGE_END_TIME", localStop));
                changes = true;
            }

            if (current.Limit != required.Limit)
            {
                await PostAsync(UrlToWrite, GetHoldParams("HOLD_FORCED_DISCHG_SOC_LIMIT", required.Limit.ToString()));
                changes = true;
            }

            if (current.Rate != required.Rate)
            {
                int rate = required.Rate < 0 || required.Rate > 100 ? 90 : required.Rate;
                rate = rate > 0 && rate < 13 ? 13 : rate;
                await PostAsync(UrlToWrite, GetHoldParams("HOLD_FORCED_DISCHG_POWER_CMD", rate.ToString())); // !
                changes = true;
            }

            return changes;
        }

        public (bool, int, int) GetBatteryCalibration(Dictionary<string, string> settings)
        {
            bool enabled = settings["FUNC_BATTERY_CALIBRATION_EN"].ToUpper() == "TRUE";
            int daysSince = int.Parse(settings["ASC_HOLD_ACCUMULATED_UNCALIBRATED_COUNT_DAYS"]);
            int period = int.Parse(settings["ASC_HOLD_CALIBRATION_PERIOD_DAYS"]);

            return (enabled, daysSince, period);
        }

        /*
        public async Task<bool> SetBatteryCalibrationDays(int days)
        {
            try
            {
                await PostAsync(UrlToWriteFunction, GetHoldParams("ASC_HOLD_CALIBRATION_PERIOD_DAYS", days.ToString()));
            }
            catch
            {
                return false;
            }
            return true;
        }

        public async Task<bool> SetBatteryCalibrationEnabled(bool enabled)
        {
            try
            {
                await PostAsync(UrlToWriteFunction, GetFuncParams("FUNC_BATTERY_CALIBRATION_EN", enabled));
            }
            catch
            {
                return false;
            }
            return true;
        }
        */

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

        public int KwhToBatt(int kWh)
        {
            decimal battkWh = _BatterySettings.CapacityAmpHours * _BatterySettings.Voltage / 1000M;
            return Convert.ToInt32(Math.Floor(kWh / battkWh));
        }

        public int BattToKwh(int batt)
        {
            decimal battkWh = _BatterySettings.CapacityAmpHours * _BatterySettings.Voltage / 1000M;
            return Convert.ToInt32(Math.Floor(Convert.ToDecimal(batt) * battkWh / 100M));
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

        public async Task<(double today, double tomorrow)> Forecast()
        {
            HttpResponseMessage r = await PostAsync("/WManage/api/weather/forecast", new Dictionary<string, string>()
            {
                {"serialNum", Settings.Station },
                { "refreshCache", "false"},
            });
            string json = await r.Content.ReadAsStringAsync();
            double today = 0;
            double tomorrow = 0;
            using (JsonDocument j = JsonDocument.Parse(json))
            {
                JsonElement e = j.RootElement;
                JsonElement f;
                if(e.TryGetProperty("todayPvEnergy", out f))
                {
                    today = f.GetProperty("todayPvEnergy").GetDouble();
                }
                if(e.TryGetProperty("ePvPredict", out f))
                {
                    tomorrow = f.GetProperty("tomorrowPvEnergy").GetDouble();
                }
            }
            return (today, tomorrow);
        }
    }

    public struct LuxActionSettings
    {
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public int Limit { get; set; }
        public int Rate { get; set; }

        public LuxActionSettings Clone()
        {
            return new LuxActionSettings()
            {
                Start = Start,
                End = End,
                Limit = Limit,
                Rate = Rate
            };
        }
    }
}
