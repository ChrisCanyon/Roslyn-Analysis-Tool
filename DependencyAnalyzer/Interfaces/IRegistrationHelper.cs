using Microsoft.CodeAnalysis;

namespace DependencyAnalyzer.Interfaces
{
    public interface IRegistrationHelper
    {
        public Task<List<RegistrationInfo>> GetSolutionRegistrations(Solution solution);
    }
}
