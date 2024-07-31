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

        (double powerKwh, double hours, double rateKw, int ratePercent) CalculateTransfer(int percentFrom, int percentTo, DateTime timeFrom, DateTime timeTo);

        int BatteryMinimumLimit { get; }
    }

    public class BatterySettings : Settings
    {
        public int BatteryMinimumLimit { get; set; } // 8
        public int MaxInversionW { get; set; } // 3600
        public int MaxBatteryW { get; set; } // 4000
        public int CapacityAmpHours { get; set; } // 189
        public int Voltage { get; set; } // 55
        public int Efficiency { get; set; } // 96?!
    }


    public class BatteryService : Service<BatterySettings>, IBatteryService
    {
        public BatteryService(ILogger<BatteryService> logger, IOptions<BatterySettings> settings) : base(logger, settings) { }

        public override bool ValidateSettings()
        {
            return Settings.CapacityAmpHours > 0 && Settings.Voltage > 0 && Settings.MaxBatteryW > 0;
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
            return Convert.ToDouble(Settings.MaxBatteryW) * Convert.ToDouble(percent) / (100.0 * 1000.0);
        }

        public int TransferKiloWattsToPercent(double kiloWatts)
        {
            return Convert.ToInt32(Math.Round(kiloWatts * 1000.0 * 100.0 / Settings.MaxBatteryW));
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

        public (double powerKwh, double hours, double rateKw, int ratePercent) CalculateTransfer(int percentFrom, int percentTo, DateTime timeFrom, DateTime timeTo)
        {
            double powerRequiredKwh = CapacityPercentToKiloWattHours(percentTo - percentFrom);
            powerRequiredKwh = (powerRequiredKwh < 0 ? -1 : 1) * powerRequiredKwh;
            double hoursToCharge = (timeTo - timeFrom).TotalHours;
            hoursToCharge = (hoursToCharge < 0 ? -1 : 0) * hoursToCharge;
            double kW = powerRequiredKwh / hoursToCharge;
            int b = TransferKiloWattsToPercent(kW);
            b = b < 0 ? 10 : b;
            return (powerRequiredKwh, hoursToCharge, kW, RoundPercent(b));
        }

        public int RoundPercent(int percent)
        {
             if (percent <= 13) { return 13; }
            else if (percent <= 21) { return 21; }
            else if (percent <= 34) { return 34; }
            else if (percent <= 55) { return 55; }
            else if (percent <= 67) { return 67; }
            else if (percent <= 75) { return 75; }
            else if (percent <= 80) { return 80; }
            return 90;
        }

        public int BatteryMinimumLimit { get { return Settings.BatteryMinimumLimit; } }
    }
}
