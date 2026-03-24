using DependencyAnalyzer.Models;
using DependencyAnalyzer.Parsers;
using Microsoft.CodeAnalysis;
using System.Diagnostics;

namespace DependencyAnalyzer
{
    public class PerWebRequestManualResolutionAnalyzer
    {
        private readonly SolutionAnalyzer _solutionAnalyzer;
        private readonly ManualResolutionParser _manualResolutionParser;

        public PerWebRequestManualResolutionAnalyzer(
            SolutionAnalyzer solutionAnalyzer,
            ManualResolutionParser manualResolutionParser)
        {
            _solutionAnalyzer = solutionAnalyzer;
            _manualResolutionParser = manualResolutionParser;
        }

        public List<PerWebRequestManualResolveResult> Analyze()
        {
            Console.WriteLine($"[PerWebRequestAnalyzer] Analyzing {_manualResolutionParser.ManuallyResolvedSymbols.Count} manual resolutions...");

            var results = _manualResolutionParser.ManuallyResolvedSymbols
                .Select(mr => new
                {
                    ManualResolve = mr,
                    PerWebRequestRegistrations = _solutionAnalyzer
                        .GetRegistrationsForSymbol(mr.ResolvedType)
                        .Where(r => r.Lifetime == LifetimeTypes.PerWebRequest)
                        .ToList()
                })
                .Where(x => x.PerWebRequestRegistrations.Any())
                .Select(x => new PerWebRequestManualResolveResult
                {
                    ManualResolveInfo = x.ManualResolve,
                    PerWebRequestRegistrations = x.PerWebRequestRegistrations
                })
                .ToList();

            Console.WriteLine($"[PerWebRequestAnalyzer] Found {results.Count} manual resolutions of PerWebRequest dependencies");
            return results;
        }

        public void GenerateConsoleReport(List<PerWebRequestManualResolveResult> results)
        {
            Console.WriteLine($"\n{'='*80}");
            Console.WriteLine($"PerWebRequest Manual Resolution Analysis Report");
            Console.WriteLine($"{'='*80}");
            Console.WriteLine($"Total manual resolutions of PerWebRequest dependencies: {results.Count}\n");

            foreach (var result in results)
            {
                Console.WriteLine($"\n{'-'*80}");
                Console.WriteLine($"Resolved Type: {result.ManualResolveInfo.ResolvedType.ToDisplayString()}");
                Console.WriteLine($"Project: {result.ManualResolveInfo.Project}");
                Console.WriteLine($"Usage: {result.ManualResolveInfo.Usage}");
                Console.WriteLine($"Location: {result.ManualResolveInfo.InvocationPath}");
                Console.WriteLine($"Code: {result.ManualResolveInfo.CodeSnippet}");

                Console.WriteLine($"\nRegistered as PerWebRequest in:");
                foreach (var reg in result.PerWebRequestRegistrations)
                {
                    Console.WriteLine($"  - Project: {reg.ProjectName}");
                    Console.WriteLine($"    Service: {reg.ServiceType?.ToDisplayString() ?? "self"}");
                    Console.WriteLine($"    Implementation: {reg.ImplementationType?.ToDisplayString() ?? "unknown"}");
                    if (reg.IsFactoryResolved)
                    {
                        Console.WriteLine($"    (Factory Resolved)");
                    }
                }
            }

            Console.WriteLine($"\n{'='*80}");
        }

        public void GenerateCsvReport(List<PerWebRequestManualResolveResult> results, string outputPath)
        {
            using var writer = new StreamWriter(outputPath);

            // Header
            writer.WriteLine("ResolvedType,Project,Usage,Location,CodeSnippet,RegisteredInProject,ServiceType,ImplementationType,IsFactoryResolved");

            foreach (var result in results)
            {
                foreach (var reg in result.PerWebRequestRegistrations)
                {
                    writer.WriteLine($"\"{result.ManualResolveInfo.ResolvedType.ToDisplayString()}\"," +
                        $"\"{result.ManualResolveInfo.Project}\"," +
                        $"\"{result.ManualResolveInfo.Usage}\"," +
                        $"\"{EscapeCsv(result.ManualResolveInfo.InvocationPath)}\"," +
                        $"\"{EscapeCsv(result.ManualResolveInfo.CodeSnippet)}\"," +
                        $"\"{reg.ProjectName}\"," +
                        $"\"{reg.ServiceType?.ToDisplayString() ?? "self"}\"," +
                        $"\"{reg.ImplementationType?.ToDisplayString() ?? "unknown"}\"," +
                        $"\"{reg.IsFactoryResolved}\"");
                }
            }

            Console.WriteLine($"\nCSV report written to: {outputPath}");
        }

        private static string EscapeCsv(string value)
        {
            if (value == null) return string.Empty;
            return value.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ");
        }
    }

    public record PerWebRequestManualResolveResult
    {
        public required ManualResolveInfo ManualResolveInfo { get; init; }
        public required List<RegistrationInfo> PerWebRequestRegistrations { get; init; }
    }
}
