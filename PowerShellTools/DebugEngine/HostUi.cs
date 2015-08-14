using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security;
using PowerShellTools.Repl;
using PowerShellTools.Commands.UserInterface;
using PowerShellTools.DebugEngine.RunspacePickerUI;
using Thread = System.Threading.Thread;

namespace PowerShellTools.DebugEngine
{
#if POWERSHELL
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using System.Threading.Tasks;
    using System.Windows.Forms;
    using Microsoft.VisualStudio.Shell;
    using Microsoft.VisualStudio.Shell.Interop;
    using PowerShellTools.Common.Debugging;
    using PowerShellTools.Common.ServiceManagement.DebuggingContract;
    using PowerShellTools.CredentialUI;
    using PowerShellTools.DebugEngine.PromptUI;
    using PowerShellTools.ServiceManagement;
    using IReplWindow = IPowerShellReplWindow;
#endif

    /// <summary>
    /// The PoshTools PowerShell host and debugger; the part that interaces with the host (Visual Studio).
    /// </summary>
    public partial class ScriptDebugger
    {
        private readonly Guid _instanceId = Guid.NewGuid();
        private readonly CultureInfo _originalCultureInfo = Thread.CurrentThread.CurrentCulture;
        private readonly CultureInfo _originalUiCultureInfo = Thread.CurrentThread.CurrentUICulture;
        private Runspace _runspace;
        private IPowerShellDebuggingService _debuggingServiceTest;

        public IPowerShellDebuggingService DebuggingService
        {
            get
            {
                if (_debuggingServiceTest != null)
                {
                    return _debuggingServiceTest;
                }

                return PowerShellToolsPackage.DebuggingService;
            }
            private set
            {
                _debuggingServiceTest = value;
            }
        }

        public Runspace Runspace
        {
            get
            {
                return _runspace;
            }
            set
            {
                _runspace = value;
            }
        }

        public ScriptDebugger(bool overrideExecutionPolicy)
            : this(overrideExecutionPolicy, null)
        {
            ConnectionManager.Instance.ConnectionException += ConnectionExceptionHandler;
        }

        internal ScriptDebugger(bool overrideExecutionPolicy, IPowerShellDebuggingService debuggingServiceTestHook)
        {
            OverrideExecutionPolicy = overrideExecutionPolicy;
            _debuggingServiceTest = debuggingServiceTestHook;
            DebuggingService.SetRunspace(overrideExecutionPolicy);

            //TODO: remove once user prompt work is finished for debugging
            _runspace = RunspaceFactory.CreateRunspace();
            _runspace.Open();
            HostUi = new HostUi();

            BreakpointManager = new BreakpointManager();

            NativeMethods.SetForegroundWindow();
        }

        public HostUi HostUi { get; private set; }

        public bool OverrideExecutionPolicy { get; private set; }

        public IReplWindow ReplWindow
        {
            get { return HostUi.ReplWindow; }
            set
            {
                HostUi.ReplWindow = value;
                if (value != null)
                {
                    RefreshPrompt();
                }
            }
        }

        /// <summary>
        ///     Refreshes the prompt in the REPL window to match the current PowerShell prompt value.
        /// </summary>
        public void RefreshPrompt()
        {
            if (HostUi != null && HostUi.ReplWindow != null)
                HostUi.ReplWindow.SetOptionValue(ReplOptions.CurrentPrimaryPrompt, GetPrompt());
        }

        private string GetPrompt()
        {
            try
            {
                string prompt = string.Empty;

                if (DebuggingService != null)
                {
                    if (IsDebuggingCommandReady)
                    {
                        prompt = DebuggingService.ExecuteDebuggingCommandOutNull(DebugEngineConstants.GetPrompt);
                    }
                    else if (DebuggingService.GetRunspaceAvailability() == RunspaceAvailability.Available)
                    {
                        prompt = DebuggingService.GetPrompt();
                    }

                    return prompt;
                }

                return prompt;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// Hanldes interaction with the HostUi.
    /// </summary>
    public class HostUi
    {
        public IReplWindow ReplWindow { get; set; }

        private static readonly object AnimationIconGeneralIndex = (short)STATUSBARCONSTS.SBAI_Gen; //General Status Bar Animation
        private static readonly object AnimationProgressSyncObject = new object();  // Needed to keep the animation count correct.

        private static HashSet<long> _animationProgressSources = new HashSet<long>();

        internal void VSOutputProgress(long sourceId, ProgressRecord record)
        {
            if (record == null)
            {
                throw new ArgumentNullException("record");
            }

            //TODO: If Visual studio ever has a global event/task pane, this would be a perfect place to tie into here.

            var statusBar = (IVsStatusbar)PowerShellToolsPackage.GetGlobalService(typeof(SVsStatusbar));

            if (statusBar != null)
            {
                uint cookie = 0;

                ProgressRecordType progressStatus = record.RecordType;

                string label = string.Format(ResourceStrings.ProgressBarFormat, record.Activity, record.StatusDescription);

                switch (progressStatus)
                {
                    case ProgressRecordType.Processing:
                        {
                            if (record.PercentComplete >= 0 && record.PercentComplete < 100)
                            {
                                statusBar.Progress(ref cookie, 1, label, (uint)record.PercentComplete, 100);
                            }
                            else if (record.PercentComplete == 100)
                            {
                                statusBar.Progress(ref cookie, 1, "", 0, 0);
                            }
                            else
                            {
                                // According to PS ProgressRecord docs, Negative values means a progress bar should not be displayed.

                                lock (AnimationProgressSyncObject)
                                {
                                    if (_animationProgressSources.Add(sourceId)) //Returns false if already exists.
                                    {
                                        // This is needed because Visual Studio keeps a count of each animation. 
                                        // Animation is removed only when count goes to zero.
                                        statusBar.Animation(1, AnimationIconGeneralIndex);
                                    }

                                    statusBar.SetText(label);
                                }
                            }
                            //Currently, we do not show Seconds Remaining
                            break;
                        }
                    case ProgressRecordType.Completed:
                        {
                            //Only other value is ProgressRecordType.Completed

                            if (record.PercentComplete >= 0)
                            {
                                statusBar.Progress(ref cookie, 0, string.Empty, 0, 0);
                            }
                            else
                            {
                                lock (AnimationProgressSyncObject)
                                {
                                    if (_animationProgressSources.Remove(sourceId))  //returns false if item not found.
                                    {
                                        statusBar.Animation(0, AnimationIconGeneralIndex);
                                    }

                                    statusBar.SetText(label);
                                }
                            }
                            break;
                        }
                }
            }
        }

        public Action<string> OutputString { get; set; }

        /// <summary>
        /// Read host from user input
        /// </summary>
        /// <param name="message">Prompt dialog message</param>
        /// <param name="name">Parameter Name if any</param>
        /// <returns>User input string</returns>
        public string ReadLine(string message, string name)
        {
            string input = string.Empty;

            ThreadHelper.Generic.Invoke(() =>
            {
                ReadHostPromptDialogViewModel viewModel = new ReadHostPromptDialogViewModel(message, name);
                ReadHostPromptDialog dialog = new ReadHostPromptDialog(viewModel);

                var ret = dialog.ShowModal();

                if (ret.HasValue && ret.Value == true)
                {
                    input = viewModel.ParameterValue;
                }
            });

            return input;
        }

        public int ReadChoice(string caption, string message, IList<ChoiceItem> choices, int defaultChoice)
        {
            if (string.IsNullOrEmpty(caption))
            {
                caption = ResourceStrings.PromptForChoice_DefaultCaption;
            }

            if (message == null)
            {
                message = string.Empty;
            }

            if (choices == null)
            {
                throw new ArgumentNullException("choices");
            }

            if (!choices.Any())
            {
                throw new ArgumentException(string.Format(ResourceStrings.ChoicesCollectionShouldHaveAtLeastOneElement, "choices"), "choices");
            }

            foreach (var c in choices)
            {
                if (c == null)
                {
                    throw new ArgumentNullException("choices");
                }
            }

            if (defaultChoice < -1 || defaultChoice >= choices.Count)
            {
                throw new ArgumentOutOfRangeException("defaultChoice");
            }

            int choice = -1;
            ThreadHelper.Generic.Invoke(() =>
            {
                ReadHostPromptForChoicesViewModel viewModel = new ReadHostPromptForChoicesViewModel(caption, message, choices, defaultChoice);
                ReadHostPromptForChoicesView dialog = new ReadHostPromptForChoicesView(viewModel);

                NativeMethods.SetForegroundWindow();
                var ret = dialog.ShowModal();
                if (ret == true)
                {
                    choice = viewModel.UserChoice;
                }
            });

            return choice;
        }

        /// <summary>
        /// Ask for securestring from user
        /// </summary>
        /// <param name="message">Message of dialog window.</param>
        /// <param name="name">Name of the parameter.</param>
        /// <returns>A PSCredential object that contains the securestring.</returns>
        public async Task<PSCredential> ReadSecureStringAsPSCredential(string message, string name)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            SecureString secString = new SecureString();
            SecureStringDialogViewModel viewModel = new SecureStringDialogViewModel(message, name);
            SecureStringDialog dialog = new SecureStringDialog(viewModel);

            var ret = dialog.ShowModal();
            if (ret.HasValue && ret.Value == true)
            {
                secString = viewModel.SecString;
            }

            return new PSCredential("securestring", secString);
        }

        /// <summary>
        /// Ask for PSCredential from user
        /// </summary>
        /// <param name="caption">The caption for the message window.</param>
        /// <param name="message">The text of the message.</param>
        /// <param name="userName">The user name whose credential is to be prompted for. If this parameter set to null or an empty string, the function will prompt for the user name first.</param>
        /// <param name="targetName">The name of the target for which the credential is collected.</param>
        /// <param name="allowedCredentialTypes">A bitwise combination of the PSCredentialTypes enumeration values that identify the types of credentials that can be returned.</param>
        /// <param name="options">A bitwise combination of the PSCredentialUIOptions enumeration values that identify the UI behavior when it gathers the credentials.</param>
        /// <returns>A PSCredential object that contains the credentials for the target.</returns>
        public PSCredential GetPSCredential(string caption, string message, string userName,
            string targetName, PSCredentialTypes allowedCredentialTypes, PSCredentialUIOptions options)
        {
            PSCredential result = null;

            CredentialsDialog dialog = new CredentialsDialog(targetName, caption, message);
            dialog.Name = userName;

            switch (options)
            {
                case PSCredentialUIOptions.AlwaysPrompt:
                    dialog.AlwaysDisplay = true;
                    break;
                case PSCredentialUIOptions.ReadOnlyUserName:
                    dialog.KeepName = true;
                    break;
                case PSCredentialUIOptions.Default:
                    dialog.ValidName = true;
                    break;
                case PSCredentialUIOptions.None:
                    break;
                default:
                    break;
            }

            if (dialog.Show() == DialogResult.OK)
            {
                result = new PSCredential(dialog.Name, dialog.Password);
            }

            return result;
        }

        /// <summary>
        /// Output string from debugger in VS output/REPL pane window
        /// </summary>
        /// <param name="output"></param>
        public void VsOutputString(string output)
        {
            if (ReplWindow != null)
            {
                if (output.StartsWith(PowerShellConstants.PowerShellOutputErrorTag))
                {
                    ReplWindow.WriteError(output);
                }
                else
                {
                    ReplWindow.WriteOutput(output);
                }
            }

            if (OutputString != null)
            {
                OutputString(output);
            }
        }

        public int PromptUserToPickRunspace(Collection<PSObject> runspaces)
        {
            // grab info from each PSObject and wrap it up inside of a RunspaceInfo object
            List<RunspaceInfo> runspaceInfos = new List<RunspaceInfo>();
            foreach (PSObject obj in runspaces)
            {
                int runspaceId = (int)obj.Properties["Id"].Value;
                string runspaceName = obj.Properties["Name"].Value.ToString();

                string computerName;
                if(obj.Properties["ConnectionInfo"].Value == null)
                {
                    // assume localhost if the runspace does not have a connection info
                    computerName = "localhost";
                }
                else
                {
                    computerName = (obj.Properties["ConnectionInfo"].Value as PSObject).Properties["ComputerName"].ToString();
                }

                string state = obj.Properties["RunspaceStateInfo"].Value.ToString();
                string availability = obj.Properties["RunspaceAvailability"].Value.ToString();

                runspaceInfos.Add(new RunspaceInfo(runspaceId, runspaceName, computerName, state, availability));
            }

            int chosenRunspace = -1;
            ThreadHelper.Generic.Invoke(() =>
            {
                var viewModel = new RunspacePickerWindowViewModel(runspaceInfos);
                var view = new RunspacePickerWindow(viewModel);

                if (view.ShowModal() == true)
                {
                    chosenRunspace = view.RunspacesListView.SelectedIndex;
                }
                else
                {
                    chosenRunspace = -1;
                }
            });

            return chosenRunspace;
        }
    }
}