using DependencyAnalyzer.Interfaces;
using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DependencyAnalyzer.RegistrationParsers
{
    public class MicrosoftDIRegistrationParser : IRegistrationParser
    {
        public async Task<List<RegistrationInfo>> GetSolutionRegistrations(Solution solution)
        {
            var ret = new List<RegistrationInfo>();

            var registrationTasks = new List<Task<List<RegistrationInfo>>>();

            foreach (var project in solution.Projects)
            {
                if (project.Name.ToLower().Contains("test")) continue;
                //no async for debug
#if DEBUG
                var projectRegistrations = await GetRegistrationsFromProjectAsync(project, solution);
                ret.AddRange(projectRegistrations);
#else
                registrationTasks.Add(GetRegistrationsFromProjectAsync(project, solution));
#endif
            }

            await Task.WhenAll(registrationTasks.ToArray());

            foreach (var task in registrationTasks)
            {
                var projectRegistrations = task.Result;
                ret.AddRange(projectRegistrations);
            }

            return ret;
        }

        private async Task<List<RegistrationInfo>> GetRegistrationsFromProjectAsync(Project project, Solution solution)
        {
            var registrations = new List<RegistrationInfo>();

            foreach (var doc in project.Documents)
            {
                var root = await doc.GetSyntaxRootAsync();

                // Get the semantic model for the current document (lets us resolve symbols)
                var model = await doc.GetSemanticModelAsync();

                if (root == null || model == null) continue;

                // Find all method invocation expressions in this file (e.g., container.Register(...))
               
                //Are installers a thing in M.DI
                
                //loop through invocations and parse registration info
            }

            // Return all registrations found in this project
            return registrations;
        }
    }
}
