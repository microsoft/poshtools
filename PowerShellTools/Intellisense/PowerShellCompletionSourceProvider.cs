using System.ComponentModel.Composition;
using log4net;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Utilities;

namespace PowerShellTools.Intellisense
{
    [Export(typeof(ICompletionSourceProvider))]
    [ContentType("PowerShell")]
    [Name("PowerShellTokenCompletion")]
    internal class PowerShellCompletionSourceProvider : ICompletionSourceProvider
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(PowerShellCompletionSourceProvider));

        [Import]
        internal ITextStructureNavigatorSelectorService NavigatorService { get; set; }

        [Import]
        internal IGlyphService GlyphService { get; set; }

        public ICompletionSource TryCreateCompletionSource(ITextBuffer textBuffer)
        {
            Log.Debug("TryCreateCompletionSource");
            return new PowerShellCompletionSource(GlyphService);
        }
    }
}
