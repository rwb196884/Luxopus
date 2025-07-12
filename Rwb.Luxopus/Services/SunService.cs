namespace Rwb.Luxopus.Services
{
    // https://coordinatesharp.com/DeveloperGuide#available-celestial-data

    public class SunSettings : Settings
    {
        public decimal Latitude { get; set; }
        public decimal Longitude { get; set; }
    }

    public interface ISunService
    {
        DateTime? GetSunrise(DateTime date);
        DateTime? GetSunset(DateTime date);

        /// <summary>
        /// Degrees up from horizon.
        /// </summary>
        /// <returns></returns>
        double GetSunSolarElevationAngle();

        /// <summary>
        /// Degrees clockwise from north.
        /// </summary>
        /// <returns></returns>
        double GetSunSolarAzimuth();
    }

    internal class SunService : Service<SunSettings>, ISunService
    {
        public SunService(ILogger<SunService> logger, IOptions<SunSettings> settings)
            : base(logger, settings)
        {

        }

        private Coordinate GetCoordinate(DateTime t)
        {
            return new Coordinate(Convert.ToDouble(Settings.Latitude), Convert.ToDouble(Settings.Longitude), t);
        }

        public override bool ValidateSettings()
        {
            if (Settings.Latitude < -90
                || Settings.Latitude > 90
                || Settings.Longitude < -180
                || Settings.Longitude > 180
                )
            {
                return false;
            }
            return true;
        }

        public DateTime? GetSunrise(DateTime date)
        {
            Coordinate c = GetCoordinate(date);
            return ToUtc(c.CelestialInfo.SunRise);
        }

        public DateTime? GetSunset(DateTime date)
        {
            Coordinate c = GetCoordinate(date);
            return ToUtc(c.CelestialInfo.SunSet);
        }

        public double GetSunSolarElevationAngle()
        {
            Coordinate c = GetCoordinate(DateTime.UtcNow);
            return c.CelestialInfo.SunAltitude;
        }

        public double GetSunSolarAzimuth()
        {
            Coordinate c = GetCoordinate(DateTime.UtcNow);
            return c.CelestialInfo.SunAzimuth;
        }

        private DateTime? ToUtc(DateTime? localTime)
        {
            if (!localTime.HasValue) { return null; }
            return DateTime.SpecifyKind(localTime.Value, DateTimeKind.Utc);
        }
    }
}
