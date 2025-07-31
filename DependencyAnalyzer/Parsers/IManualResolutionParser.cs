namespace DependencyAnalyzer.Parsers
{
    public interface IManualResolutionParser
    {
        public List<ManualResolutionInfo> FindAllManuallyResolvedSymbols();
    }
}
