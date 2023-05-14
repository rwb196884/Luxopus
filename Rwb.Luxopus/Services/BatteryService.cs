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
        double CapacityPercentToKiloWattHours(int percent);

        int CapacityKiloWattHoursToPercent(double kiloWattHours);
        double TransferPercentToKiloWatts(int percent);

        int TransferKiloWattsToPercent(double kiloWatts);

        int RoundPercent(int percent);
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
            return Settings.CapacityAmpHours > 0 && Settings.Voltage > 0 && Settings.MaxPowerWatts > 0;
        }

        private double CapacityWh
        {
            get
            {
                return Convert.ToDouble(Settings.CapacityAmpHours * Settings.Voltage);
            }
        }

        public double CapacityPercentToKiloWattHours(int percent)
        {
            return CapacityWh * Convert.ToDouble(percent) / (100.0 * 1000.0);
        }

        public int CapacityKiloWattHoursToPercent(double kiloWattHours)
        {
            return Convert.ToInt32(Math.Round(kiloWattHours * 1000.0 * 100.0 / CapacityWh));
        }

        public double TransferPercentToKiloWatts(int percent)
        {
            return Convert.ToDouble(Settings.MaxPowerWatts) * Convert.ToDouble(percent) / (100.0 * 1000.0);
        }

        public int TransferKiloWattsToPercent(double kiloWatts)
        {
            return Convert.ToInt32(Math.Round(kiloWatts * 1000.0 * 100.0 / Settings.MaxPowerWatts));
        }




        //public int PercentForAnHour(int watts)
        //{
        //    return PercentPerHour(Settings.CapacityAmpHours, Settings.Voltage, watts);
        //}

        //public int Rate(int changePercent, double hours)
        //{
        //    int battWattHours = Settings.CapacityAmpHours * Settings.Voltage;
        //    int changeWattHours = battWattHours * changePercent / 100;
        //    double changeWattHoursPerHour = changeWattHours / hours;
        //    return Convert.ToInt32(Math.Round(changeWattHoursPerHour / Settings.MaxPowerWatts));
        //}

        //private static int PercentPerHour(int batteryAmpHours, int batteryVoltage, int watts)
        //{
        //    int battWattHours = batteryAmpHours * batteryVoltage;
        //    decimal hours = Convert.ToDecimal(battWattHours) / Convert.ToDecimal(watts);
        //    return Convert.ToInt32(Math.Ceiling(100M / hours));
        //}

        public int RoundPercent(int percent)
        {
            if (percent < 5) { return 5; }
            else if (percent <= 10) { return 10; }
            else if (percent <= 25) { return 25; }
            else if (percent <= 33) { return 33; }
            else if (percent <= 50) { return 50; }
            else if (percent <= 67) { return 67; }
            else if (percent <= 75) { return 75; }
            else if (percent <= 80) { return 80; }
            return 90;
        }
    }
}
