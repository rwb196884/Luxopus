using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Services
{
    public class AtSettings : Settings { }

    public interface IAtService
    {
        void Schedule(Func<Task> what, DateTime when);
        List<Func<Task>> Dequeue();
    }

    public class AtService : Service<AtSettings>, IAtService
    {
        class Job
        {
            public Func<Task> What;
            public DateTime When;
        }

        private readonly List<Job> _Jobs;
        private readonly object _Lock;

        public AtService(ILogger<AtService> logger, IOptions<AtSettings> settings) : base(logger, settings)
        {
            _Jobs = new List<Job>();
            _Lock = new object();
        }

        public override bool ValidateSettings() { return true; }

        public void Schedule(Func<Task> what, DateTime when)
        {
            lock (_Lock)
            {
                _Jobs.Add(new Job()
                {
                    What = what,
                    When = when
                });
            }
        }

        public List<Func<Task>> Dequeue()
        {
            List<Func<Task>> result = new List<Func<Task>>();
            lock (_Lock) { 
                for(int i = _Jobs.Count - 1; i >= 0; i--)
                {
                    if (_Jobs[i].When <= DateTime.Now)
                    {
                        result.Add(_Jobs[i].What);
                        _Jobs.RemoveAt(i);
                    }
                }
            }
            return result;
        }
    }
}
