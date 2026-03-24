using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using RandomCodeAnalysis;
using RandomCodeAnalysis.Analyzers;
using System.Diagnostics;

namespace DependencyAnalyzer
{
    public static class AsyncCallChainAnalysisRunner
    {
        public static async Task Run(
            string solutionPath,
            string fullyQualifiedTypeName,
            string methodName,
            bool shallow = false)
        {
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
            Console.WriteLine("ASYNC CALL CHAIN ANALYZER");
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
            Console.WriteLine($"Target: {fullyQualifiedTypeName}.{methodName}");
            Console.WriteLine($"Mode: {(shallow ? "Shallow" : "Deep")}");
            Console.WriteLine();

            // Register MSBuild instance
            MSBuildLocator.RegisterDefaults();

            // Load the solution
            var stopwatch = Stopwatch.StartNew();
            using var workspace = MSBuildWorkspace.Create();
            var solution = await workspace.OpenSolutionAsync(solutionPath);
            stopwatch.Stop();
            Console.WriteLine($"Loaded solution in {stopwatch.ElapsedMilliseconds}ms ({stopwatch.Elapsed.TotalMinutes:F2} minutes)");
            Console.WriteLine($"Projects: {solution.Projects.Count()}");
            Console.WriteLine();

            // Analyze call chain
            stopwatch.Restart();
            var analyzer = new CallChainAnalyzer(solution);
            var topNode = await analyzer.FindFullMethodChain(fullyQualifiedTypeName, methodName, shallow);
            stopwatch.Stop();
            Console.WriteLine($"Call chain analysis completed in {stopwatch.ElapsedMilliseconds}ms ({stopwatch.Elapsed.TotalMinutes:F2} minutes)");
            Console.WriteLine();

            // Generate report
            var reporter = new CallChainReporter();
            await reporter.GenerateAsyncConversionReport(topNode, solution);

            Console.WriteLine("\n═══════════════════════════════════════════════════════════════");
            Console.WriteLine("DONE!");
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
        }
    }
}
