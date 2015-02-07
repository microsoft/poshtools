using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management.Automation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using log4net;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Repl;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using PowerShellTools.Classification;
using PowerShellTools.Common.ServiceManagement.IntelliSenseContract;

namespace PowerShellTools.Intellisense
{
    /// <summary>
    /// Class that is used for both the editor and the REPL window to manage the completion sources and
    /// completion session in the ITextBuffers. 
    /// </summary>
    internal class IntelliSenseManager
    {
        internal IOleCommandTarget NextCommandHandler;
        private readonly ITextView _textView;
        private readonly ICompletionBroker _broker;
        private ICompletionSession _activeSession;
        private readonly SVsServiceProvider _serviceProvider;
        private static readonly ILog Log = LogManager.GetLogger(typeof(IntelliSenseManager));
        private readonly bool _isRepl;
        private bool _intellisenseRunning;

        public IntelliSenseManager(ICompletionBroker broker, SVsServiceProvider provider, IOleCommandTarget commandHandler, ITextView textView)
        {
            _broker = broker;
            NextCommandHandler = commandHandler;
            _textView = textView;
            _isRepl = _textView.Properties.ContainsProperty(BufferProperties.FromRepl);
            _serviceProvider = provider;
        }
        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            return NextCommandHandler.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
        }

        /// <summary>
        /// Main method used to determine how to handle keystrokes within a ITextBuffer.
        /// </summary>
        /// <param name="pguidCmdGroup"></param>
        /// <param name="nCmdId"></param>
        /// <param name="nCmdexecopt"></param>
        /// <param name="pvaIn"></param>
        /// <param name="pvaOut"></param>
        /// <returns></returns>
        public int Exec(ref Guid pguidCmdGroup, uint nCmdId, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            if (VsShellUtilities.IsInAutomationFunction(_serviceProvider))
            {
                return NextCommandHandler.Exec(ref pguidCmdGroup, nCmdId, nCmdexecopt, pvaIn, pvaOut);
            }
            //make a copy of this so we can look at it after forwarding some commands 
            var commandId = nCmdId;
            var typedChar = char.MinValue;
            //make sure the input is a char before getting it 
            if (pguidCmdGroup == VSConstants.VSStd2K && nCmdId == (uint)VSConstants.VSStd2KCmdID.TYPECHAR)
            {
                typedChar = (char)(ushort)Marshal.GetObjectForNativeVariant(pvaIn);
            }

            Log.DebugFormat("Typed Character: {0}", typedChar);

            //check for a commit character 
            if (nCmdId == (uint)VSConstants.VSStd2KCmdID.RETURN
                || nCmdId == (uint)VSConstants.VSStd2KCmdID.TAB
                || (char.IsWhiteSpace(typedChar)))
            {
                //check for a a selection 
                if (_activeSession != null && !_activeSession.IsDismissed)
                {
                    //if the selection is fully selected, commit the current session 
                    if (_activeSession.SelectedCompletionSet.SelectionStatus.IsSelected)
                    {
                        Log.Debug("Commit");
                        _activeSession.Commit();

                        //also, don't add the character to the buffer 
                        return VSConstants.S_OK;
                    }

                    Log.Debug("Dismiss");
                    //if there is no selection, dismiss the session
                    _activeSession.Dismiss();
                }
                else if (nCmdId == (uint)VSConstants.VSStd2KCmdID.TAB && _isRepl)
                {
                    TriggerCompletion();
                    return VSConstants.S_OK;
                }
            }
            // check for Ctrl-Space usage
            if (commandId == (uint)VSConstants.VSStd2KCmdID.COMPLETEWORD)
            {
                if (_activeSession != null && !_activeSession.IsDismissed)
                {
                    if (_activeSession.CompletionSets[0].SelectionStatus.IsSelected)
                    {
                        _activeSession.Commit();
                    }
                }
                else
                {
                    TriggerCompletion();
                }
                return VSConstants.S_OK;
            }

            //pass along the command so the char is added to the buffer 
            int retVal = NextCommandHandler.Exec(ref pguidCmdGroup, nCmdId, nCmdexecopt, pvaIn, pvaOut);
            bool handled = false;
            if (!typedChar.Equals(char.MinValue) && IsIntellisenseTrigger(typedChar))
            {
                TriggerCompletion();
            }
            if (!typedChar.Equals(char.MinValue) && IsFilterTrigger(typedChar))
            {
                if (_activeSession != null)
                {
                    if (_activeSession.IsStarted)
                    {
                        try
                        {
                            Log.Debug("Filter");
                            _activeSession.Filter();
                        }
                        catch (Exception ex)
                        {
                            Log.Debug("Failed to filter session.", ex);
                        }
                    }
                }
            }
            else if (commandId == (uint)VSConstants.VSStd2KCmdID.BACKSPACE //redo the filter if there is a deletion
                     || commandId == (uint)VSConstants.VSStd2KCmdID.DELETE)
            {
                if (_activeSession != null && !_activeSession.IsDismissed)
                {
                    try
                    {
                        Log.Debug("Filter");
                        _activeSession.Filter();
                    }
                    catch (Exception ex)
                    {
                        Log.Debug("Failed to filter session.", ex);
                    }
                }

                handled = true;
            }
            if (handled) return VSConstants.S_OK;
            return retVal;
        }

        /// <summary>
        /// Triggers an IntelliSense session. This is done in a seperate thread than the UI to allow
        /// the user to continue typing. 
        /// </summary>
        private void TriggerCompletion()
        {
            var caretPosition = (int)_textView.Caret.Position.BufferPosition;
            var thread = new Thread(() =>
            {
                try
                {
                    var line = _textView.Caret.Position.BufferPosition.GetContainingLine();
                    var caretInLine = (caretPosition - line.Start);
                    var text = line.GetText().Substring(0, caretInLine);
                    StartIntelliSense(line.Start, caretPosition, text);
                }
                catch (Exception ex)
                {
                    Log.Warn("Failed to start IntelliSense", ex);
                    _intellisenseRunning = false;
                }

            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private void StartIntelliSense(int lineStartPosition, int caretPosition, string lineTextUpToCaret)
        {
            if (_intellisenseRunning) return;

            _intellisenseRunning = true;
            var statusBar = (IVsStatusbar)PowerShellToolsPackage.Instance.GetService(typeof(SVsStatusbar));
            statusBar.SetText("Running IntelliSense...");
            var sw = new Stopwatch();
            sw.Start();

            IList<CompletionResult> completionMatchesList;
            int completionReplacementIndex;
            int completionReplacementLength;

            // Procedures for correctly supporting IntelliSense in REPL window.
            // Step 1, determine if this is REPL windows IntelliSense. If no, continue with normal IntelliSense triggering process. Otherwise, continue with the following steps.            
            // Step 2, map the caret position in current REPL window text buffer to the one in current POWERSHELL text buffer.
            // Step 3, get the current POWERSHELL text which only ranges from 0 to the caret position from Step 2.
            // Step 4, get the command completion results using the script text from Step 3 and the mapped caret position from Step 2.
            // Step 5, from this point on, make sure we go back to the original text buffer and caret position so that we can show the completion window in the right place.
            string script = String.Empty;
            int scriptParsePosition = 0;
            if (_textView.TextBuffer.ContentType.TypeName.Equals(PowerShellConstants.LanguageName, StringComparison.Ordinal))
            {
                script = _textView.TextBuffer.CurrentSnapshot.GetText();
                scriptParsePosition = caretPosition;
            }
            else if (_textView.TextBuffer.ContentType.TypeName.Equals(ReplConstants.ReplContentTypeName, StringComparison.Ordinal))
            {
                var currentActiveReplBuffer = _textView.BufferGraph.GetTextBuffers(p => p.ContentType.TypeName.Equals(PowerShellConstants.LanguageName, StringComparison.Ordinal))
                                                                   .LastOrDefault();
                var currentBufferPoint = _textView.BufferGraph.MapDownToBuffer(_textView.Caret.Position.BufferPosition,
                                                                               PointTrackingMode.Positive,
                                                                               currentActiveReplBuffer,
                                                                               PositionAffinity.Successor);
                scriptParsePosition = currentBufferPoint.Value.Position;
                script = currentActiveReplBuffer.CurrentSnapshot.GetText(0, scriptParsePosition);
            }
            else
            {
                Log.Error("The content type of the text buffer isn't recognized.");
                return;
            }

            // Go out-of-proc here to get the completion list
            var commandCompletion = PowerShellToolsPackage.IntelliSenseService.GetCompletionResults(script, scriptParsePosition);
            if (commandCompletion == null)
            {
                return;
            }
            completionMatchesList = (from item in commandCompletion.CompletionMatches
                                     select new CompletionResult(item.CompletionText,
                                                                 item.ListItemText,
                                                                 (CompletionResultType)item.ResultType,
                                                                 item.ToolTip)).ToList();
            completionReplacementLength = commandCompletion.ReplacementLength;
            completionReplacementIndex = caretPosition - completionReplacementLength;

            var line = _textView.Caret.Position.BufferPosition.GetContainingLine();
            var caretInLine = (caretPosition - line.Start);
            var text = line.GetText().Substring(0, caretInLine);

            if (string.Equals(lineTextUpToCaret, text, StringComparison.Ordinal) && completionMatchesList.Count != 0)
            {
                if (completionMatchesList.Count != 0)
                {
                    try
                    {
                        IntellisenseDone(completionMatchesList,
                                        lineStartPosition,
                                        completionReplacementIndex,
                                        completionReplacementLength,
                                        caretPosition);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug("Failed to start IntelliSense.", ex);
                    }
                }
            }

            statusBar.SetText(String.Format("IntelliSense complete in {0:0.00} seconds...", sw.Elapsed.TotalSeconds));
            _intellisenseRunning = false;
        }

        private void IntellisenseDone(IList<CompletionResult> completionResults, int lineStartPosition, int replacementIndex, int replacementLength, int startCaretPosition)
        {
            var textBuffer = _textView.TextBuffer;
            var length = replacementIndex - lineStartPosition;
            if (!SpanArgumentsAreValid(textBuffer.CurrentSnapshot, replacementIndex, replacementLength) || !SpanArgumentsAreValid(textBuffer.CurrentSnapshot, lineStartPosition, length))
            {
                return;
            }
            var lastWordReplacementSpan = textBuffer.CurrentSnapshot.CreateTrackingSpan(replacementIndex, replacementLength, SpanTrackingMode.EdgeInclusive);
            var lineUpToReplacementSpan = textBuffer.CurrentSnapshot.CreateTrackingSpan(lineStartPosition, length, SpanTrackingMode.EdgeExclusive);

            var triggerPoint = textBuffer.CurrentSnapshot.CreateTrackingPoint(startCaretPosition, PointTrackingMode.Positive);
            textBuffer.Properties.AddProperty(typeof(IList<CompletionResult>), completionResults);
            textBuffer.Properties.AddProperty(BufferProperties.LastWordReplacementSpan, lastWordReplacementSpan);
            textBuffer.Properties.AddProperty(BufferProperties.LineUpToReplacementSpan, lineUpToReplacementSpan);

            Log.Debug("Dismissing all sessions...");


            if (Application.Current.Dispatcher.Thread == Thread.CurrentThread)
            {
                StartSession(triggerPoint);
            }
            else
            {
                Application.Current.Dispatcher.Invoke(() => StartSession(triggerPoint));
            }

            textBuffer.Properties.RemoveProperty(typeof(IList<CompletionResult>));
            textBuffer.Properties.RemoveProperty(BufferProperties.LastWordReplacementSpan);
            textBuffer.Properties.RemoveProperty(BufferProperties.LineUpToReplacementSpan);
        }

        private void StartSession(ITrackingPoint triggerPoint)
        {
            Log.Debug("Creating new completion session...");
            _broker.DismissAllSessions(_textView);
            _activeSession = _broker.CreateCompletionSession(_textView, triggerPoint, true);
            _activeSession.Properties.AddProperty(BufferProperties.SessionOriginIntellisense, "Intellisense");
            _activeSession.Dismissed += CompletionSession_Dismissed;
            _activeSession.Start();
        }


        private void CompletionSession_Dismissed(object sender, EventArgs e)
        {
            Log.Debug("Session Dismissed.");
            _activeSession.Dismissed -= CompletionSession_Dismissed;
            _activeSession = null;
        }

        internal static bool SpanArgumentsAreValid(ITextSnapshot snapshot, int start, int length)
        {
            return start >= 0 && length >= 0 && start + length <= snapshot.Length;
        }

        /// <summary>
        /// Determines whether a typed character should cause the completion source list to filter.
        /// </summary>
        /// <param name="ch"></param>
        /// <returns></returns>
        private static bool IsFilterTrigger(char ch)
        {
            Log.DebugFormat("IsFilterTrigger: [{0}]", ch);
            return (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');
        }

        /// <summary>
        /// Determines whether a typed character should cause the manager to trigger the intellisense drop down.
        /// </summary>
        /// <param name="ch"></param>
        /// <returns></returns>
        private static bool IsIntellisenseTrigger(char ch)
        {
            Log.DebugFormat("IsIntellisenseTrigger: [{0}]", ch);
            return ch == '-' || ch == '$' || ch == '.' || ch == ':' || ch == '\\';
        }
    }
}