using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using System.Collections.Concurrent;

namespace RandomCodeAnalysis.Analyzers
{
    public class MethodReferenceCache
    {
        private readonly ConcurrentDictionary<IMethodSymbol, IEnumerable<ReferencedSymbol>> _cache;
        private readonly Solution _solution;
        private readonly Solution _filteredSolution;

        public MethodReferenceCache(Solution solution)
        {
            _solution = solution;
            _cache = new ConcurrentDictionary<IMethodSymbol, IEnumerable<ReferencedSymbol>>(SymbolEqualityComparer.Default);

            // Create filtered solution excluding test projects for faster searching
            var filteredSolution = solution;
            var testProjects = solution.Projects
                .Where(p => p.Name.Contains("test", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var testProject in testProjects)
            {
                filteredSolution = filteredSolution.RemoveProject(testProject.Id);
            }

            _filteredSolution = filteredSolution;

            if (testProjects.Any())
            {
                Console.WriteLine($"[MethodReferenceCache] Excluding {testProjects.Count} test projects from search: {string.Join(", ", testProjects.Select(p => p.Name))}");
            }
        }

        public async Task<IEnumerable<ReferencedSymbol>> FindAllReferencesAsync(IMethodSymbol method)
        {
            if (_cache.TryGetValue(method, out var cached))
            {
                Console.WriteLine($"[CACHE HIT] {method.ContainingType?.Name}.{method.Name}");
                return cached;
            }

            Console.WriteLine($"[CACHE MISS] Finding references for {method.ContainingType?.Name}.{method.Name}...");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var references = new List<ReferencedSymbol>();
            references.AddRange(await SymbolFinder.FindReferencesAsync(method, _filteredSolution).ConfigureAwait(false));
            stopwatch.Stop();

            Console.WriteLine($"[CACHE MISS] Found {references.Sum(r => r.Locations.Count())} locations for {method.ContainingType?.Name}.{method.Name} in {stopwatch.ElapsedMilliseconds}ms ({stopwatch.Elapsed.TotalSeconds:F1}s)");

            _cache[method] = references;
            return references;
        }

        public async Task<IEnumerable<ReferencedSymbol>> FindAllReferencesAsync(IEnumerable<IMethodSymbol> methods)
        {
            var referenceTasks = methods.Select(method =>
                Task.Run(() => FindAllReferencesAsync(method))
            ).ToList();

            var allReferences = await Task.WhenAll(referenceTasks).ConfigureAwait(false);

            var ret = new List<ReferencedSymbol>();
            foreach (var references in allReferences)
            {
                ret.AddRange(references);
            }
            return ret;
        }

        public int CacheSize => _cache.Count;
    }
}
