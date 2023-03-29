using System.Threading.Tasks;

namespace Luxopus
{
    internal interface ILuxopusPlanService
    {
        bool ValidateSettings();
        Plan? GetCurrentPlan();
        void SavePlan(Plan plan);
    }
}
