using Microsoft.CodeAnalysis;

namespace DependencyAnalyzer.Parsers.MicrosoftDI
{
    public class MDIManualResolutionParser : BaseParser, IManualResolutionParser
    {
        Solution _solution;
        private List<ManualResolutionInfo>? _manualResolutionInfos;

        public MDIManualResolutionParser(Solution solution)
        {
            _solution = solution;
            _manualResolutionInfos = null;
        }

        public List<ManualResolutionInfo> FindAllManuallyResolvedSymbols()
        {
            if (_manualResolutionInfos != null) { return _manualResolutionInfos; }
            _manualResolutionInfos = new List<ManualResolutionInfo> { };

            //TODO actually do stuff

            return _manualResolutionInfos;
        }
    }
}
