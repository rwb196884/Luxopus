using Rwb.Luxopus.Jobs;
using Microsoft.Extensions.Logging;
using NCrontab;
using NCrontab.Scheduler;
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
            Batt batt,
            LuxDaily luxDaily,
            OctopusMeters octopusMeters,
            OctopusPrices octopusPrices,
            Solcast solcast,
            // Openweathermap
            // Solar elevation angle
            PlanChecker planChecker,
            PlanA planA
            )
        {
            _Logger = logger;
            _Jobs = new List<Job>();
            _Scheduler = scheduler;
            _Scheduler.Next += _Scheduler_Next;

            AddJob(luxMonitor, "*/8 * * * *"); // every 8 minutes.
            AddJob(batt, "*/13 * * * *"); // every 8 minutes.
            AddJob(luxDaily, "51 23 * * *"); // at the end of every day
            AddJob(octopusMeters, "53 16 * * *"); // will get yesterday's meters.
            AddJob(octopusPrices, "34 16 * * *"); // tomorrow's prices 'should be' available at 4pm, apparently.
            AddJob(solcast, "21 7,16 * * *"); // Early morning to get update for the day, late night for making plan.
            AddJob(planChecker, "1,31 * * * *"); // At the start of every half hour.
            AddJob(planA, "34 16 * * *"); // Make plan after getting prices and before evening peak.

            _StartupTasks = new List<Job>()
            {
                octopusMeters,
                octopusPrices,
                //solcast // severely rate lmited.
                planA,
                planChecker
            };
        }

        private void _Scheduler_Next(object? sender, ScheduledEventArgs e)
        {
            // Hello.
        }

        private void AddJob(Job j, string cron)
        {
            _Jobs.Add(j);
            _Scheduler.AddTask(CrontabSchedule.Parse(cron), j.RunAsync);
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
