using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RandomCodeAnalysis
{
    internal class RATWriter
    {
        public static async Task SaveSolutionToDiskAsync(Solution newSolution, Solution oldSolution)
        {
            var changes = newSolution.GetChanges(oldSolution);

            foreach (var projectChange in changes.GetProjectChanges())
            {
                foreach (var docId in projectChange.GetChangedDocuments())
                {
                    var doc = newSolution.GetDocument(docId);
                    if (doc?.FilePath == null)
                        continue;

                    var text = await doc.GetTextAsync().ConfigureAwait(false);
                    File.WriteAllText(doc.FilePath, text.ToString());
                }

                // If you also add/remove docs later:
                // foreach (var docId in projectChange.GetAddedDocuments()) { ... }
                // foreach (var docId in projectChange.GetRemovedDocuments()) { File.Delete(path); }
            }
        }
    }
}
