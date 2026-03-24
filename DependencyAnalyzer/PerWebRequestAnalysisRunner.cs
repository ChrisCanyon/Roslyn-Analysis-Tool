using DependencyAnalyzer.Parsers;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;

namespace DependencyAnalyzer
{
    public static class PerWebRequestAnalysisRunner
    {
        public static async Task Run(string solutionPath, string outputDirectory)
        {
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
            Console.WriteLine("PERWEBREQUEST MANUAL RESOLUTION ANALYZER");
            Console.WriteLine("═══════════════════════════════════════════════════════════════");

            // Register MSBuild instance
            MSBuildLocator.RegisterDefaults();

            // Load the solution
            Console.WriteLine($"Loading solution: {solutionPath}");
            var workspace = MSBuildWorkspace.Create();
            var solution = await workspace.OpenSolutionAsync(solutionPath);
            Console.WriteLine($"Loaded {solution.Projects.Count()} projects");

            // Build analyzers
            Console.WriteLine("Building SolutionAnalyzer...");
            var solutionAnalyzer = await SolutionAnalyzer.Build(solution);
            Console.WriteLine($"Found {solutionAnalyzer.RegistrationInfos.Count} registrations");

            Console.WriteLine("Building ManualResolutionParser...");
            var manualResolutionParser = new ManualResolutionParser(solution, solutionAnalyzer);
            await manualResolutionParser.Build();
            Console.WriteLine($"Found {manualResolutionParser.ManuallyResolvedSymbols.Count} manual resolutions");

            // Run PerWebRequest analyzer
            Console.WriteLine("Running PerWebRequest analyzer...");
            var perWebRequestAnalyzer = new PerWebRequestManualResolutionAnalyzer(solutionAnalyzer, manualResolutionParser);
            var results = perWebRequestAnalyzer.Analyze();

            // Generate console report
            perWebRequestAnalyzer.GenerateConsoleReport(results);

            // Generate CSV report
            string outputPath = Path.Combine(outputDirectory, "PerWebRequest_Manual_Resolutions.csv");
            perWebRequestAnalyzer.GenerateCsvReport(results, outputPath);

            Console.WriteLine("\n═══════════════════════════════════════════════════════════════");
            Console.WriteLine("DONE!");
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
        }
    }
}
