using System.Collections.Generic;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace PowerShellTools.Intellisense
{
    /// <summary>
    /// Class that is used when cycling through completions using the tab command
    /// </summary>
    internal class TabCompleteSession
    {
        private IList<Completion> _completions;
        private int _index;
        private bool _isInitialized;

        /// <summary>
        /// Creates an uninitialized TabCompleteSession
        /// </summary>
        public TabCompleteSession()
        {
            _isInitialized = false;
        }

        /// <summary>
        /// Creates and initializes the TabCompleteSession
        /// </summary>
        /// <param name="activeSession">The active session</param>
        public TabCompleteSession(ICompletionSession activeSession)
        {
            Initialize(activeSession);
        }

        /// <summary>
        /// Returns true if the session has been initialized, otherwise false
        /// </summary>
        public bool IsInitialized
        {
            get
            {
                return _isInitialized;
            }
        }

        /// <summary>
        /// Initializes the TabCompleteSession with Intellisense information
        /// </summary>
        /// <param name="activeSession">The active session</param>
        public void Initialize(ICompletionSession activeSession)
        {
            _completions = activeSession.SelectedCompletionSet.Completions;
            _index = _completions.IndexOf(activeSession.SelectedCompletionSet.SelectionStatus.Completion);

            activeSession.Commit();

            _isInitialized = true;
        }

        /// <summary>
        /// Replaces the previous completion with the next completion if one exists
        /// </summary>
        /// <param name="textView">The text view in which to replace the token</param>
        /// <returns>True if the replace occured, false if there were no more completions</returns>
        public bool ReplaceWithNextCompletion(ITextView textView)
        {
            _index++;

            if (_completions == null || _index >= _completions.Count)
            {
                return false;
            }

            var replacementText = _completions[_index].InsertionText;
            var previousCompletionLength = _completions[_index - 1].InsertionText.Length;
            var replacementPosition = textView.Caret.Position.BufferPosition.Position - previousCompletionLength;
            textView.TextBuffer.Replace(new Span(replacementPosition, previousCompletionLength), replacementText);

            return true;
        }
    }
}
