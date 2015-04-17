using PowerShellTools.Common.IntelliSense;
using PowerShellTools.Common.ServiceManagement.IntelliSenseContract;
using PowerShellTools.HostService.ServiceManagement.Debugging;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using System.ServiceModel;
using System.Threading.Tasks;

namespace PowerShellTools.HostService.ServiceManagement
{
    /// <summary>
    /// Represents a implementation of the service contract.
    /// </summary>
    [PowerShellServiceHostBehavior]
    public sealed class PowerShellIntelliSenseService : IPowershellIntelliSenseService
    {
        private readonly Runspace _runspace = PowerShellDebuggingService.Runspace;
        private long _requestTrigger;
        private string _script = string.Empty;
        private int _caretPosition;
        private IIntelliSenseServiceCallback _callback;

        /// <summary>
        /// Request trigger property
        /// Once it was set, initellisense service starts processing the existing request in background thread
        /// </summary>
        public long RequestTrigger
        {
            get
            {
                return _requestTrigger;
            }
            set
            {
                _requestTrigger = value;
                
                if (_callback == null)
                {
                    _callback = OperationContext.Current.GetCallbackChannel<IIntelliSenseServiceCallback>();
                }
                
                // Start process the existing waiting request, should only be one
                Task.Run(() =>
                    {
                        try
                        {
                            CommandCompletion commandCompletion = null;
                            lock (ServiceCommon.RunspaceLock)
                            {
                                commandCompletion = CommandCompletionHelper.GetCommandCompletionList(_script, _caretPosition, _runspace);
                            }
                            ServiceCommon.LogCallbackEvent("Callback intellisense at position {0}", _caretPosition);
                            _callback.PushCompletionResult(CompletionResultList.FromCommandCompletion(commandCompletion));

                            // Reset trigger
                            _requestTrigger = 0;
                        }
                        catch (Exception ex)
                        {
                            ServiceCommon.Log("Failed to retrieve the completion list per request due to exception: {0}", ex.Message);
                        }
                    });
            }
        }

        /// <summary>
        /// Default ctor
        /// </summary>
        public PowerShellIntelliSenseService() { }

        /// <summary>
        /// Ctor (unit test hook)
        /// </summary>
        /// <param name="callback">Callback context object (unit test hook)</param>
        public PowerShellIntelliSenseService(IIntelliSenseServiceCallback callback)
            :this()
        {
            _callback = callback;
            _runspace = RunspaceFactory.CreateRunspace();
            _runspace.Open();
        }

        #region IAutoCompletionService Members

        /// <summary>
        /// Calculate the completion results list based on the script we have and the caret position.
        /// </summary>
        /// <param name="script">The active script.</param>
        /// <param name="caretPosition">The caret position.</param>
        /// <param name="triggerTag">Tag(incremental long) indicating the trigger sequence in client side</param>
        /// <returns>A completion results list.</returns>
        public void RequestCompletionResults(string script, int caretPosition, long triggerTag)
        {
            ServiceCommon.Log("Intellisense request received, caret position: {0}", _caretPosition.ToString());

            if (_requestTrigger == 0 ||
                triggerTag > RequestTrigger)
            {
                ServiceCommon.Log("Procesing request, caret position: {0}", _caretPosition.ToString());
                _script = script;
                _caretPosition = caretPosition;
                DismissGetCompletionResults();
                RequestTrigger = triggerTag; // triggering new request processing
            }
        }

        /// <summary>
        /// Suspecting this is a powershell bug, the first time you call CommandCompletion.CompleteInput, it takes much longer than usual.
        /// We are using this dummy call during intializing to warm it up.
        /// </summary>
        public void GetDummyCompletionList()
        {
            var commandCompletion = CommandCompletionHelper.GetCommandCompletionList("Write-", 6, _runspace);
        }

        /// <summary>
        /// Get error from parsing
        /// </summary>
        /// <param name="spanText">Script text</param>
        /// <returns></returns>
        public ParseErrorItem[] GetParseErrors(string spanText)
        {
            ParseError[] errors;
            Token[] tokens;
            Parser.ParseInput(spanText, out tokens, out errors);
            return (from item in errors
                    select new ParseErrorItem(item.Message,
                                              item.Extent.StartOffset,
                                              item.Extent.EndOffset)).ToArray();
        }

        /// <summary>
        /// Dismiss the current running completion request
        /// </summary>
        private void DismissGetCompletionResults()
        {
            try
            {
                CommandCompletionHelper.DismissCommandCompletionListRequest();
            }
            catch
            {
                ServiceCommon.Log("Failed to stop the existing one.");
            }
        }

        #endregion
    }
}
