using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NCrontab;
using NCrontab.Scheduler;
using Rwb.Luxopus.Jobs;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Rwb.Luxopus
{
    public class LuxopusSettings
    {
        public string Plan { get; set; }
        public string Check { get; set; }
        public string Burst { get; set; }
    }

    public class Luxopus
    {
        private readonly List<Job> _Jobs;
        private readonly IScheduler _Scheduler; // https://github.com/thomasgalliker/NCrontab.Scheduler
        private readonly ILogger _Logger;

        private readonly IReadOnlyList<Job> _StartupTasks;

        private Job GetJob(IServiceProvider serviceProvider, string name)
        {
            Type? t = Type.GetType($"Rwb.Luxopus.Jobs.{name}");
            if( t == null)
            {
                _Logger.LogError($"Could not find type Rwb.Luxopus.Jobs.{name} when configuring jobs.");
                return serviceProvider.GetRequiredService<NullJob>();
            }
            Job? j = serviceProvider.GetRequiredService(t!) as Job;
            if( j == null)
            {
                _Logger.LogError($"Could get service for type Rwb.Luxopus.Jobs.{name} when configuring jobs.");
                return serviceProvider.GetRequiredService<NullJob>();
            }
            return j!;
        }

        public Luxopus(IOptions<LuxopusSettings> settings, IServiceProvider serviceProvider, ILogger<Luxopus> logger, IScheduler scheduler,
            LuxMonitor luxMonitor,
            LuxDaily luxDaily,
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
        ){
            _Logger = logger;
            _Scheduler = scheduler;
            _Scheduler.Next += _Scheduler_Next;

            Job planner = GetJob(serviceProvider, settings.Value.Plan);
            _Logger.LogInformation($" Plan service: {planner.GetType().Name}");
            Job planChecker = GetJob(serviceProvider, settings.Value.Check);
            _Logger.LogInformation($"Check service: {planChecker.GetType().Name}");
            Job burst = GetJob(serviceProvider, settings.Value.Burst);
            _Logger.LogInformation($"Burst service: {burst.GetType().Name}");

            if (planner.GetType() == typeof(NullJob)) { }

            _Jobs = new List<Job>();

            AddJob(luxMonitor, "* * * * *"); // every minute -- the most that cron will allow.
            AddJob(luxDaily, "51 * * * *"); // at the end of every day. Try every hour because of time zone nuissance.
            AddJob(octopusMeters, "53 16 * * *"); // will get yesterday's meters.
            AddJob(octopusPrices, $"5,34 16,17 * * *"); // tomorrow's prices 'should be' available at 4pm, apparently.
            AddJob(solcast, "21 7,16 * * *"); // Early morning to get update for the day, late night for making plan.
            AddJob(sunPosition, "8 * * * *"); // Every 8 minutes.
            AddJob(sunrise, "0 10 * * *"); // Every day.
            AddJob(openweathermap, "0 */7 * * *"); // Every 7 hours.
            AddJob(planChecker, "1,31 * * * *"); // At the start of every half hour.
            // Make plan after getting prices and before evening peak.
            //AddJob(planA, "34 16 * * *"); 
            //AddJob(planZero, "38 16 * * *");
            AddJob(planner, "38 16 * * *");
            AddJob(burst, "* 8-15 * * *");
            AddJob(at, "*/8 * * * *");

            _StartupTasks = new List<Job>()
            {
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
}
