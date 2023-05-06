using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;

namespace Rwb.Luxopus.Services
{
    /// <summary>
    /// Converting between energy, percent, and rates.
    /// </summary>
    public interface IBatteryService
    {
        /// <summary>
        /// Get the battery percent change for receiving/supplying power at a rate of <paramref name="watts"/> for one hour.
        /// </summary>
        /// <param name="watts"></param>
        /// <returns></returns>
        int PercentForAnHour(int watts);

        /// <summary>
        /// Get the battery charge rate in percent of maximum that is required to produce a change of <paramref name="changePercent"/> percent
        /// over a duration of <paramref name="hours"/> hours.
        /// </summary>
        /// <param name="changePercent"></param>
        /// <param name="hours"></param>
        /// <returns></returns>
        int Rate(int changePercent, double hours);
    }

    public class BatterySettings : Settings
    {
        public int CapacityAmpHours { get; set; } // 189
        public int Voltage { get; set; } // 55
        public int MaxPowerWatts { get; set; } // 3000
    }


    public class BatteryService : Service<BatterySettings>, IBatteryService
    {
        public BatteryService(ILogger<BatteryService> logger, IOptions<BatterySettings> settings) : base(logger, settings) { }

        public override bool ValidateSettings()
        {
            return true;
        }

        public int PercentForAnHour(int watts)
        {
            return PercentPerHour(Settings.CapacityAmpHours, Settings.Voltage, watts);
        }

        public int Rate(int changePercent, double hours)
        {
            int battWattHours = Settings.CapacityAmpHours * Settings.Voltage;
            int changeWattHours = battWattHours * changePercent / 100;
            double changeWattHoursPerHour = changeWattHours / hours;
            return Convert.ToInt32(Math.Round(changeWattHoursPerHour / Settings.MaxPowerWatts));
        }

        private static int PercentPerHour(int batteryAmpHours, int batteryVoltage, int watts)
        {
            int battWattHours = batteryAmpHours * batteryVoltage;
            decimal hours = Convert.ToDecimal(battWattHours) / Convert.ToDecimal(watts);
            return Convert.ToInt32(Math.Ceiling(100M / hours));
        }
    }
}
