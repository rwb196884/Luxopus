using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.IO;

namespace Rwb.Luxopus.Services
{
    public interface IBurstLogService
    {
        void Write(string burstLog);
        string Read();
        void Clear();
    }

    public class BurstLogSettings : Settings
    {
        public string LogLocation { get; set; }
    }

    public class BurstLogService : Service<BurstLogSettings>, IBurstLogService
    {
        public BurstLogService(ILogger<BurstLogService> logger, IOptions<BurstLogSettings> settings) : base(logger, settings) { }

        public override bool ValidateSettings()
        {
            return Directory.Exists(Settings.LogLocation);
        }

        private string FilePath { get { return Path.Join(Settings.LogLocation, "burst.log"); } }
        private FileInfo BurstLogFile { get { return new FileInfo(FilePath); } }

        public void Write(string burstLog)
        {
            using (StreamWriter w = BurstLogFile.AppendText())
            {
                w.WriteLine(DateTime.UtcNow.ToString("dd MMM HH:mm") + " UTC");
                w.WriteLine(burstLog);
                w.WriteLine();
            }
        }

        public string Read()
        {
            if (!File.Exists(BurstLogFile.FullName)) { return ""; }
            string burstLog = "";
            using (FileStream fs = File.OpenRead(BurstLogFile.FullName))
            {
                using (StreamReader r = new StreamReader(fs))
                {
                    burstLog = r.ReadToEnd();
                }
            }
            return burstLog;
        }

        public void Clear()
        {
            if (File.Exists(BurstLogFile.FullName))
            {
                File.Delete(BurstLogFile.FullName);
            }
        }
    }
}
