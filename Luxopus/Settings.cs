namespace Luxopus
{
    internal class LuxopusSettings
    {
        public string PlanLocation { get; set; }
    }

    /// <summary>
    /// Seems not to be provided by InfluxDB.Client so we make on rather than demand the entire config and then do .GetValue("InfluxDB:Token");.
    /// </summary>
    internal class InfluxDBSettings
    {
        public string Token { get; set; }
        public string Server { get; set; }
        public string Org { get; set; }
    }

    internal class LuxSettings
    {
        public string Username { get; set; }
        public string Password { get; set; }
        public string Station { get; set; }
        public string BaseAddress { get; set; }
        public string luxBaseAddress { get; set; }
        public string luxToken { get; set; }
        public string luxOrg { get; set; }
    }
}
