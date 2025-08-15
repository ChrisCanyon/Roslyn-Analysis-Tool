namespace DependencyAnalyzer.Models
{
    public class DependencyGraph
    {
        public List<DependencyNode> Nodes;
        public DependencyGraph(List<DependencyNode> nodes)
        {
            Nodes = nodes;
        }
    }
}
