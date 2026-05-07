using DependencyAnalyzer.Extensions;
using DependencyAnalyzer.Models;
using DependencyAnalyzer.Parsers;
using Microsoft.CodeAnalysis;
using System.Text;

namespace DependencyAnalyzer
{
    /// <summary>
    /// Analyzes the full dependency chain for manually resolved objects to determine the true impact of memory leaks.
    /// When a transient is manually resolved and not released, it leaks not just itself but its entire dependency chain.
    /// </summary>
    public class DependencyChainAnalyzer
    {
        private readonly DependencyGraph _dependencyGraph;
        private readonly ManualResolutionParser _manualResolutionParser;

        public DependencyChainAnalyzer(DependencyGraph dependencyGraph, ManualResolutionParser manualResolutionParser)
        {
            _dependencyGraph = dependencyGraph;
            _manualResolutionParser = manualResolutionParser;
        }

        /// <summary>
        /// Analyzes a manually resolved type and returns detailed information about its entire dependency chain.
        /// </summary>
        public ManualResolutionChainAnalysis AnalyzeManualResolution(ManualResolveInfo resolution)
        {
            var analysis = new ManualResolutionChainAnalysis
            {
                Resolution = resolution,
                ChainDetails = new List<DependencyChainNode>()
            };

            // Find the node in the dependency graph
            var rootNode = FindNodeForType(resolution.ResolvedType);
            if (rootNode == null)
            {
                analysis.IsUnregistered = true;
                return analysis;
            }

            // Check if there's a matching disposal
            var hasRelease = HasMatchingRelease(resolution, rootNode);
            analysis.HasRelease = hasRelease;

            // If it's released or a singleton, it doesn't leak
            if (hasRelease || rootNode.Lifetime == LifetimeTypes.Singleton)
            {
                analysis.IsLeak = false;
                return analysis;
            }

            // Traverse the dependency chain
            var visited = new HashSet<DependencyNode>();
            var chainInfo = TraverseDependencyChain(rootNode, 0, visited);

            analysis.ChainDetails = chainInfo.Nodes;
            analysis.TotalLeakedObjects = chainInfo.TransientCount + chainInfo.PerWebRequestCount;
            analysis.TotalTransients = chainInfo.TransientCount;
            analysis.TotalPerWebRequest = chainInfo.PerWebRequestCount;
            analysis.TotalSingletons = chainInfo.SingletonCount;
            analysis.MaxDepth = chainInfo.MaxDepth;
            analysis.IsLeak = analysis.TotalLeakedObjects > 0;
            analysis.LeakSeverity = CalculateLeakSeverity(analysis);
            analysis.EstimatedMemoryImpact = EstimateMemoryImpact(chainInfo);
            analysis.HasCircularDependencies = chainInfo.HasCircularDependencies;

            return analysis;
        }

        private ChainTraversalResult TraverseDependencyChain(DependencyNode node, int depth, HashSet<DependencyNode> visited)
        {
            var result = new ChainTraversalResult
            {
                Nodes = new List<DependencyChainNode>(),
                MaxDepth = depth
            };

            // Check for circular dependencies
            if (visited.Contains(node))
            {
                result.HasCircularDependencies = true;
                return result;
            }

            visited.Add(node);

            // Add current node to chain
            var chainNode = new DependencyChainNode
            {
                Node = node,
                Depth = depth,
                Lifetime = node.Lifetime,
                ClassName = node.ClassName,
                ProjectName = node.ProjectName,
                IsLeaked = IsLeakedLifetime(node.Lifetime),
                DependencyCount = node.DependsOn.Count
            };
            result.Nodes.Add(chainNode);

            // Count by lifetime
            switch (node.Lifetime)
            {
                case LifetimeTypes.Transient:
                    result.TransientCount++;
                    break;
                case LifetimeTypes.PerWebRequest:
                    result.PerWebRequestCount++;
                    break;
                case LifetimeTypes.Singleton:
                    result.SingletonCount++;
                    // Singletons don't leak, so we can stop traversing their dependencies
                    // However, we might want to continue for analysis purposes
                    break;
            }

            // Traverse dependencies
            foreach (var dependency in node.DependsOn)
            {
                var subResult = TraverseDependencyChain(dependency, depth + 1, visited);

                result.Nodes.AddRange(subResult.Nodes);
                result.TransientCount += subResult.TransientCount;
                result.PerWebRequestCount += subResult.PerWebRequestCount;
                result.SingletonCount += subResult.SingletonCount;
                result.MaxDepth = Math.Max(result.MaxDepth, subResult.MaxDepth);
                result.HasCircularDependencies = result.HasCircularDependencies || subResult.HasCircularDependencies;
            }

            visited.Remove(node); // Allow the node to be visited through different paths
            return result;
        }

        private bool IsLeakedLifetime(LifetimeTypes lifetime)
        {
            return lifetime == LifetimeTypes.Transient ||
                   lifetime == LifetimeTypes.PerWebRequest ||
                   lifetime == LifetimeTypes.Controller; // Controllers are typically transient too
        }

        private LeakSeverity CalculateLeakSeverity(ManualResolutionChainAnalysis analysis)
        {
            // Severity based on number of leaked objects and depth
            if (analysis.TotalLeakedObjects == 0)
                return LeakSeverity.None;
            if (analysis.TotalLeakedObjects == 1 && analysis.MaxDepth <= 1)
                return LeakSeverity.Low;
            if (analysis.TotalLeakedObjects <= 5 && analysis.MaxDepth <= 3)
                return LeakSeverity.Medium;
            if (analysis.TotalLeakedObjects <= 10 && analysis.MaxDepth <= 5)
                return LeakSeverity.High;

            return LeakSeverity.Critical;
        }

        private long EstimateMemoryImpact(ChainTraversalResult chainInfo)
        {
            // Rough estimation: assume each object takes ~1KB base + dependencies
            // This is a very rough estimate and could be refined based on actual type analysis
            long baseObjectSize = 1024; // 1KB per object
            long totalSize = 0;

            foreach (var node in chainInfo.Nodes)
            {
                if (node.IsLeaked)
                {
                    // Larger objects with more dependencies likely use more memory
                    long nodeSize = baseObjectSize * (1 + node.DependencyCount / 10);
                    totalSize += nodeSize;
                }
            }

            return totalSize;
        }

        private DependencyNode? FindNodeForType(INamedTypeSymbol type)
        {
            return _dependencyGraph.Nodes.FirstOrDefault(n =>
                SymbolEqualityComparer.Default.Equals(n.ImplementationType, type) ||
                (n.ServiceInterface != null && SymbolEqualityComparer.Default.Equals(n.ServiceInterface, type)));
        }

        private bool HasMatchingRelease(ManualResolveInfo resolution, DependencyNode node)
        {
            if (_manualResolutionParser.ManuallyDisposedSymbols == null)
                return false;

            return _manualResolutionParser.ManuallyDisposedSymbols.Any(disposal =>
                disposal.Project == resolution.Project &&
                node.SatisfiesDependency(disposal.DisposedType));
        }

        /// <summary>
        /// Generates a comprehensive report for all manual resolutions showing their dependency chains.
        /// </summary>
        public string GenerateChainAnalysisReport(List<ManualResolveInfo> resolutions)
        {
            var sb = new StringBuilder();
            var analyses = resolutions.Select(r => AnalyzeManualResolution(r)).ToList();

            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine("DEPENDENCY CHAIN ANALYSIS FOR MANUAL RESOLUTIONS");
            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine();

            // Group by severity
            var bySeverity = analyses.GroupBy(a => a.LeakSeverity).OrderByDescending(g => g.Key);

            foreach (var severityGroup in bySeverity)
            {
                if (severityGroup.Key == LeakSeverity.None) continue;

                sb.AppendLine($"### {severityGroup.Key} SEVERITY LEAKS ###");
                sb.AppendLine();

                foreach (var analysis in severityGroup.OrderByDescending(a => a.TotalLeakedObjects))
                {
                    sb.AppendLine($"Type: {analysis.Resolution.ResolvedType.Name}");
                    sb.AppendLine($"Location: {analysis.Resolution.InvocationPath}");
                    sb.AppendLine($"Project: {analysis.Resolution.Project}");
                    sb.AppendLine($"Usage: {analysis.Resolution.Usage}");

                    if (analysis.IsUnregistered)
                    {
                        sb.AppendLine("⚠️ WARNING: Type is not registered in DI container!");
                    }
                    else
                    {
                        sb.AppendLine($"Leaked Objects: {analysis.TotalLeakedObjects} ({analysis.TotalTransients} transient, {analysis.TotalPerWebRequest} per-request)");
                        sb.AppendLine($"Chain Depth: {analysis.MaxDepth}");
                        sb.AppendLine($"Estimated Memory Impact: {analysis.EstimatedMemoryImpact / 1024.0:F1} KB per instance");

                        if (analysis.HasCircularDependencies)
                        {
                            sb.AppendLine("⚠️ CIRCULAR DEPENDENCIES DETECTED!");
                        }

                        // Show the dependency chain
                        sb.AppendLine("Dependency Chain:");
                        var chainByDepth = analysis.ChainDetails.GroupBy(c => c.Depth).OrderBy(g => g.Key);
                        foreach (var depthGroup in chainByDepth)
                        {
                            foreach (var chainNode in depthGroup)
                            {
                                var indent = new string(' ', chainNode.Depth * 2);
                                var leakIndicator = chainNode.IsLeaked ? "❌" : "✓";
                                sb.AppendLine($"{indent}{leakIndicator} {chainNode.ClassName} ({chainNode.Lifetime}) - {chainNode.ProjectName}");
                            }
                        }
                    }

                    sb.AppendLine();
                }
            }

            // Summary statistics
            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine("SUMMARY");
            sb.AppendLine("═══════════════════════════════════════════════════════════════");

            var leaks = analyses.Where(a => a.IsLeak).ToList();
            sb.AppendLine($"Total Manual Resolutions: {analyses.Count}");
            sb.AppendLine($"Total Leaks: {leaks.Count}");
            sb.AppendLine($"Critical Severity: {leaks.Count(a => a.LeakSeverity == LeakSeverity.Critical)}");
            sb.AppendLine($"High Severity: {leaks.Count(a => a.LeakSeverity == LeakSeverity.High)}");
            sb.AppendLine($"Medium Severity: {leaks.Count(a => a.LeakSeverity == LeakSeverity.Medium)}");
            sb.AppendLine($"Low Severity: {leaks.Count(a => a.LeakSeverity == LeakSeverity.Low)}");
            sb.AppendLine();

            if (leaks.Any())
            {
                var totalLeakedObjects = leaks.Sum(a => a.TotalLeakedObjects);
                var avgLeakedPerResolution = totalLeakedObjects / (double)leaks.Count;
                var maxChainDepth = leaks.Max(a => a.MaxDepth);
                var totalMemoryImpact = leaks.Sum(a => a.EstimatedMemoryImpact);

                sb.AppendLine($"Total Leaked Objects Across All Resolutions: {totalLeakedObjects}");
                sb.AppendLine($"Average Leaked Objects Per Resolution: {avgLeakedPerResolution:F1}");
                sb.AppendLine($"Maximum Chain Depth: {maxChainDepth}");
                sb.AppendLine($"Total Estimated Memory Impact: {totalMemoryImpact / 1024.0:F1} KB per instance");

                // Top offenders
                sb.AppendLine();
                sb.AppendLine("TOP 5 WORST OFFENDERS (by leaked object count):");
                foreach (var leak in leaks.OrderByDescending(a => a.TotalLeakedObjects).Take(5))
                {
                    sb.AppendLine($"  - {leak.Resolution.ResolvedType.Name}: {leak.TotalLeakedObjects} objects, depth {leak.MaxDepth}");
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Generates a CSV report with detailed chain analysis data for Excel/data analysis.
        /// </summary>
        public void GenerateChainAnalysisCsv(List<ManualResolveInfo> resolutions, string outputPath)
        {
            var sb = new StringBuilder();

            // Header
            sb.AppendLine("Project,Type,Location,Usage,Has Release,Leak Severity,Total Leaked Objects,Transients,Per-Request,Singletons,Max Depth,Estimated Memory (KB),Has Circular Deps,Direct Dependencies,Full Chain");

            var analyses = resolutions.Select(r => AnalyzeManualResolution(r)).ToList();

            foreach (var analysis in analyses.OrderByDescending(a => a.TotalLeakedObjects))
            {
                // Build the full chain string
                var chainString = string.Join(" -> ",
                    analysis.ChainDetails
                        .OrderBy(c => c.Depth)
                        .Select(c => $"{c.ClassName}({c.Lifetime})")
                );

                // Count direct dependencies (depth 1)
                var directDeps = analysis.ChainDetails.Count(c => c.Depth == 1);

                sb.AppendLine($"\"{analysis.Resolution.Project}\"," +
                    $"\"{analysis.Resolution.ResolvedType.Name}\"," +
                    $"\"{EscapeCsvValue(analysis.Resolution.InvocationPath)}\"," +
                    $"\"{analysis.Resolution.Usage}\"," +
                    $"\"{(analysis.HasRelease ? "Yes" : "No")}\"," +
                    $"\"{analysis.LeakSeverity}\"," +
                    $"{analysis.TotalLeakedObjects}," +
                    $"{analysis.TotalTransients}," +
                    $"{analysis.TotalPerWebRequest}," +
                    $"{analysis.TotalSingletons}," +
                    $"{analysis.MaxDepth}," +
                    $"{analysis.EstimatedMemoryImpact / 1024.0:F1}," +
                    $"\"{(analysis.HasCircularDependencies ? "Yes" : "No")}\"," +
                    $"{directDeps}," +
                    $"\"{EscapeCsvValue(chainString)}\"");
            }

            File.WriteAllText(outputPath, sb.ToString());
        }

        private string EscapeCsvValue(string value)
        {
            if (value.Contains("\"") || value.Contains(",") || value.Contains("\n"))
            {
                return value.Replace("\"", "\"\"");
            }
            return value;
        }
    }

    #region Supporting Classes

    public class ManualResolutionChainAnalysis
    {
        public ManualResolveInfo Resolution { get; set; }
        public bool IsLeak { get; set; }
        public bool HasRelease { get; set; }
        public bool IsUnregistered { get; set; }
        public int TotalLeakedObjects { get; set; }
        public int TotalTransients { get; set; }
        public int TotalPerWebRequest { get; set; }
        public int TotalSingletons { get; set; }
        public int MaxDepth { get; set; }
        public LeakSeverity LeakSeverity { get; set; }
        public long EstimatedMemoryImpact { get; set; }
        public bool HasCircularDependencies { get; set; }
        public List<DependencyChainNode> ChainDetails { get; set; } = new List<DependencyChainNode>();
    }

    public class DependencyChainNode
    {
        public DependencyNode Node { get; set; }
        public int Depth { get; set; }
        public LifetimeTypes Lifetime { get; set; }
        public string ClassName { get; set; }
        public string ProjectName { get; set; }
        public bool IsLeaked { get; set; }
        public int DependencyCount { get; set; }
    }

    public enum LeakSeverity
    {
        None = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        Critical = 4
    }

    internal class ChainTraversalResult
    {
        public List<DependencyChainNode> Nodes { get; set; } = new List<DependencyChainNode>();
        public int TransientCount { get; set; }
        public int PerWebRequestCount { get; set; }
        public int SingletonCount { get; set; }
        public int MaxDepth { get; set; }
        public bool HasCircularDependencies { get; set; }
    }

    #endregion
}