using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.IO;

namespace Luxopus.Services
{
    internal class Plan
    {

    }

    internal class LuxopusPlanSettings : Settings
    {
        public string PlanLocation { get; set; }
    }

    internal interface ILuxopusPlanService
    {
        Plan? GetCurrentPlan();
        void SavePlan(Plan plan);
    }

    internal class LuxopusPlanService : Service<LuxopusPlanSettings>, ILuxopusPlanService
    {
        public LuxopusPlanService(ILogger<LuxopusPlanService> logger, IOptions<LuxopusPlanSettings> settings) : base(logger, settings) { }

        public override bool ValidateSettings()
        {
            return Directory.Exists(Settings.PlanLocation);
        }

        public Plan? GetCurrentPlan()
        {
            return null;
        }

        public void SavePlan(Plan plan)
        {

        }
    }
}
