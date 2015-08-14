using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Threading;
using EnvDTE80;
using Microsoft.PowerShell;
using PowerShellTools.Common.Debugging;
using PowerShellTools.Common.ServiceManagement.DebuggingContract;
using System.Management.Automation.Remoting;

namespace PowerShellTools.HostService.ServiceManagement.Debugging
{
    /// <summary>
    /// Utility functions for powershell debugging service
    /// </summary>
    public partial class PowerShellDebuggingService
    {
        // Potential TODO: Refactor this class into either a static Utilities class

        private const string DteVariableName = "dte";

        private void SetRunspace(Runspace runspace)
        {
            if (_runspace != null)
            {
                UnloadRunspace(_runspace);
            }

            _runspace = runspace;
            LoadRunspace(_runspace);

            ProvideDteVariable(_runspace);
        }

        private void LoadRunspace(Runspace runspace)
        {
            if (_runspace != null)
            {
                if (GetDebugScenario() == DebugScenario.Local || _installedPowerShellVersion >= RequiredPowerShellVersionForRemoteSessionDebugging)
                {
                    runspace.Debugger.DebuggerStop += Debugger_DebuggerStop;
                    runspace.Debugger.BreakpointUpdated += Debugger_BreakpointUpdated;
                }

                runspace.StateChanged += _runspace_StateChanged;
            }
        }

        private void UnloadRunspace(Runspace runspace)
        {
            if (_runspace != null)
            {
                if (GetDebugScenario() == DebugScenario.Local || _installedPowerShellVersion >= RequiredPowerShellVersionForRemoteSessionDebugging)
                {
                    runspace.Debugger.DebuggerStop -= Debugger_DebuggerStop;
                    runspace.Debugger.BreakpointUpdated -= Debugger_BreakpointUpdated;
                }
            }

            runspace.StateChanged -= _runspace_StateChanged;
        }

        private void RefreshScopedVariable()
        {
            ServiceCommon.Log("Debuggger stopped, let us retreive all local variable in scope");
            using (var pipeline = (_runspace.CreateNestedPipeline()))
            {
                var command = new Command("Get-Variable");
                pipeline.Commands.Add(command);
                _varaiables = pipeline.Invoke();
            }
        }

        private void RefreshCallStack()
        {
            ServiceCommon.Log("Debuggger stopped, let us retreive all call stack frames");
            using (var pipeline = (_runspace.CreateNestedPipeline()))
            {
                var command = new Command("Get-PSCallstack");
                pipeline.Commands.Add(command);
                _callstack = pipeline.Invoke();
            }
        }

        private void OnTerminatingException(Exception ex)
        {
            ServiceCommon.Log("OnTerminatingException");
            UnloadRunspace(_runspace);
            if (_callback != null)
            {
                _callback.TerminatingException(new PowerShellRunTerminatingException(ex));
            }
        }

        private void DebuggerFinished()
        {
            ServiceCommon.Log("DebuggerFinished");

            ClearBreakpoints();
            _psBreakpointTable.Clear();

            if (_callback != null)
            {
                _callback.OutputStringLine(string.Empty);
                _callback.RefreshPrompt();
            }

            if (_runspace != null)
            {
                UnloadRunspace(_runspace);
            }

            if (_currentPowerShell != null)
            {
                _currentPowerShell.Stop();
                _currentPowerShell = null;
            }

            ReleaseWaitHandler();

            _debuggingCommand = string.Empty;
            _localVariables.Clear();
            _propVariables.Clear();

            if (_callback != null)
            {
                _callback.DebuggerFinished();
            }
        }

        private bool IsDebuggerActive(System.Management.Automation.Debugger debugger)
        {
            if (_installedPowerShellVersion >= RequiredPowerShellVersionForRemoteSessionDebugging)
            {
                // IsActive denotes debugger being stopped and the presence of breakpoints
                return debugger.IsActive;
            }

            return false;
        }

        private void InitializeRunspace(PSHost psHost)
        {
            ServiceCommon.Log("Initializing run space with debugger");
            InitialSessionState iss = InitialSessionState.CreateDefault();
            iss.ApartmentState = ApartmentState.STA;
            iss.ThreadOptions = PSThreadOptions.ReuseThread;

            _runspace = RunspaceFactory.CreateRunspace(psHost, iss);
            _runspace.Open();

            LoadProfile();
            ServiceCommon.Log("Done initializing runspace");
        }

        private void LoadProfile()
        {
            ServiceCommon.Log("Loading PowerShell Profile");
            using (PowerShell ps = PowerShell.Create())
            {
                var myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var windowsPowerShell = Path.Combine(myDocuments, "WindowsPowerShell");
                var profile = Path.Combine(windowsPowerShell, "PoshTools_profile.ps1");

                var fi = new FileInfo(profile);
                if (!fi.Exists)
                {
                    return;
                }

                ps.Runspace = _runspace;
                ps.AddScript(". '" + profile + "'");
                ps.Invoke();
            }
        }

        private void SetupExecutionPolicy()
        {
            SetExecutionPolicy(ExecutionPolicy.RemoteSigned, ExecutionPolicyScope.Process);
        }

        private void SetExecutionPolicy(ExecutionPolicy policy, ExecutionPolicyScope scope)
        {
            ExecutionPolicy currentPolicy = ExecutionPolicy.Undefined;

            ServiceCommon.Log("Setting execution policy");
            using (PowerShell ps = PowerShell.Create())
            {
                ps.Runspace = _runspace;

                ps.AddCommand("Get-ExecutionPolicy");

                foreach (var result in ps.Invoke())
                {
                    currentPolicy = ((ExecutionPolicy)result.BaseObject);
                    break;
                }

                if ((policy <= currentPolicy || currentPolicy == ExecutionPolicy.Bypass) && currentPolicy != ExecutionPolicy.Undefined) //Bypass is the absolute least restrictive, but as added in PS 2.0, and thus has a value of '4' instead of a value that corresponds to it's relative restrictiveness
                    return;

                ps.Commands.Clear();

                ps.AddCommand("Set-ExecutionPolicy")
                    .AddParameter("ExecutionPolicy", policy)
                    .AddParameter("Scope", scope)
                    .AddParameter("Force");
                ps.Invoke();
            }
        }

        private object RetrieveVariable(string varName)
        {
            var psVar = _localVariables.FirstOrDefault(v => v.Name == varName);
            object psVariable = (psVar == null) ? null : psVar.Value;

            if (psVariable == null && _propVariables.ContainsKey(varName))
            {
                psVariable = _propVariables[varName];
            }

            return psVariable;
        }

        private void ReleaseWaitHandler()
        {
            _resumeAction = DebugEngineConstants.Debugger_Stop;
            _pausedEvent.Set();
        }

        /// <summary>
        /// Provides the $dte Variable if we are in a local runspace and if the $dte variable has not yet been set.
        /// </summary>
        private static void ProvideDteVariable(Runspace runspace)
        {
            // only do this when we are working with a local runspace
            if (runspace.ConnectionInfo == null)
            {
                // Preset dte as PS variable if not yet
                if (runspace.SessionStateProxy.PSVariable.Get(DteVariableName) == null)
                {
                    ServiceCommon.Log("Providing $dte variable to the local runspace.");

                    DTE2 dte = DTEManager.GetDTE(App.VsProcessId);

                    if (dte != null)
                    {
                        // We want to make $dte constant so that it can't be overridden; similar to the $psISE analog

                        PSVariable dteVar = new PSVariable(DteVariableName, dte, ScopedItemOptions.Constant);

                        runspace.SessionStateProxy.PSVariable.Set(dteVar);
                    }
                    else
                    {
                        ServiceCommon.Log("Dte object not found.");
                    }
                }
            }
        }

        private string ExecuteDebuggingCommand(string debuggingCommand, bool output)
        {
            // Need to be thread-safe here, to ensure every debugging command get processed.
            // e.g: Set/Enable/Disable/Remove breakpoint during debugging 
            lock (_executeDebugCommandLock)
            {
                ServiceCommon.Log("Client asks for executing debugging command");
                _debugOutput = output;
                _debugCommandOutput = string.Empty;
                _debuggingCommand = debuggingCommand;
                _debugCommandEvent.Reset();
                _pausedEvent.Set();
                _debugCommandEvent.WaitOne();
                _debugOutput = true;
                _debuggingCommand = string.Empty;
                return _debugCommandOutput;
            }
        }

        private void objects_DataAdded(object sender, DataAddedEventArgs e)
        {
            var list = sender as PSDataCollection<PSObject>;
            StringBuilder outputString = new StringBuilder();
            foreach (PSObject obj in list)
            {
                outputString.AppendLine(obj.ToString());
            }

            if (_debugOutput)
            {
                NotifyOutputString(outputString.ToString());
            }
        }

        /// <summary>
        /// Opens the script a remote process is running.
        /// </summary>
        /// <param name="scriptName"></param>
        /// <returns></returns>
        private string OpenRemoteAttachedFile(string scriptName)
        {
            if (!_needToCopyRemoteScript && _mapRemoteToLocal.ContainsKey(scriptName))
            {
                return _mapRemoteToLocal[scriptName];
            }

            PSCommand psCommand = new PSCommand();
            psCommand.AddScript(string.Format("Get-Content \"{0}\"", scriptName));
            PSDataCollection<PSObject> result = new PSDataCollection<PSObject>();
            _runspace.Debugger.ProcessCommand(psCommand, result);

            string[] remoteText = new string[result.Count()];

            for (int i = 0; i < remoteText.Length; i++)
            {
                remoteText[i] = result.ElementAt(i).BaseObject as string;
            }

            // create new directory and corressponding file path/name
            string tmpFileName = Path.GetTempFileName();
            string dirPath = tmpFileName.Remove(tmpFileName.LastIndexOf('.'));
            string fullFileName = Path.Combine(dirPath, new FileInfo(scriptName).Name);

            // check to see if we have already copied the script over, and if so, overwrite
            if (_mapRemoteToLocal.ContainsKey(scriptName))
            {
                fullFileName = _mapRemoteToLocal[scriptName];
            }
            else
            {
                Directory.CreateDirectory(dirPath);
            }

            _mapRemoteToLocal[scriptName] = fullFileName;
            _mapLocalToRemote[fullFileName] = scriptName;

            File.WriteAllLines(fullFileName, remoteText);

            return fullFileName;
        }

        /// <summary>
        /// Re-adds all of the various event handlers to the runspace
        /// </summary>
        private void AddEventHandlers()
        {
            _runspace.Debugger.DebuggerStop += Debugger_DebuggerStop;
            _runspace.Debugger.BreakpointUpdated += Debugger_BreakpointUpdated;
            _runspace.StateChanged += _runspace_StateChanged;
            _runspace.AvailabilityChanged += _runspace_AvailabilityChanged;
        }

        /// <summary>
        /// Invokes given script on the provided PowerShell object after setting its runspace object. If powerShell is null, then the method will
        /// instantiate it inside of a using.
        /// </summary>
        /// <param name="powerShell">This should usually be _currentPowerShell. Make sure to enclose uses of _currentPowerShell and this method
        /// inside of a using.</param>
        /// <param name="script">Script to invoke.</param>
        /// <returns>Returns the result of the invoke</returns>
        private Collection<PSObject> InvokeScript(PowerShell powerShell, string script)
        {
            if (powerShell == null)
            {
                // if user passes in a null PowerShell object, we will instantiate it for them
                using (powerShell = PowerShell.Create())
                {
                    powerShell.Runspace = _runspace;
                    powerShell.AddScript(script);
                    return powerShell.Invoke();
                }
            }

            powerShell.Commands.Clear();
            powerShell.Runspace = _runspace;
            powerShell.AddScript(script);
            return powerShell.Invoke();
        }

        /// <summary>
        /// Uses _savedCredential in order to enter into a remote session with a remote machine. If _savedCredential is null, method will instead
        /// use InvokeScript to prompt/enter into a session without saving credentials.
        /// </summary>
        /// <param name="powerShell">This should be an already instanstiated PowerShell object, and should be inside of a using. If
        /// powerShell is null, method will return without performing any action.</param>
        /// <param name="remoteName">Machine to connect to.</param>
        private void EnterCredentialedRemoteSession(PowerShell powerShell, string remoteName, bool useSSL)
        {
            if (powerShell == null)
            {
                // callee is expected to passs in an already inalized PowerShell object to this method
                return;
            }

            if (_savedCredential == null)
            {
                InvokeScript(powerShell, string.Format(DebugEngineConstants.EnterRemoteSessionDefaultCommand, remoteName));
                return;
            }

            string port = remoteName.Split(':').ElementAtOrDefault(1);

            PSCommand enterSession = new PSCommand();
            enterSession.AddCommand("Enter-PSSession").AddParameter("ComputerName", remoteName).AddParameter("Credential", _savedCredential);

            if (port != null)
            {
                // check for user specified port
                enterSession.AddParameter("-Port", port);
            }
            if (useSSL)
            {
                // if told to use SSL from options dialog, add the SSL parameter
                enterSession.AddParameter("-UseSSL");
            }

            powerShell.Runspace = _runspace;
            powerShell.Commands.Clear();
            powerShell.Commands = enterSession;
            powerShell.Invoke();
        }

        /// <summary>
        /// Determines what string AttachToRunspace should return if it encounters an exception.
        /// </summary>
        /// <param name="exception"></param>
        /// <returns></returns>
        private string HandleAttachToRunspaceException(Exception exception)
        {
            if (exception is RemoteException || exception is PSRemotingDataStructureException)
            {
                if (_forceStop)
                {
                    // these two types of exceptions are expected if we have to stop during cleanup
                    ServiceCommon.Log(string.Format("Forced to detach via stop command; {0}", exception.ToString()));
                    return "";
                }
                else
                {
                    // other actions, such as closing a remote process mid debugging may cause an unexpected exception of these types
                    ServiceCommon.Log(string.Format("Unexpected remote exception while debugging runspace; {0}", exception.ToString()));
                    return Resources.ProcessDebugError;
                }
            }
            else
            {
                // any other sort of exception is not expected
                ServiceCommon.Log(string.Format("Unexpected exception while debugging runspace; {0}", exception.ToString()));
                return Resources.ProcessDebugError;
            }
        }

        /// <summary>
        /// Sends a collection of runspaces' info to the callback so it can present a dialog to the user
        /// </summary>
        /// <param name="runspaces">Collection of info for various runspaces in PSObject format</param>
        /// <returns>Id of the selected runspace, -1 if user hit cancel</returns>
        private int PromptUserToPickRunspace(Collection<PSObject> runspaces)
        {
            return _callback.PromptUserToPickRunspace(runspaces);
        }
    }
}
