using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rwb.Luxopus.Jobs;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rwb.Luxopus.Services
{
    internal static class IEnumerableExtensions
    {
        public static T? GetPrevious<T>(this IEnumerable<T> things, PeriodPlan? current, Func<T, bool> where = null) where T : Period
        {
            if (current == null) { return null; }
            return things.Where(z => z.Start < current.Start && (where == null || where(z))).OrderBy(z => z.Start).LastOrDefault();
        }

        public static T? GetNext<T>(this IEnumerable<T> things, PeriodPlan? current, Func<T, bool> where = null) where T : Period
        {
            if (current == null) { return null; }
            return things.Where(z => z.Start > current.Start && (where == null || where(z))).OrderBy(z => z.Start).FirstOrDefault();
        }

        /// <summary>
        /// Get the run of things.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="things"></param>
        /// <param name="start"></param>
        /// <param name="condition"></param>
        /// <returns></returns>
        public static IEnumerable<T> Gap<T>(this IOrderedEnumerable<T> things, T start, Func<T, bool> condition) where T : class
        {
            List<T> gap = new List<T>();
            if (condition(start)) { return gap; }
            using (IEnumerator<T> e = things.GetEnumerator())
            {
                bool moveNext = e.MoveNext();
                while (moveNext && e.Current != start) { moveNext = e.MoveNext(); }
                if (e.Current == null) { return gap; }
                moveNext = e.MoveNext();
                while (moveNext && condition(e.Current))
                {
                    gap.Add(e.Current);
                    moveNext = e.MoveNext();
                }
                if (moveNext && e.Current != start && !condition(e.Current))
                {
                    return gap;
                }
                return new List<T>();
            }
        }

        /// <summary>
        /// Get the run of things.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="things"></param>
        /// <param name="start"></param>
        /// <param name="condition"></param>
        /// <returns></returns>
        public static IOrderedEnumerable<T> Pag<T, TOrderBy>(this IOrderedEnumerable<T> things, T end, Func<T, bool> condition, Func<T, TOrderBy> sort) where T : class
        {
            List<T> pag = new List<T>();
            if (!condition(end)) { return pag.OrderBy(sort); }
            using (IEnumerator<T> e = things.GetEnumerator())
            {
                bool moveNext = e.MoveNext();
                while (moveNext && e.Current != end) { moveNext = e.MoveNext(); }
                if (e.Current == null) { return pag.OrderBy(sort); }
                //moveNext = e.MoveNext();
                while (moveNext && condition(e.Current))
                {
                    pag.Add(e.Current);
                    moveNext = e.MoveNext();
                }
                //if (moveNext && e.Current != end && !condition(e.Current))
                //{
                    return pag.OrderBy(sort);
                //}
                return (new List<T>()).OrderBy(sort);
            }
        }

        public static double FutureFreeHoursBeforeNextDischarge<T>(this IEnumerable<T> things, PeriodPlan current) where T : PeriodPlan
        {
            double h = 0;
            T c = things.Single(z => z.Start == current.Start);
            T? next = things.GetNext(current);
            while (next != null)
            {
                if (Plan.DischargeToGridCondition(c)) { return h; }
                if (c.Buy <= 0)
                {
                    h += next.Start.Subtract(c.Start).TotalHours;
                }
                c = next;
                next = things.GetNext(next);
            }
            return h;
        }
    }

    public class PlanSettings
    {
        public bool ChargeFromGrid { get; set; }
        public int ChargeFromGridLevel { get; set; }
        public DateTime ChargeFromGridStart { get; set; }
        public DateTime ChargeFromGridStop { get; set; }
    }

    public class Plan //: IQueryable<HalfHourPlan>
    {
        //public DateTime Created { get; set; }

        // Simply extending List<> doens't work with JsonSerializer.
        // Implementing IQueryable<HalfHourPlan> fucks it up too.
        public List<PeriodPlan> Plans { get; set; }

        public Plan(IEnumerable<ElectricityPrice> prices)
        {
            Plans = prices.Select(z => new PeriodPlan(z)).ToList();
        }

        /// <summary>
        /// Required for System.Text.Json.JsonSerializer.Deserialize.
        /// </summary>
        /// <param name="plans"></param>
        public Plan() { }

        [JsonIgnore]
        public PeriodPlan? Current
        {
            get
            {
                //return Plans.OrderBy(z => z.Start).First();
                return Plans.OrderByDescending(z => z.Start).FirstOrDefault(z => z.Start < DateTime.UtcNow);
            }
        }

        [JsonIgnore]
        public PeriodPlan? Previous
        {
            get
            {
                return Plans.GetPrevious(Current);
            }
        }

        [JsonIgnore]
        public PeriodPlan? Next
        {
            get
            {
                return Plans.GetNext(Current);
            }
        }

        public override string ToString()
        {
            if (Plans.Count == 0) { return "Ce n'est pas un plan."; }
            IEnumerable<PeriodPlan> plans = Plans.OrderBy(z => z.Start);
            return $"{plans.First().Start.ToString("dd MMM HH:mm")} to {plans.Last().Start.ToString("dd MMM HH:mm")}";
        }

        /// <summary>
        /// Find the next contiguous run of periods in which the condition is true.
        /// </summary>
        /// <param name="start"></param>
        /// <param name="condition"></param>
        /// <returns></returns>
        public (IEnumerable<PeriodPlan>, PeriodPlan?) GetNextRun(PeriodPlan start, Func<PeriodPlan, bool> condition)
        {
            List<PeriodPlan> run = new List<PeriodPlan>();
            PeriodPlan? p = start;
            // Move to the start of the next run;
            while(p != null && !condition(p))
            {
                p = Plans.GetNext(p);
            }
            while (p != null && condition(p))
            {
                run.Add(p);
                p = Plans.GetNext(p);
            }
            return (run, p);
        }

        public static Func<PeriodPlan, bool> DischargeToGridCondition { get { return (PeriodPlan p) => (p.Action?.DischargeToGrid ?? 100) < 100; } }
        public static Func<PeriodPlan, bool> ChargeFromGridCondition { get { return (PeriodPlan p) => (p.Action?.ChargeFromGrid ?? 0) > 0; } }
        public static Func<PeriodPlan, bool> FreeElectricityCondition { get { return (PeriodPlan p) => p.Buy < 0; } }

        #region IQueryable
        /*
        public Type ElementType => Plans.AsQueryable().ElementType;

        public Expression Expression => Plans.AsQueryable().Expression;

        public IQueryProvider Provider => Plans.AsQueryable().Provider;

        public IEnumerator<HalfHourPlan> GetEnumerator()
        {
            return Plans.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return ((IEnumerable)Plans).GetEnumerator();
        }
        */
        #endregion
    }

    public class LuxopusPlanSettings : Settings
    {
        public string PlanLocation { get; set; }
    }

    public interface ILuxopusPlanService
    {
        Plan? Load(DateTime t);
        IEnumerable<Plan> LoadAll(DateTime t);
        void Save(Plan plan);
    }

    public class LuxopusPlanService : Service<LuxopusPlanSettings>, ILuxopusPlanService
    {
        const string FileDateFormat = "yyyy-MM-dd_HH-mm";
        public LuxopusPlanService(ILogger<LuxopusPlanService> logger, IOptions<LuxopusPlanSettings> settings) : base(logger, settings) { }

        public override bool ValidateSettings()
        {
            return Directory.Exists(Settings.PlanLocation);
        }

        private static string GetFilename(Plan p)
        {
            IEnumerable<PeriodPlan> o = p.Plans.OrderBy(z => z.Start);
            return o.First().Start.ToString(FileDateFormat) + "__" + o.Last().Start.ToString(FileDateFormat);
        }

        private FileInfo GetPlanFile(Plan p)
        {
            return new FileInfo(Path.Combine(Settings.PlanLocation, GetFilename(p)));
        }

        private static Plan? Load(FileInfo planFile)
        {
            if (!planFile.Exists) { return null; }
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
                .Select(z => Load(z.File))
                .Where(z => z != null)
                .Select(z => z!);
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

            if (f == null)
            {
                return null;
            }

            return Load(f);
        }

        public void Save(Plan plan)
        {
            using (FileStream fs = new FileStream(Path.Combine(Settings.PlanLocation, GetFilename(plan)), FileMode.OpenOrCreate))
            {
                using (StreamWriter w = new StreamWriter(fs))
                {
                    string json = JsonSerializer.Serialize(plan);
                    w.Write(json);
                }
            }
        }
    }
}
