using Microsoft.Extensions.Options;
using System.IO;
using System.Threading.Tasks;

namespace Luxopus
{
    internal class LuxopusPlanService : ILuxopusPlanService
    {
        private readonly LuxopusSettings _AppSettings;

        public LuxopusPlanService(IOptions<LuxopusSettings> settings)
        {
            _AppSettings = settings.Value;
        }

        public bool ValidateSettings()
        {
            return Directory.Exists(_AppSettings.PlanLocation);
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
