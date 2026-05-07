using DependencyAnalyzer.Parsers;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;

namespace DependencyAnalyzer
{
    /// <summary>
    /// Runner for analyzing async-unsafe manual resolution patterns.
    /// Finds PerWebRequest dependencies resolved through transient chains,
    /// which will break when HttpContext.Current is null in async contexts.
    /// </summary>
    public static class PerWebRequestAsyncUnsafeRunner
    {
        public static async Task Run(string solutionPath, string outputDirectory, string? projectFilter = null)
        {
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
            Console.WriteLine("ASYNC-UNSAFE MANUAL RESOLUTION ANALYZER");
            Console.WriteLine("Detecting Windsor DI patterns that break in async contexts");
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
            Console.WriteLine();
            Console.WriteLine("This analyzer finds PerWebRequest dependencies that are manually");
            Console.WriteLine("resolved through transient chains. These will fail when");
            Console.WriteLine("HttpContext.Current is null in async contexts.");
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

            Console.WriteLine("\nBuilding DependencyAnalyzer...");
            var dependencyAnalyzer = new DependencyAnalyzer.Parsers.DependencyAnalyzer(solutionAnalyzer, manualResolutionParser);
            var dependencyGraph = dependencyAnalyzer.BuildFullDependencyGraph();
            Console.WriteLine($"Built dependency graph with {dependencyGraph.Nodes.Count} nodes");

            // Run PerWebRequestAsyncUnsafe analyzer
            Console.WriteLine("\nRunning Async-Unsafe analyzer...");
            var asyncUnsafeAnalyzer = new PerWebRequestAsyncUnsafeAnalyzer(dependencyGraph, manualResolutionParser);
            var results = asyncUnsafeAnalyzer.Analyze(projectFilter);

            // Generate console report
            asyncUnsafeAnalyzer.GenerateConsoleReport(results);

            // Generate CSV report
            string csvFileName = projectFilter != null
                ? $"AsyncUnsafe_{projectFilter.Replace(".", "_")}.csv"
                : "AsyncUnsafe_All.csv";
            string outputPath = Path.Combine(outputDirectory, csvFileName);
            asyncUnsafeAnalyzer.GenerateCsvReport(results, outputPath);

            // Generate summary
            Console.WriteLine("\n═══════════════════════════════════════════════════════════════");
            Console.WriteLine("ANALYSIS SUMMARY");
            Console.WriteLine("═══════════════════════════════════════════════════════════════");

            if (results.Any())
            {
                var groupedByProject = results.GroupBy(r => r.PerWebRequestNode.ProjectName);
                Console.WriteLine($"Projects with async-unsafe patterns: {groupedByProject.Count()}");
                Console.WriteLine($"Total dangerous chains found: {results.Count}");
                Console.WriteLine($"Total manual resolution points: {results.Sum(r => r.ManualResolutionPoints.Count)}");

                Console.WriteLine("\nBreakdown by project:");
                foreach (var group in groupedByProject.OrderBy(g => g.Key))
                {
                    Console.WriteLine($"  {group.Key}: {group.Count()} dangerous chains");
                }

                Console.WriteLine("\n⚠️  WARNING: These patterns will cause failures when:");
                Console.WriteLine("  - Code runs in async contexts (async/await)");
                Console.WriteLine("  - HttpContext.Current is null");
                Console.WriteLine("  - Background threads or tasks are used");
                Console.WriteLine("\nRecommendation: Refactor to avoid manual resolution of");
                Console.WriteLine("PerWebRequest dependencies through transient chains.");
            }
            else
            {
                Console.WriteLine("✓ No async-unsafe patterns found!");
                if (projectFilter != null)
                {
                    Console.WriteLine($"  Project {projectFilter} is safe for async contexts.");
                }
                else
                {
                    Console.WriteLine("  All projects are safe for async contexts.");
                }
            }

            Console.WriteLine("\n═══════════════════════════════════════════════════════════════");
            Console.WriteLine("ANALYSIS COMPLETE!");
            Console.WriteLine($"Results saved to: {outputPath}");
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
        }
    }
}