using Microsoft.CodeAnalysis;

namespace DependencyAnalyzer.Parsers.Windsor
{
    public class WindsorManualResolutionParser : BaseParser, IManualResolutionParser
    {
        private Solution _solution;
        private List<ManualResolutionInfo>? _manualResolutionInfos;

        public WindsorManualResolutionParser(Solution solution)
        {
            _solution = solution;
            _manualResolutionInfos = null;
        }


        public List<ManualResolutionInfo> FindAllManuallyResolvedSymbols()
        {
            if(_manualResolutionInfos != null) { return _manualResolutionInfos; }
            _manualResolutionInfos = new List<ManualResolutionInfo> { };
            
            //TODO actually do stuff
            
            return _manualResolutionInfos;
        }
    }
}
