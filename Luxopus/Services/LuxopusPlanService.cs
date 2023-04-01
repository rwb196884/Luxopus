using CsvHelper;
using Luxopus.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Luxopus.Services
{
    public class Plan : List<HalfHourPlan>
    {
        public HalfHourPlan? Current
        {
            get
            {
                return this.OrderByDescending(z => z.Start).FirstOrDefault(z => z.Start < DateTime.UtcNow);
            }
        }

        public HalfHourPlan? Previous
        {
            get
            {
                return this.OrderByDescending(z => z.Start).Where(z => z.Start < DateTime.UtcNow).Skip(1).Take(1).SingleOrDefault();
            }
        }

        public HalfHourPlan? Next
        {
            get
            {
                return this.OrderBy(z => z.Start).Where(z => z.Start > DateTime.UtcNow).FirstOrDefault();
            }
        }
    }

    internal class LuxopusPlanSettings : Settings
    {
        public string PlanLocation { get; set; }
    }

    internal interface ILuxopusPlanService
    {
        Plan? Load(DateTime t);
        IEnumerable<Plan> LoadAll(DateTime t);
        void Save(Plan plan);
    }

    internal class LuxopusPlanService : Service<LuxopusPlanSettings>, ILuxopusPlanService
    {
        const string FileDateFormat = "yyyy-MM-dd_HH-mm";
        public LuxopusPlanService(ILogger<LuxopusPlanService> logger, IOptions<LuxopusPlanSettings> settings) : base(logger, settings) { }

        public override bool ValidateSettings()
        {
            return Directory.Exists(Settings.PlanLocation);
        }

        private static string GetFilename(Plan p)
        {
            IEnumerable<HalfHourPlan> o = p.OrderBy(z => z.Start);
            return o.First().Start.ToString(FileDateFormat) + "__" + o.Last().Start.ToString(FileDateFormat);
        }

        private FileInfo GetPlanFile(Plan p)
        {
            return new FileInfo(Path.Combine(Settings.PlanLocation, GetFilename(p)));
        }

        private static Plan Load(FileInfo planFile)
        {
            using (FileStream fs = planFile.OpenRead())
            {
                using (StreamReader r = new StreamReader(fs))
                {
                    string json = r.ReadToEnd();
                    return JsonSerializer.Deserialize<Plan>(json);
                }
            }
        }

        public IEnumerable<Plan> LoadAll(DateTime t)
        {
            return (new DirectoryInfo(Settings.PlanLocation))
            .GetFiles().Select(z =>
            {
                string[] bits = z.Name.Split("__");
                return new
                {
                    File = z,
                    FirstPlan = DateTime.ParseExact(bits[0], "yyyy-MM-dd_HH-mm", CultureInfo.InvariantCulture),
                    LastPlan = DateTime.ParseExact(bits[1], "yyyy-MM-dd_HH-mm", CultureInfo.InvariantCulture)
                };
            }
            )
                .Where(z => z.FirstPlan <= t && z.LastPlan >= t)
                .Select(z => Load(z.File));
        }

        public Plan? Load(DateTime t)
        {
            FileInfo? f = (new DirectoryInfo(Settings.PlanLocation))
           .GetFiles().Select(z =>
           {
               string[] bits = z.Name.Split("__");
               return new
               {
                   File = z,
                   FirstPlan = DateTime.ParseExact(bits[0], "yyyy-MM-dd_HH-mm", CultureInfo.InvariantCulture),
                   LastPlan = DateTime.ParseExact(bits[1], "yyyy-MM-dd_HH-mm", CultureInfo.InvariantCulture)
               };
           }
           )
               .Where(z => z.FirstPlan <= t && z.LastPlan >= t)
               .OrderByDescending(z => z.FirstPlan)
               .FirstOrDefault()
               ?.File;

            if(f == null)
            {
                return null;
            }

            return Load(f);
        }

        public void Save(Plan plan)
        {
            using (FileStream fs = new FileStream(Path.Combine(Settings.PlanLocation, GetFilename(plan)), FileMode.CreateNew))
            {
                using (StreamWriter w = new StreamWriter(fs))
                {
                    w.Write(JsonSerializer.Serialize(plan));
                }
            }
        }

    }
}
