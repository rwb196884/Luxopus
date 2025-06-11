using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NCrontab;
using NCrontab.Scheduler;
using Rwb.Luxopus.Jobs;
using Rwb.Luxopus.Services;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Rwb.Luxopus
{
    public class LuxopusSettings : Settings
    {
        public string Check { get; set; }
        public string Burst { get; set; }
    }

    public class Luxopus
    {
        private readonly List<Job> _Jobs;
        private readonly IScheduler _Scheduler; // https://github.com/thomasgalliker/NCrontab.Scheduler
        private readonly ILogger _Logger;

        private readonly IReadOnlyList<Job> _StartupTasks;

        public Luxopus(ILuxopusServiceResolver luxopusServiceResolver, ILogger<Luxopus> logger, IScheduler scheduler,
            LuxMonitor luxMonitor,
            LuxDaily luxDaily,
            LuxForecast luxForecast,
            OctopusMeters octopusMeters,
            OctopusPrices octopusPrices,
            Solcast solcast,
            SolarPosition sunPosition,
            Sunrise sunrise,
            Openweathermap openweathermap,
            //PlanChecker planChecker,
            ////PlanA planA
            ////PlanZero planZero,
            //PlanFlux2 planFlux,
            //Burst burst
            AtJob at
        )
        {
            _Logger = logger;
            _Scheduler = scheduler;
            _Scheduler.Next += _Scheduler_Next;

            Job planner = luxopusServiceResolver.GetPlanJob();
            _Logger.LogInformation($" Plan service: {planner.GetType().Name}");
            Job planChecker = luxopusServiceResolver.GetCheckJob();
            _Logger.LogInformation($"Check service: {planChecker.GetType().Name}");
            Job burst = luxopusServiceResolver.GetBurstJob();
            _Logger.LogInformation($"Burst service: {burst.GetType().Name}");

            if (planner.GetType() == typeof(NullJob)) { }

            _Jobs = new List<Job>();

            AddJob(luxMonitor, "* * * * *"); // every minute -- the most that cron will allow.
            AddJob(luxDaily, "51 * * * *"); // at the end of every day. Try every hour because of time zone nuissance.
            AddJob(luxForecast, "13 6-16 * * *"); // Hourly.
            AddJob(octopusMeters, "53 16 * * *"); // will get yesterday's meters.
            AddJob(octopusPrices, "34 9,16,17 * * *"); // tomorrow's prices 'should be' available at 4pm, apparently.
            AddJob(solcast, "21 7,16 * * *"); // Early morning to get update for the day, late night for making plan.
            AddJob(sunPosition, "*/13 * * * *"); // Every 13 minutes.
            AddJob(sunrise, "0 4 * * *"); // Every day -- before sunrise.
            AddJob(openweathermap, "0 */7 * * *"); // Every 7 hours.
            AddJob(planChecker, "1,31 * * * *"); // At the start of every half hour.
            // Make plan after getting prices and before evening peak.
            //AddJob(planA, "34 16 * * *"); 
            //AddJob(planZero, "38 16 * * *");
            //AddJob(planner, "38 10,16 * * *"); // Is now called from octopusPrices whenever there are new prices.
            AddJob(burst, "* 8-15 * * *");
            AddJob(at, "*/8 * * * *");

            _StartupTasks = new List<Job>()
            {
                luxMonitor,
                octopusPrices,
                sunPosition,
                sunrise,
                openweathermap,
                planner,
                burst,
                planChecker
                //planZero,
                //octopusMeters,
                ////solcast, // severely rate lmited.
            };
        }

        private void _Scheduler_Next(object? sender, ScheduledEventArgs e)
        {
            // Hello.
        }

        private void AddJob(Job j, string cron)
        {
            _Jobs.Add(j);
            Guid id = _Scheduler.AddTask(CrontabSchedule.Parse(cron), j.RunAsync);
            _Logger.LogInformation($"Job {j.GetType().Name} is {id}.");
        }

        public void Start()
        {
            foreach (Job j in _StartupTasks)
            {
                _Logger.LogInformation($"Running startup job {j.GetType().Name}.");
                j.RunAsync(CancellationToken.None).Wait();
            }

            _Logger.LogInformation("Luxopus is starting scheduler.");
            _Scheduler.Start();
        }

        public void Stop()
        {
            _Scheduler.Stop();
            _Logger.LogInformation("Luxopus has stopped scheduler.");
        }
    }

    public interface ILuxopusServiceResolver
    {
        Job GetPlanJob();
        Job GetCheckJob();
        Job GetBurstJob();
    }

    public class LuxopusServiceResolver : Service<LuxopusSettings>, ILuxopusServiceResolver
    {
        private readonly IServiceProvider _ServiceProvider;

        public LuxopusServiceResolver(ILogger<LuxopusServiceResolver> logger, IOptions<LuxopusSettings> settings, IServiceProvider serviceProvider) : base(logger, settings)
        {
            _ServiceProvider = serviceProvider;
        }

        public override bool ValidateSettings()
        {
            if (GetType() == null) { return false; }
            if (GetType() == null) { return false; }
            if (GetType() == null) { return false; }
            return true;
        }

        private Type? GetType(string name)
        {
            return Type.GetType($"Rwb.Luxopus.Jobs.{name}"); ;
        }

        private Job GetJob(string name)
        {
            Type? t = GetType(name);
            if (t == null)
            {
                Logger.LogError($"Could not find type Rwb.Luxopus.Jobs.{name}.");
                return _ServiceProvider.GetRequiredService<NullJob>();
            }
            Job? j = _ServiceProvider.GetRequiredService(t!) as Job;
            if (j == null)
            {
                Logger.LogError($"Could get service for type Rwb.Luxopus.Jobs.{name}.");
                return _ServiceProvider.GetRequiredService<NullJob>();
            }
            return j!;
        }


        public Job GetPlanJob()
        {
            //return GetJob(Settings.Plan);
            return _ServiceProvider.GetRequiredService<Planner>();
        }

        public Job GetCheckJob()
        {
            return GetJob(Settings.Check);
        }

        public Job GetBurstJob()
        {
            return GetJob(Settings.Burst);
        }
    }
}
