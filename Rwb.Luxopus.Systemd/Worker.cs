using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Configuration;
using System.Threading;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Systemd
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly Luxopus _luxopus;

        public Worker(ILogger<Worker> logger, Luxopus luxopus)
        {
            _logger = logger;
            _luxopus = luxopus;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _luxopus.Start();
            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
                await Task.Delay(1000, stoppingToken);
            }
            _luxopus.Stop();
        }
    }
}