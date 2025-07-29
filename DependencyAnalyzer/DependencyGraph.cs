using Microsoft.CodeAnalysis;

namespace DependencyAnalyzer
{
    public class DependencyGraph
    {
        public List<DependencyNode> Nodes;
        public DependencyGraph(List<DependencyNode> nodes) { 
            Nodes = nodes;
        }
    }
}
