using Luxopus.Jobs;
using Microsoft.Extensions.Logging;
using NCrontab;
using NCrontab.Scheduler;
using System.Collections.Generic;

namespace Luxopus
{
    internal class Luxopus
    {
        private readonly List<Job> _Jobs; 
        private readonly IScheduler _Scheduler; // https://github.com/thomasgalliker/NCrontab.Scheduler
        private readonly ILogger _Logger;

        public Luxopus(ILogger<Luxopus> logger, IScheduler scheduler, 
            LuxMonitor luxMonitor
            // LUX daily
            // Octopus prices
            // Octopus meters
            // Solcast
            // Openweathermap
            // Solar elevation angle
            )
        {
            _Logger = logger;
            _Jobs = new List<Job>();
            _Scheduler = scheduler;
            _Scheduler.Next += _Scheduler_Next;

            AddJob(luxMonitor, "*/1 * * * *"); // every 8 minutes.

        }

        private void _Scheduler_Next(object? sender, ScheduledEventArgs e)
        {
        }

        private void AddJob(Job j, string cron)
        {
            _Jobs.Add(j);
            _Scheduler.AddTask(CrontabSchedule.Parse(cron), j.RunAsync);
        }

        public void Start()
        {
            _Scheduler.Start();
        }

        public void Stop()
        {
            _Scheduler.Stop();
        }
    }
}
