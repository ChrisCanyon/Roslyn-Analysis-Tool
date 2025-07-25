using Microsoft.CodeAnalysis;

namespace DependencyAnalyzer.Interfaces
{
    public interface IRegistrationParser
    {
        public Task<List<RegistrationInfo>> GetSolutionRegistrations(Solution solution);
    }
}
