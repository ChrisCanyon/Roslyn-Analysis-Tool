using Microsoft.CodeAnalysis;
using RandomCodeAnalysis.Models.MethodChain;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RandomCodeAnalysis
{
    public class CallChainReporter
    {
        public async Task GenerateAsyncConversionReport(MethodReferenceNode topNode, Solution solution)
        {
            var report = new StringBuilder();
            var stats = new ConversionStats();
            var methodDetails = new List<MethodDetail>();

            report.AppendLine("╔════════════════════════════════════════════════════════════════╗");
            report.AppendLine("║        Async Conversion Analysis Report                       ║");
            report.AppendLine("╔════════════════════════════════════════════════════════════════╗");
            report.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"Top-level method: {topNode.MethodName}");
            report.AppendLine();

            // Analyze the call chain
            await AnalyzeCallChain(topNode, solution, stats, methodDetails, new HashSet<string>());

            // Write summary statistics
            report.AppendLine("═══════════════════════════════════════════════════════════════");
            report.AppendLine("SUMMARY STATISTICS");
            report.AppendLine("═══════════════════════════════════════════════════════════════");
            report.AppendLine($"Total methods to convert:        {stats.TotalMethods}");
            report.AppendLine($"Already async methods:           {stats.AlreadyAsync}");
            report.AppendLine($"Methods needing conversion:      {stats.NeedsConversion}");
            report.AppendLine($"Total call sites to add await:   {stats.TotalCallSites}");
            report.AppendLine($"Unique files affected:           {stats.UniqueFiles.Count}");
            report.AppendLine($"Estimated lines of code:         ~{stats.EstimatedLinesOfCode}");
            report.AppendLine();

            // Write file breakdown
            report.AppendLine("═══════════════════════════════════════════════════════════════");
            report.AppendLine("FILES AFFECTED");
            report.AppendLine("═══════════════════════════════════════════════════════════════");
            foreach (var file in stats.UniqueFiles.OrderBy(f => f))
            {
                var methodsInFile = methodDetails.Where(m => m.FilePath == file).ToList();
                var callSitesInFile = methodDetails.Where(m => m.CallSites.Any(cs => cs.FilePath == file))
                    .Sum(m => m.CallSites.Count(cs => cs.FilePath == file));

                report.AppendLine($"📄 {file}");
                report.AppendLine($"   Methods to convert: {methodsInFile.Count}");
                report.AppendLine($"   Call sites to await: {callSitesInFile}");
                report.AppendLine();
            }

            // Write detailed method list
            report.AppendLine("═══════════════════════════════════════════════════════════════");
            report.AppendLine("METHODS REQUIRING CONVERSION");
            report.AppendLine("═══════════════════════════════════════════════════════════════");

            foreach (var method in methodDetails.Where(m => !m.IsAlreadyAsync).OrderBy(m => m.FilePath).ThenBy(m => m.LineNumber))
            {
                report.AppendLine($"🔧 {method.MethodName}");
                report.AppendLine($"   Location: {method.FilePath}:{method.LineNumber}");
                report.AppendLine($"   Return type: {method.ReturnType}");
                report.AppendLine($"   Call sites to update: {method.CallSites.Count}");

                if (method.CallSites.Any())
                {
                    report.AppendLine($"   Call sites:");
                    foreach (var callSite in method.CallSites.Take(10)) // Limit to first 10
                    {
                        report.AppendLine($"      • {callSite.FilePath}:{callSite.LineNumber}");
                    }
                    if (method.CallSites.Count > 10)
                    {
                        report.AppendLine($"      ... and {method.CallSites.Count - 10} more");
                    }
                }
                report.AppendLine();
            }

            // Write already async methods
            if (methodDetails.Any(m => m.IsAlreadyAsync))
            {
                report.AppendLine("═══════════════════════════════════════════════════════════════");
                report.AppendLine("METHODS ALREADY ASYNC (NO CONVERSION NEEDED)");
                report.AppendLine("═══════════════════════════════════════════════════════════════");

                foreach (var method in methodDetails.Where(m => m.IsAlreadyAsync).OrderBy(m => m.FilePath).ThenBy(m => m.LineNumber))
                {
                    report.AppendLine($"✓ {method.MethodName}");
                    report.AppendLine($"   Location: {method.FilePath}:{method.LineNumber}");
                    report.AppendLine();
                }
            }

            // Write conversion recommendations
            report.AppendLine("═══════════════════════════════════════════════════════════════");
            report.AppendLine("RECOMMENDED CONVERSION APPROACH");
            report.AppendLine("═══════════════════════════════════════════════════════════════");
            report.AppendLine("1. Start with the leaf method (top of this report)");
            report.AppendLine("2. Convert method signature to async Task/Task<T>");
            report.AppendLine("3. Add 'Async' suffix to method name");
            report.AppendLine("4. Let the compiler identify call sites that need updating");
            report.AppendLine("5. Add 'await' to each call site");
            report.AppendLine("6. Move up the call chain to caller methods");
            report.AppendLine("7. Repeat until all methods in the chain are converted");
            report.AppendLine();
            report.AppendLine($"Estimated time: {EstimateConversionTime(stats)} hours");
            report.AppendLine();

            // Write warnings
            if (stats.HasWarnings())
            {
                report.AppendLine("═══════════════════════════════════════════════════════════════");
                report.AppendLine("⚠️  WARNINGS");
                report.AppendLine("═══════════════════════════════════════════════════════════════");
                foreach (var warning in stats.Warnings)
                {
                    report.AppendLine($"⚠️  {warning}");
                }
                report.AppendLine();
            }

            // Write the report to file
            await WriteReportToFile(report.ToString(), solution);
        }

        private async Task AnalyzeCallChain(
            MethodReferenceNode node,
            Solution solution,
            ConversionStats stats,
            List<MethodDetail> methodDetails,
            HashSet<string> visited)
        {
            var methodKey = node.MethodName;
            if (visited.Contains(methodKey))
                return;

            visited.Add(methodKey);

            var method = node.ReferencedMethod;
            stats.TotalMethods++;

            var detail = new MethodDetail
            {
                MethodName = node.MethodName,
                IsAlreadyAsync = method.IsAsync,
                ReturnType = method.ReturnType.ToDisplayString(),
                MethodKind = method.MethodKind.ToString()
            };

            // Get location info
            var location = method.Locations.FirstOrDefault();
            if (location != null && location.IsInSource)
            {
                detail.FilePath = location.SourceTree?.FilePath ?? "Unknown";
                var lineSpan = location.GetLineSpan();
                detail.LineNumber = lineSpan.StartLinePosition.Line + 1;

                if (!string.IsNullOrEmpty(detail.FilePath))
                {
                    stats.UniqueFiles.Add(detail.FilePath);
                }
            }

            if (method.IsAsync)
            {
                stats.AlreadyAsync++;
            }
            else
            {
                stats.NeedsConversion++;
            }

            // Analyze call sites
            stats.TotalCallSites += node.CallSites.Count;
            foreach (var callSite in node.CallSites)
            {
                var callSiteDetail = new CallSiteDetail
                {
                    DocumentId = callSite.DocumentId,
                    Span = callSite.Span
                };

                // Resolve file path and line number from document
                var document = solution.GetDocument(callSite.DocumentId);
                if (document != null)
                {
                    callSiteDetail.FilePath = document.FilePath ?? "Unknown";

                    // Get line number from span
                    var syntaxTree = await document.GetSyntaxTreeAsync();
                    if (syntaxTree != null)
                    {
                        var lineSpan = syntaxTree.GetLineSpan(callSite.Span);
                        callSiteDetail.LineNumber = lineSpan.StartLinePosition.Line + 1;
                    }

                    if (!string.IsNullOrEmpty(callSiteDetail.FilePath))
                    {
                        stats.UniqueFiles.Add(callSiteDetail.FilePath);
                    }
                }

                detail.CallSites.Add(callSiteDetail);
            }

            // Estimate lines of code (rough heuristic: 10 lines per method + 2 per call site)
            stats.EstimatedLinesOfCode += 1 + node.CallSites.Count;

            // Check for potential issues
            if (method.MethodKind != MethodKind.Ordinary && method.MethodKind != MethodKind.LocalFunction)
            {
                stats.Warnings.Add($"Method {detail.MethodName} has unsupported MethodKind: {method.MethodKind}");
            }

            if (method.DeclaringSyntaxReferences.Length == 0)
            {
                stats.Warnings.Add($"Method {detail.MethodName} has no declaring syntax references");
            }

            methodDetails.Add(detail);

            // Recurse to callers
            foreach (var caller in node.CallerNodes)
            {
                await AnalyzeCallChain(caller, solution, stats, methodDetails, visited);
            }
        }

        private double EstimateConversionTime(ConversionStats stats)
        {
            // Rough estimate: 5 minutes per method + 30 seconds per call site
            double methodTime = stats.NeedsConversion * 5.0;
            double callSiteTime = stats.TotalCallSites * 0.5;
            return Math.Round((methodTime + callSiteTime) / 60.0, 1);
        }

        private async Task WriteReportToFile(string reportContent, Solution solution)
        {
            try
            {
                var solutionDir = Path.GetDirectoryName(solution.FilePath);
                if (string.IsNullOrEmpty(solutionDir))
                {
                    solutionDir = Directory.GetCurrentDirectory();
                }

                var reportFileName = $"AsyncConversionReport_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                var reportFilePath = Path.Combine(solutionDir, reportFileName);

                await File.WriteAllTextAsync(reportFilePath, reportContent);
                Console.WriteLine($"╔════════════════════════════════════════════════════════════════╗");
                Console.WriteLine($"║ Report generated successfully!                                ║");
                Console.WriteLine($"╚════════════════════════════════════════════════════════════════╝");
                Console.WriteLine($"Location: {reportFilePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to write report: {ex.Message}");
            }
        }
    }

    internal class ConversionStats
    {
        public int TotalMethods { get; set; }
        public int AlreadyAsync { get; set; }
        public int NeedsConversion { get; set; }
        public int TotalCallSites { get; set; }
        public int EstimatedLinesOfCode { get; set; }
        public HashSet<string> UniqueFiles { get; } = new HashSet<string>();
        public List<string> Warnings { get; } = new List<string>();

        public bool HasWarnings() => Warnings.Any();
    }

    internal class MethodDetail
    {
        public string MethodName { get; set; } = "";
        public string FilePath { get; set; } = "";
        public int LineNumber { get; set; }
        public bool IsAlreadyAsync { get; set; }
        public string ReturnType { get; set; } = "";
        public string MethodKind { get; set; } = "";
        public List<CallSiteDetail> CallSites { get; } = new List<CallSiteDetail>();
    }

    internal class CallSiteDetail
    {
        public DocumentId DocumentId { get; set; } = null!;
        public Microsoft.CodeAnalysis.Text.TextSpan Span { get; set; }
        public string FilePath { get; set; } = "";
        public int LineNumber { get; set; }
    }
}
