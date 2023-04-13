using Microsoft.Extensions.Logging;
using NCrontab;
using NCrontab.Scheduler;
using Rwb.Luxopus.Jobs;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Rwb.Luxopus
{
    public class Luxopus
    {
        private readonly List<Job> _Jobs;
        private readonly IScheduler _Scheduler; // https://github.com/thomasgalliker/NCrontab.Scheduler
        private readonly ILogger _Logger;

        private readonly IReadOnlyList<Job> _StartupTasks;

        public Luxopus(ILogger<Luxopus> logger, IScheduler scheduler,
            LuxMonitor luxMonitor,
            LuxDaily luxDaily,
            OctopusMeters octopusMeters,
            OctopusPrices octopusPrices,
            Solcast solcast,
            SolarPosition sunPosition,
            Sunrise sunrise,
            // Openweathermap
            // Solar elevation angle
            PlanChecker planChecker,
            //PlanA planA
            PlanZero planZero,
            PlanFlux planFlux
            )
        {
            _Logger = logger;
            _Jobs = new List<Job>();
            _Scheduler = scheduler;
            _Scheduler.Next += _Scheduler_Next;

            AddJob(luxMonitor, "*/8 * * * *"); // every 8 minutes.
            AddJob(luxDaily, "51 * * * *"); // at the end of every day. Try every hour because of time zone nuissance.
            AddJob(octopusMeters, "53 16 * * *"); // will get yesterday's meters.
            AddJob(octopusPrices, "34 16 * * *"); // tomorrow's prices 'should be' available at 4pm, apparently.
            AddJob(solcast, "21 7,16 * * *"); // Early morning to get update for the day, late night for making plan.
            AddJob(sunPosition, "8 * * * *"); // Every 8 minutes.
            AddJob(sunrise, "0 10 * * *"); // Every day.
            AddJob(planChecker, "1,31 * * * *"); // At the start of every half hour.
            // Make plan after getting prices and before evening peak.
            //AddJob(planA, "34 16 * * *"); 
            AddJob(planZero, "38 16 * * *");
            AddJob(planFlux, "38 16 * * *"); // Has been hacked to let planZero take priority.

            _StartupTasks = new List<Job>()
            {
                luxMonitor,
                //octopusMeters,
                //octopusPrices,
                ////solcast, // severely rate lmited.
                ////planA,
                planZero,
                planFlux,
                planChecker,
                sunrise,
                sunPosition
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
