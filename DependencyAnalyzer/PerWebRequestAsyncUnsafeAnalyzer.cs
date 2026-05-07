using DependencyAnalyzer.Comparers;
using DependencyAnalyzer.Extensions;
using DependencyAnalyzer.Models;
using DependencyAnalyzer.Parsers;
using Microsoft.CodeAnalysis;
using System.Text;

namespace DependencyAnalyzer
{
    /// <summary>
    /// Analyzes PerWebRequest/Scoped dependencies to find dangerous manual resolution patterns
    /// that will break in async contexts when HttpContext.Current is null.
    /// Finds ANY manual resolution in the consumer chain of PerWebRequest services,
    /// regardless of the consumer's lifetime (Singleton, Transient, etc).
    /// </summary>
    public class PerWebRequestAsyncUnsafeAnalyzer
    {
        private readonly DependencyGraph _dependencyGraph;
        private readonly ManualResolutionParser _manualResolutionParser;
        private readonly FullyQualifiedNameComparer _comparer = new FullyQualifiedNameComparer();

        public PerWebRequestAsyncUnsafeAnalyzer(
            DependencyGraph dependencyGraph,
            ManualResolutionParser manualResolutionParser)
        {
            _dependencyGraph = dependencyGraph;
            _manualResolutionParser = manualResolutionParser;
        }

        public List<DangerousAsyncChain> Analyze(string? projectFilter = null)
        {
            Console.WriteLine($"[PerWebRequestAsyncUnsafeAnalyzer] Starting analysis...");

            var results = new List<DangerousAsyncChain>();

            // Get all PerWebRequest nodes in the specified project (or all projects)
            var perWebRequestNodes = _dependencyGraph.Nodes
                .Where(n => n.Lifetime == LifetimeTypes.PerWebRequest)
                .Where(n => projectFilter == null || n.ProjectName == projectFilter)
                .ToList();

            Console.WriteLine($"[PerWebRequestAsyncUnsafeAnalyzer] Found {perWebRequestNodes.Count} PerWebRequest nodes to analyze");

            foreach (var perWebNode in perWebRequestNodes)
            {
                var chains = FindDangerousChains(perWebNode);
                results.AddRange(chains);
            }

            Console.WriteLine($"[PerWebRequestAsyncUnsafeAnalyzer] Found {results.Count} dangerous async chains");
            return results;
        }

        private List<DangerousAsyncChain> FindDangerousChains(DependencyNode perWebNode)
        {
            var dangerousChains = new List<DangerousAsyncChain>();
            var visitedNodes = new HashSet<DependencyNode>();
            var currentPath = new List<ChainNode>();

            // Start traversal from the PerWebRequest node
            TraverseConsumers(perWebNode, currentPath, visitedNodes, dangerousChains);

            return dangerousChains;
        }

        private void TraverseConsumers(
            DependencyNode currentNode,
            List<ChainNode> currentPath,
            HashSet<DependencyNode> visitedNodes,
            List<DangerousAsyncChain> dangerousChains)
        {
            // Prevent cycles
            if (visitedNodes.Contains(currentNode))
                return;

            visitedNodes.Add(currentNode);

            // Add current node to path
            var chainNode = new ChainNode
            {
                Node = currentNode,
                ManualResolutionSource = null,
                ResolvingParent = null
            };
            currentPath.Add(chainNode);

            // Continue traversing UP through ALL consumers (not just transients!)
            // Any manual resolution in the chain is dangerous, regardless of lifetime
            foreach (var consumer in currentNode.DependedOnBy)
            {
                // Check how the consumer resolves this dependency (currentNode)
                var rawDep = consumer.GetRawDependency(currentNode);

                // Check if it's a manual resolution
                if (rawDep.Source == DependencySource.Manual_Local ||
                    rawDep.Source == DependencySource.Manual_Stored ||
                    rawDep.Source == DependencySource.Manual_Ambiguous)
                {
                    // Mark this node as being manually resolved by its consumer
                    chainNode.ManualResolutionSource = rawDep.Source;
                    chainNode.ResolvingParent = consumer;

                    // Record the dangerous chain
                    var chainKey = string.Join("->", currentPath.Select(cn => cn.Node.ClassName));
                    if (!dangerousChains.Any(dc => dc.ChainKey == chainKey))
                    {
                        dangerousChains.Add(new DangerousAsyncChain
                        {
                            ChainKey = chainKey,
                            PerWebRequestNode = currentPath.First().Node,
                            Chain = new List<ChainNode>(currentPath),
                            ManualResolutionPoints = currentPath
                                .Where(cn => cn.ManualResolutionSource != null)
                                .ToList()
                        });
                    }
                }

                // Continue traversing up the chain regardless of lifetime
                TraverseConsumers(consumer, currentPath, visitedNodes, dangerousChains);
            }

            // Backtrack
            currentPath.RemoveAt(currentPath.Count - 1);
            visitedNodes.Remove(currentNode);
        }


        public void GenerateConsoleReport(List<DangerousAsyncChain> results)
        {
            Console.WriteLine($"\n{'='*100}");
            Console.WriteLine($"DANGEROUS ASYNC MANUAL RESOLUTION CHAINS");
            Console.WriteLine($"{'='*100}");
            Console.WriteLine($"Found {results.Count} chains that will break when HttpContext.Current is null in async contexts\n");

            var groupedByProject = results.GroupBy(r => r.PerWebRequestNode.ProjectName);

            foreach (var projectGroup in groupedByProject)
            {
                Console.WriteLine($"\n{'-'*100}");
                Console.WriteLine($"PROJECT: {projectGroup.Key}");
                Console.WriteLine($"Dangerous chains: {projectGroup.Count()}");
                Console.WriteLine($"{'-'*100}");

                foreach (var chain in projectGroup)
                {
                    Console.WriteLine($"\nPerWebRequest Root: {chain.PerWebRequestNode.ClassName}");
                    Console.WriteLine($"Chain Length: {chain.Chain.Count}");
                    Console.WriteLine($"Manual Resolution Points: {chain.ManualResolutionPoints.Count}");

                    Console.WriteLine("\nDependency Chain (bottom to top):");
                    for (int i = 0; i < chain.Chain.Count; i++)
                    {
                        var node = chain.Chain[i];
                        var indent = new string(' ', i * 2);
                        var marker = "";

                        if (node.ManualResolutionSource != null)
                        {
                            marker = $" [MANUALLY RESOLVED by {node.ResolvingParent?.ClassName ?? "unknown"}]";
                        }

                        Console.WriteLine($"{indent}└─ {node.Node.ClassName} ({node.Node.Lifetime}){marker}");

                        if (node.ManualResolutionSource != null)
                        {
                            Console.WriteLine($"{indent}   📍 Resolution Type: {node.ManualResolutionSource}");
                            if (node.ResolvingParent != null)
                            {
                                Console.WriteLine($"{indent}   📍 Resolved by: {node.ResolvingParent.ClassName} in {node.ResolvingParent.ProjectName}");

                                // Find the actual manual resolution info if available
                                var manualResolve = _manualResolutionParser.ManuallyResolvedSymbols
                                    .FirstOrDefault(mr =>
                                        _comparer.Equals(mr.ContainingType, node.ResolvingParent.ImplementationType) &&
                                        (node.Node.SatisfiesDependency(mr.ResolvedType)));

                                if (manualResolve != null)
                                {
                                    Console.WriteLine($"{indent}      Location: {manualResolve.InvocationPath}");
                                    Console.WriteLine($"{indent}      Code: {manualResolve.CodeSnippet.Replace("\n", " ").Trim()}");
                                }
                            }
                        }
                    }
                }
            }

            Console.WriteLine($"\n{'='*100}");
        }

        public void GenerateCsvReport(List<DangerousAsyncChain> results, string outputPath)
        {
            using var writer = new StreamWriter(outputPath);

            // Header
            writer.WriteLine("Project,Manual Resolution Location,Dangerous PerWebRequest Object,Dependency Chain from Resolved to Dangerous");

            foreach (var chain in results)
            {
                // Only output rows for nodes that are actually manually resolved
                foreach (var chainNode in chain.ManualResolutionPoints)
                {
                    // Find the actual manual resolution info if available
                    var manualResolve = chainNode.ResolvingParent != null
                        ? _manualResolutionParser.ManuallyResolvedSymbols
                            .FirstOrDefault(mr =>
                                _comparer.Equals(mr.ContainingType, chainNode.ResolvingParent.ImplementationType) &&
                                (chainNode.Node.SatisfiesDependency(mr.ResolvedType)))
                        : null;

                    // Build location string with code snippet
                    var locationWithCode = manualResolve != null
                        ? $"{manualResolve.InvocationPath} - {manualResolve.CodeSnippet.Replace("\r", "").Replace("\n", " ").Trim()}"
                        : $"{chainNode.ResolvingParent?.ProjectName}:{chainNode.ResolvingParent?.ClassName} - {chainNode.ManualResolutionSource}";

                    // Build the dependency chain from manually resolved node DOWN to PerWebRequest
                    // The chain is stored from PerWebRequest UP, so we need to reverse the relevant portion
                    var nodeIndex = chain.Chain.IndexOf(chainNode);
                    var chainFromResolvedToPerWeb = string.Join(" -> ",
                        chain.Chain.Take(nodeIndex + 1).Reverse().Select(cn => cn.Node.ClassName));

                    // The dangerous object is the PerWebRequest node at the bottom of the chain
                    var dangerousObject = chain.PerWebRequestNode.ClassName;

                    writer.WriteLine($"\"{chain.PerWebRequestNode.ProjectName}\"," +
                        $"\"{EscapeCsv(locationWithCode)}\"," +
                        $"\"{dangerousObject}\"," +
                        $"\"{chainFromResolvedToPerWeb}\"");
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

    public class DangerousAsyncChain
    {
        public required string ChainKey { get; init; }
        public required DependencyNode PerWebRequestNode { get; init; }
        public required List<ChainNode> Chain { get; init; }
        public required List<ChainNode> ManualResolutionPoints { get; init; }
    }

    public class ChainNode
    {
        public required DependencyNode Node { get; init; }
        public DependencySource? ManualResolutionSource { get; set; }
        public DependencyNode? ResolvingParent { get; set; }
    }
}