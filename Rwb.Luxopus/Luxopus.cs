using Microsoft.Extensions.Logging;
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
        public string Plan { get; set; }
        public string Burst { get; set; }
    }

    public class Luxopus
    {
        private readonly List<Job> _Jobs;
        private readonly IScheduler _Scheduler; // https://github.com/thomasgalliker/NCrontab.Scheduler
        private readonly ILogger _Logger;

        private readonly IReadOnlyList<Job> _StartupTasks;

        public Luxopus(ILogger<Luxopus> logger, IScheduler scheduler,
            LuxMonitor luxMonitor,
            LuxDaily luxDaily,
            LuxForecast luxForecast,
            OctopusMeters octopusMeters,
            OctopusPrices octopusPrices,
            Solcast solcast,
            SolarPosition sunPosition,
            Sunrise sunrise,
            Openweathermap openweathermap,
            ////PlanA planA
            ////PlanZero planZero,
            //PlanFlux2 planFlux,
            //Burst burst
            AtJob at,
            Planner planner,
            PlanChecker planChecker,
            BurstManager burst,
            GenerationForecast generationForecast
        )
        {
            _Logger = logger;
            _Scheduler = scheduler;
            _Scheduler.Next += _Scheduler_Next;

            if (planner.GetType() == typeof(NullJob)) { }

            _Jobs = new List<Job>();

            AddJob(luxMonitor, "* * * * *"); // every minute -- the most that cron will allow.
            AddJob(luxDaily, "51 * * * *"); // at the end of every day. Try every hour because of time zone nuissance.
            AddJob(luxForecast, "13 6-16 * * *"); // Hourly.
            AddJob(octopusMeters, "53 16 * * *"); // will get yesterday's meters.
            AddJob(octopusPrices, "34 11,16,17 * * *"); // tomorrow's prices 'should be' available at 4pm, apparently.
            AddJob(solcast, "21 7,16 * * *"); // Early morning to get update for the day, late night for making plan.
            AddJob(sunPosition, "*/13 * * * *"); // Every 13 minutes.
            AddJob(sunrise, "0 4 * * *"); // Every day -- before sunrise.
            AddJob(openweathermap, "0 */7 * * *"); // Every 7 hours.
            AddJob(planChecker, "1,31 * * * *"); // At the start of every half hour.
            // Make plan after getting prices and before evening peak.
            //AddJob(planA, "34 16 * * *"); 
            //AddJob(planZero, "34 16 * * *");
            //AddJob(planner, "34 10,16 * * *"); // Tried to call from octopusPrices whenever there are new prices but ended up with no plan.
            AddJob(planner, "34 16 * * *"); // Tried to call from octopusPrices whenever there are new prices but ended up with no plan.
            //AddJob(burst, "* 8-15 * * *");
            AddJob(burst, "* 8-15 * 3-9 *");
            AddJob(at, "*/8 * * * *");
            AddJob(generationForecast, "21 16 * * *");

            _StartupTasks = new List<Job>()
            {
                planner, // For dev.

                octopusMeters,
                luxMonitor,
                octopusPrices,
                sunPosition,
                sunrise,
                openweathermap,
                planner,
                planChecker,
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
