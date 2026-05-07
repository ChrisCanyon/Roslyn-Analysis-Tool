using DependencyAnalyzer.Extensions;
using DependencyAnalyzer.Models;
using DependencyAnalyzer.Parsers;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
using System.Text;

namespace DependencyAnalyzer
{
    public static class TransientLeakRunner
    {
        public static async Task Run(string solutionPath, string outputDirectory, bool generateCsv = true)
        {
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
            Console.WriteLine("TRANSIENT LEAK ANALYZER");
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
            Console.WriteLine("This report identifies manual resolutions of TRANSIENT types");
            Console.WriteLine("that may cause memory leaks if not properly released.");
            Console.WriteLine("═══════════════════════════════════════════════════════════════");

            // Register MSBuild instance
            MSBuildLocator.RegisterDefaults();

            // Load the solution
            Console.WriteLine($"\nLoading solution: {solutionPath}");
            var workspace = MSBuildWorkspace.Create();
            var solution = await workspace.OpenSolutionAsync(solutionPath);
            Console.WriteLine($"Loaded {solution.Projects.Count()} projects");

            // Build analyzers
            Console.WriteLine("\nBuilding SolutionAnalyzer...");
            var solutionAnalyzer = await SolutionAnalyzer.Build(solution);
            Console.WriteLine($"Found {solutionAnalyzer.RegistrationInfos.Count} registrations");

            Console.WriteLine("\nBuilding ManualResolutionParser...");
            var manualResolutionParser = new ManualResolutionParser(solution, solutionAnalyzer);
            await manualResolutionParser.Build();
            Console.WriteLine($"Found {manualResolutionParser.ManuallyResolvedSymbols.Count} manual resolutions");
            Console.WriteLine($"Found {manualResolutionParser.ManuallyDisposedSymbols.Count} manual disposals");

            // Build dependency graph
            Console.WriteLine("\nBuilding DependencyGraph...");
            var dependencyAnalyzer = new Parsers.DependencyAnalyzer(solutionAnalyzer, manualResolutionParser);
            var dependencyGraph = dependencyAnalyzer.BuildFullDependencyGraph();

            // Create report runner
            var errorReportRunner = new ErrorReportGenerator(dependencyGraph, manualResolutionParser);

            // Create chain analyzer for deep dependency analysis
            var chainAnalyzer = new DependencyChainAnalyzer(dependencyGraph, manualResolutionParser);

            // Generate reports for each project
            var projectReports = new Dictionary<string, List<TransientResolutionInfo>>();
            var allProjects = solution.Projects.Select(p => p.Name).Distinct().OrderBy(p => p);

            Console.WriteLine($"\n═══════════════════════════════════════════════════════════════");
            Console.WriteLine("ANALYZING PROJECTS FOR TRANSIENT MANUAL RESOLUTIONS");
            Console.WriteLine($"═══════════════════════════════════════════════════════════════");

            foreach (var projectName in allProjects)
            {
                Console.WriteLine($"\n--- Project: {projectName} ---");

                // Parse the report to extract data for CSV
                var projectResolutions = ParseReportForProject(projectName, manualResolutionParser, dependencyGraph);
                if (projectResolutions.Any())
                {
                    projectReports[projectName] = projectResolutions;

                    // Group by direct vs dependency leaks
                    var directLeaks = projectResolutions.Where(r => !r.HasRelease && r.IsDirectLeak).ToList();
                    var dependencyLeaks = projectResolutions.Where(r => !r.HasRelease && !r.IsDirectLeak).ToList();
                    var properlyReleased = projectResolutions.Where(r => r.HasRelease).ToList();

                    if (directLeaks.Any())
                    {
                        Console.WriteLine($"  ❌ DIRECT LEAKS: {directLeaks.Count} transient(s) resolved in {projectName} not released");
                        foreach (var leak in directLeaks.Take(3)) // Show first 3
                        {
                            Console.WriteLine($"     - {leak.TypeName} at {leak.ResolutionPath.Split(':').FirstOrDefault()}");
                        }
                        if (directLeaks.Count > 3)
                            Console.WriteLine($"     ... and {directLeaks.Count - 3} more");
                    }

                    if (dependencyLeaks.Any())
                    {
                        var depByProject = dependencyLeaks.GroupBy(d => d.Project);
                        Console.WriteLine($"  ⚠️  DEPENDENCY LEAKS: {dependencyLeaks.Count} transient(s) from {depByProject.Count()} library(ies)");
                        foreach (var depGroup in depByProject.Take(2))
                        {
                            Console.WriteLine($"     From {depGroup.Key}: {depGroup.Count()} leak(s)");
                        }
                    }

                    if (properlyReleased.Any())
                    {
                        Console.WriteLine($"  ✓ {properlyReleased.Count} transient(s) properly released");
                    }
                }
                else
                {
                    Console.WriteLine($"  ✓ No transient manual resolutions found");
                }
            }

            // Generate summary
            Console.WriteLine($"\n═══════════════════════════════════════════════════════════════");
            Console.WriteLine("SUMMARY");
            Console.WriteLine($"═══════════════════════════════════════════════════════════════");

            int totalResolutions = projectReports.Sum(p => p.Value.Count);
            int totalUnreleased = projectReports.Sum(p => p.Value.Count(r => !r.HasRelease));
            int totalDirectLeaks = projectReports.Sum(p => p.Value.Count(r => !r.HasRelease && r.IsDirectLeak));
            int totalDependencyLeaks = projectReports.Sum(p => p.Value.Count(r => !r.HasRelease && !r.IsDirectLeak));

            Console.WriteLine($"Total projects analyzed: {allProjects.Count()}");
            Console.WriteLine($"Projects with transient resolutions: {projectReports.Count}");
            Console.WriteLine($"Total transient manual resolutions found: {totalResolutions}");
            Console.WriteLine($"Total resolutions without release (LEAKS): {totalUnreleased}");
            Console.WriteLine($"  - Direct leaks (in project itself): {totalDirectLeaks}");
            Console.WriteLine($"  - Dependency leaks (in libraries): {totalDependencyLeaks}");

            if (totalUnreleased > 0)
            {
                Console.WriteLine($"\n⚠️  WARNING: Found {totalUnreleased} potential memory leaks!");
                Console.WriteLine("These transient objects are manually resolved but never released.");
                if (totalDependencyLeaks > 0)
                {
                    Console.WriteLine($"Note: {totalDependencyLeaks} leaks are in dependencies and should be fixed in their respective libraries.");
                }
            }

            // Generate CSV if requested
            if (generateCsv && projectReports.Any())
            {
                string csvPath = Path.Combine(outputDirectory, "Transient_Manual_Resolutions.csv");
                GenerateCsvReport(projectReports, csvPath);
                Console.WriteLine($"\nCSV report generated: {csvPath}");
            }

            // Generate detailed chain analysis report
            Console.WriteLine("\n═══════════════════════════════════════════════════════════════");
            Console.WriteLine("GENERATING DEPENDENCY CHAIN ANALYSIS...");
            Console.WriteLine("═══════════════════════════════════════════════════════════════");

            // Get all manual resolutions that are transient and not released
            var transientManualResolutions = manualResolutionParser.ManuallyResolvedSymbols
                .Where(r => {
                    var node = dependencyGraph.Nodes.FirstOrDefault(n =>
                        n.SatisfiesDependency(r.ResolvedType));
                    return node != null && node.Lifetime == LifetimeTypes.Transient;
                })
                .ToList();

            var chainAnalysisReport = chainAnalyzer.GenerateChainAnalysisReport(transientManualResolutions);
            Console.WriteLine(chainAnalysisReport);

            // Save chain analysis report to file
            if (generateCsv)
            {
                string chainReportPath = Path.Combine(outputDirectory, "Dependency_Chain_Analysis.txt");
                File.WriteAllText(chainReportPath, chainAnalysisReport);
                Console.WriteLine($"Chain analysis report saved to: {chainReportPath}");

                // Generate detailed CSV with chain analysis
                string chainCsvPath = Path.Combine(outputDirectory, "Dependency_Chain_Analysis.csv");
                chainAnalyzer.GenerateChainAnalysisCsv(transientManualResolutions, chainCsvPath);
                Console.WriteLine($"Chain analysis CSV saved to: {chainCsvPath}");
            }

            Console.WriteLine("\n═══════════════════════════════════════════════════════════════");
            Console.WriteLine("ANALYSIS COMPLETE!");
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
        }

        private static List<TransientResolutionInfo> ParseReportForProject(
            string projectName,
            ManualResolutionParser manualResolutionParser,
            DependencyGraph dependencyGraph)
        {
            var results = new List<TransientResolutionInfo>();

            // Get ALL manual resolutions
            var allManualResolutions = manualResolutionParser.ManuallyResolvedSymbols ?? new List<ManualResolveInfo>();

            // Find which projects does our target project depend on
            var projectDependencies = GetProjectDependencies(projectName, dependencyGraph);
            projectDependencies.Add(projectName); // Include the project itself

            // Get resolutions from this project AND its dependencies
            var relevantResolutions = allManualResolutions
                .Where(x => projectDependencies.Contains(x.Project))
                .ToList();

            foreach (var resolution in relevantResolutions)
            {
                // Find ALL nodes that match this resolved type (don't filter by project)
                var matchingNodes = dependencyGraph.Nodes.Where(node =>
                    node.SatisfiesDependency(resolution.ResolvedType));

                foreach (var node in matchingNodes)
                {
                    if (node.Lifetime == LifetimeTypes.Transient)
                    {
                        // Check if there's a disposal IN THE SAME PROJECT where it was resolved
                        var disposals = manualResolutionParser.ManuallyDisposedSymbols
                            ?.Where(x => node.SatisfiesDependency(x.DisposedType) && x.Project == resolution.Project)
                            .ToList() ?? new List<ManualDisposeInfo>();

                        results.Add(new TransientResolutionInfo
                        {
                            Project = resolution.Project, // Use resolution's project, not the target project
                            TargetProject = projectName,  // Add field to track which project is affected
                            TypeName = node.ClassName,
                            ResolutionPath = resolution.InvocationPath,
                            HasRelease = disposals.Any(),
                            ReleaseLocations = disposals.Select(d => $"{d.FileAndLine}: {d.CodeSnippet}").ToList(),
                            IsDirectLeak = resolution.Project == projectName
                        });
                    }
                }
            }

            return results;
        }

        // Helper to find which projects a given project depends on
        private static HashSet<string> GetProjectDependencies(string project, DependencyGraph graph)
        {
            var dependencies = new HashSet<string>();

            // Look at all nodes in the target project
            var projectNodes = graph.Nodes.Where(n => n.ProjectName == project);

            // Find all projects that these nodes depend on
            foreach (var node in projectNodes)
            {
                AddDependencyProjects(node, dependencies, new HashSet<DependencyNode>());
            }

            return dependencies;
        }

        private static void AddDependencyProjects(DependencyNode node, HashSet<string> projects, HashSet<DependencyNode> visited)
        {
            if (!visited.Add(node)) return;

            projects.Add(node.ProjectName);

            foreach (var dependency in node.DependsOn)
            {
                AddDependencyProjects(dependency, projects, visited);
            }
        }

        private static void GenerateCsvReport(Dictionary<string, List<TransientResolutionInfo>> projectReports, string outputPath)
        {
            var csv = new StringBuilder();
            csv.AppendLine("Target Project,Resolution Project,Type,Leak Type,Has Release,Resolution Path,Release Locations");

            foreach (var project in projectReports.OrderBy(p => p.Key))
            {
                foreach (var resolution in project.Value.OrderBy(r => r.TypeName))
                {
                    var releaseLocations = string.Join("; ", resolution.ReleaseLocations);
                    var hasRelease = resolution.HasRelease ? "YES" : "NO - LEAK!";
                    var leakType = resolution.IsDirectLeak ? "Direct" : "Dependency";

                    csv.AppendLine($"\"{resolution.TargetProject}\",\"{resolution.Project}\",\"{resolution.TypeName}\",\"{leakType}\",\"{hasRelease}\",\"{resolution.ResolutionPath}\",\"{releaseLocations}\"");
                }
            }

            File.WriteAllText(outputPath, csv.ToString());
        }

        private class TransientResolutionInfo
        {
            public string Project { get; set; }  // Where the resolution happens
            public string TargetProject { get; set; }  // Which project is affected
            public string TypeName { get; set; }
            public string ResolutionPath { get; set; }
            public bool HasRelease { get; set; }
            public List<string> ReleaseLocations { get; set; } = new List<string>();
            public bool IsDirectLeak { get; set; }  // True if leak is in the target project itself
        }
    }
}