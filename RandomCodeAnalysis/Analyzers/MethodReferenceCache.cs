using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using System.Collections.Concurrent;

namespace RandomCodeAnalysis.Analyzers
{
    public class MethodReferenceCache
    {
        private readonly ConcurrentDictionary<IMethodSymbol, IEnumerable<ReferencedSymbol>> _cache;
        private readonly Solution _solution;

        public MethodReferenceCache(Solution solution)
        {
            _solution = solution;
            _cache = new ConcurrentDictionary<IMethodSymbol, IEnumerable<ReferencedSymbol>>(SymbolEqualityComparer.Default);
        }

        public async Task<IEnumerable<ReferencedSymbol>> FindAllReferencesAsync(IMethodSymbol method)
        {
            if (_cache.TryGetValue(method, out var cached))
            {
                Console.WriteLine($"[CACHE HIT] {method.Name}");
                return cached;
            }

            Console.WriteLine($"[CACHE MISS] Finding references for {method.Name}");
            var references = new List<ReferencedSymbol>();
            references.AddRange(await SymbolFinder.FindReferencesAsync(method, _solution).ConfigureAwait(false));

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
