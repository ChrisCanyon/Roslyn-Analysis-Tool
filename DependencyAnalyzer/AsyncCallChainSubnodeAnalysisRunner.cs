using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using RandomCodeAnalysis;
using RandomCodeAnalysis.Analyzers;
using System.Diagnostics;

namespace DependencyAnalyzer
{
    public static class AsyncCallChainSubnodeAnalysisRunner
    {
        /// <summary>
        /// Analyzes a method and all of its direct callers (subnodes) in parallel,
        /// generating separate async conversion reports for each.
        /// </summary>
        public static async Task Run(
            string solutionPath,
            string fullyQualifiedTypeName,
            string methodName,
            bool shallow = false)
        {
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
            Console.WriteLine("ASYNC CALL CHAIN SUBNODE ANALYZER");
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

            // Analyze main call chain
            stopwatch.Restart();
            var analyzer = new CallChainAnalyzer(solution);
            var topNode = await analyzer.FindFullMethodChain(fullyQualifiedTypeName, methodName, shallow);
            stopwatch.Stop();
            Console.WriteLine($"Main call chain analysis completed in {stopwatch.ElapsedMilliseconds}ms ({stopwatch.Elapsed.TotalMinutes:F2} minutes)");
            Console.WriteLine($"Found {topNode.CallerNodes.Count} direct callers to analyze");
            Console.WriteLine();

            // Generate report for the main method
            var reporter = new CallChainReporter();
            await reporter.GenerateAsyncConversionReport(topNode, solution);

            // Analyze each direct caller (subnode) in parallel
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
            Console.WriteLine("ANALYZING SUBNODES (DIRECT CALLERS)");
            Console.WriteLine("═══════════════════════════════════════════════════════════════");

            var subnodeTasks = topNode.CallerNodes.Select(callerNode => Task.Run(async () =>
            {
                var method = callerNode.ReferencedMethod;

                var fullyQualifiedTypeNameLocal = method.ContainingType
                    .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    .Replace("global::", "");

                var methodNameLocal = method.Name;

                Console.WriteLine($"[START] Analyzing: {fullyQualifiedTypeNameLocal}.{methodNameLocal}");

                var localStopwatch = Stopwatch.StartNew();
                var result = await analyzer.FindFullMethodChain(fullyQualifiedTypeNameLocal, methodNameLocal, shallow);
                await reporter.GenerateAsyncConversionReport(result, solution);
                localStopwatch.Stop();

                Console.WriteLine($"[COMPLETE] {fullyQualifiedTypeNameLocal}.{methodNameLocal}");
                Console.WriteLine($"           Elapsed: {localStopwatch.ElapsedMilliseconds}ms ({localStopwatch.Elapsed.TotalMinutes:F2} minutes)");

                return result;
            })).ToList();

            await Task.WhenAll(subnodeTasks);

            Console.WriteLine("\n═══════════════════════════════════════════════════════════════");
            Console.WriteLine($"DONE! Analyzed 1 main method + {topNode.CallerNodes.Count} subnodes");
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
        }
    }
}
