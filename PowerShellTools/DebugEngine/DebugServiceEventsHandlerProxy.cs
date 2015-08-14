using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.ServiceModel;
using System.Text;
using System.Threading.Tasks;
using PowerShellTools.Common.ServiceManagement.DebuggingContract;
using Microsoft.VisualStudio.Shell;
using PowerShellTools.ServiceManagement;
using System.Collections.ObjectModel;
using System.Management.Automation.Host;
using PowerShellTools.Common.Debugging;

namespace PowerShellTools.DebugEngine
{
    /// <summary>
    /// Proxy of debugger service event handlers
    /// This works as InstanceContext for debugger service channel
    /// </summary>
    [CallbackBehavior(
        ConcurrencyMode = ConcurrencyMode.Multiple, 
        UseSynchronizationContext = false,
        IncludeExceptionDetailInFaults = true)]
    [DebugServiceEventHandlerBehavior]
    public class DebugServiceEventsHandlerProxy : IDebugEngineCallback
    {
        private ScriptDebugger _debugger;
        private bool _uiOutput;

        public DebugServiceEventsHandlerProxy()
            : this(null, true) { }

        public DebugServiceEventsHandlerProxy(ScriptDebugger debugger, bool uiOutput)
        {
            _debugger = debugger;
            _uiOutput = uiOutput;
        }

        public ScriptDebugger Debugger
        {
            get
            {
                if (_debugger == null)
                {
                    _debugger = PowerShellToolsPackage.Debugger;
                }
                return _debugger;
            }
        }

        /// <summary>
        /// Debugger stopped
        /// </summary>
        /// <param name="e">DebuggerStoppedEventArgs</param>
        public void DebuggerStopped(DebuggerStoppedEventArgs e)
        {
            Debugger.DebuggerStop(e);
        }

        /// <summary>
        /// Placeholder for future support on debugging command in REPL window
        /// Breakpoint has updated
        /// </summary>
        /// <param name="e">DebuggerBreakpointUpdatedEventArgs</param>
        public void BreakpointUpdated(DebuggerBreakpointUpdatedEventArgs e)
        {
            Debugger.BreakpointManager.UpdateBreakpoint(e);
        }

        /// <summary>
        /// Debugger has string to output
        /// </summary>
        /// <param name="output">string to output</param>
        public void OutputString(string output)
        {
            if (_uiOutput)
            {
                ThreadHelper.Generic.Invoke(() =>
                {
                    Debugger.HostUi.VsOutputString(output);
                });
            }
            else
            {
                Debugger.HostUi.VsOutputString(output);
            }
        }

        /// <summary>
        /// Debugger has string to output
        /// </summary>
        /// <param name="output">string to output</param>
        public void OutputStringLine(string output)
        {
            OutputString(output + Environment.NewLine);
        }

        /// <summary>
        /// Debugger finished
        /// </summary>
        public void DebuggerFinished()
        {
            Debugger.DebuggerFinished();
        }

        /// <summary>
        /// Execution engine has terminating exception thrown
        /// </summary>
        /// <param name="ex">DebuggingServiceException</param>
        public void TerminatingException(PowerShellRunTerminatingException ex)
        {
            Debugger.TerminateException(ex);
        }

        /// <summary>
        /// Refreshes the prompt in the REPL window to match the current PowerShell prompt value
        /// </summary>
        public void RefreshPrompt()
        {
            Debugger.RefreshPrompt();
        }

        /// <summary>
        /// Ask for user input
        /// </summary>
        /// <param name="message">Diaglog message</param>
        /// <param name="name">Parameter name if any</param>
        /// <returns>Intput string</returns>
        public string ReadHostPrompt(string message, string name)
        {
            return Debugger.HostUi.ReadLine(message, name);
        }

        public int ReadHostPromptForChoices(string caption, string message, IList<ChoiceItem> choices, int defaultChoice)
        {
            return Debugger.HostUi.ReadChoice(caption, message, choices, defaultChoice);
        }

        /// <summary>
        /// Ask for securestring from user
        /// </summary>
        /// <param name="message">Message of dialog window.</param>
        /// <param name="name">Name of the parameter.</param>
        /// <returns>A PSCredential object that contains the credentials for the target.</returns>
        public PSCredential ReadSecureStringPrompt(string message, string name)
        {
            return Debugger.HostUi.ReadSecureStringAsPSCredential(message, name).Result;
        }

        /// <summary>
        /// To open specific file in client
        /// </summary>
        /// <param name="fullName">Full name of remote file(mapped into local)</param>
        public void OpenRemoteFile(string fullName)
        {
            Debugger.OpenFileInVS(fullName);
        }

        /// <summary>
        /// Set flag to indicate if runspace is hosting remote session
        /// </summary>
        /// <param name="enabled">Boolean indicate remote session is enable or not</param>
        public void SetRemoteRunspace(bool enabled)
        {
            Debugger.RemoteSession = enabled;
        }

        /// <summary>
        /// Outputs the Progress of the operation.
        /// </summary>
        /// <param name="sourceId">The id of the operation.</param>
        /// <param name="record">The record of the operation.</param>
        public void OutputProgress(long sourceId, ProgressRecord record)
        {
            Debugger.HostUi.VSOutputProgress(sourceId, record);
        }

        /// <summary>
        /// Ask for user input PSCredential from VS
        /// </summary>
        /// <param name="caption">The caption for the message window.</param>
        /// <param name="message">The text of the message.</param>
        /// <param name="userName">The user name whose credential is to be prompted for. If this parameter set to null or an empty string, the function will prompt for the user name first.</param>
        /// <param name="targetName">The name of the target for which the credential is collected.</param>
        /// <param name="allowedCredentialTypes">A bitwise combination of the PSCredentialTypes enumeration values that identify the types of credentials that can be returned.</param>
        /// <param name="options">A bitwise combination of the PSCredentialUIOptions enumeration values that identify the UI behavior when it gathers the credentials.</param>
        /// <returns>A PSCredential object that contains the credentials for the target.</returns>
        public PSCredential GetPSCredentialPrompt(string caption, string message, string userName,
            string targetName, PSCredentialTypes allowedCredentialTypes, PSCredentialUIOptions options)
        {
            return Debugger.HostUi.GetPSCredential(caption, message, userName,
                targetName, allowedCredentialTypes, options);
        }

        /// <summary>
        /// App running in host process requiring an user input
        /// </summary>
        public void RequestUserInputOnStdIn()
        {
            string inputText = Debugger.HostUi.ReadLine(Resources.UserInputRequestMessage, string.Empty);

            // Feed into stdin stream
            if (ConnectionManager.Instance != null && ConnectionManager.Instance.HostProcess != null)
            {
                ConnectionManager.Instance.HostProcess.WriteHostProcessStandardInputStream(inputText);
            }
        }

        /// <summary>
        /// Clear REPL window
        /// </summary>
        public void ClearHostScreen()
        {
            if (Debugger.ReplWindow != null)
            {
                Debugger.ReplWindow.ClearScreen();
            }
        }

        /// <summary>
        /// Wait next keystrock from VS
        /// </summary>
        /// <returns></returns>
        public VsKeyInfo VsReadKey()
        {
            if (Debugger.ReplWindow != null)
            {
                return Debugger.ReplWindow.WaitKey();
            }
            else
            {
                return null;
            }
        }

        /// <summary>
        /// Check whether the user has pressed a key. 
        /// </summary>
        /// <returns>Boolean indicating whether the user has pressed a key</returns>
        public bool IsKeyAvailable()
        {
            bool isAvailable = false;
            if (Debugger.ReplWindow != null)
            {
                isAvailable = Debugger.ReplWindow.IsKeyAvailable();
            }

            return isAvailable;
        }

        /// <summary>
        /// Get REPL window width so that buffer size can be coordinate
        /// </summary>
        /// <returns>REPL window size</returns>
        public int GetREPLWindowWidth()
        {
            int width = PowerShellTools.Common.Constants.MinimalReplBufferWidth;
            if (Debugger.ReplWindow != null)
            {
                width = Debugger.ReplWindow.GetRawHostBufferWidth();
            }

            return width;
        }

        public int PromptUserToPickRunspace(Collection<PSObject> runspaces)
        {
            return Debugger.HostUi.PromptUserToPickRunspace(runspaces);
        }
    }
}
