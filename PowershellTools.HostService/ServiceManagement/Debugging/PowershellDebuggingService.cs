using EnvDTE80;
using Microsoft.PowerShell;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.ServiceModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PowerShellTools.Common.ServiceManagement.DebuggingContract;
using System.Text.RegularExpressions;
using PowerShellTools.Common.Debugging;
using System.Diagnostics;
using PowerShellTools.Common.IntelliSense;
using PowerShellTools.Common;

namespace PowerShellTools.HostService.ServiceManagement.Debugging
{
    [ServiceBehavior(ConcurrencyMode = ConcurrencyMode.Multiple)]
    [PowerShellServiceHostBehavior]
    public partial class PowerShellDebuggingService : IPowershellDebuggingService
    {
        private static Runspace _runspace;
        private PowerShell _currentPowerShell;
        private IDebugEngineCallback _callback;
        private string _debuggingCommand;
        private IEnumerable<PSObject> _varaiables;
        private IEnumerable<PSObject> _callstack;
        private Collection<PSVariable> _localVariables;
        private Dictionary<string, Object> _propVariables;
        private Dictionary<string, string> _mapLocalToRemote;
        private Dictionary<string, string> _mapRemoteToLocal;
        private List<PowerShellBreakpointRecord> _psBreakpointTable;
        private readonly AutoResetEvent _pausedEvent = new AutoResetEvent(false);
        private readonly AutoResetEvent _debugCommandEvent = new AutoResetEvent(false);
        private object _executeDebugCommandLock = new object();
        private string _debugCommandOutput;
        private bool _debugOutput;
        private static readonly Regex _rgx = new Regex(DebugEngineConstants.ExecutionCommandFileReplacePattern);
        private DebuggerResumeAction _resumeAction;
        private Version _installedPowerShellVersion;

        /// <summary>
        /// Minimal powershell version required for remote session debugging
        /// </summary>
        private static readonly Version RequiredPowerShellVersionForRemoteSessionDebugging = new Version(4, 0);

        public PowerShellDebuggingService()
        {
            ServiceCommon.Log("Initializing debugging engine service ...");
            HostUi = new HostUi(this);
            _localVariables = new Collection<PSVariable>();
            _propVariables = new Dictionary<string, object>();
            _mapLocalToRemote = new Dictionary<string, string>();
            _mapRemoteToLocal = new Dictionary<string, string>();
            _psBreakpointTable = new List<PowerShellBreakpointRecord>();
            _debugOutput = true;
            _installedPowerShellVersion = DependencyUtilities.GetInstalledPowerShellVersion();
            InitializeRunspace(this);
        }

        /// <summary>
        /// The runspace used by the current PowerShell host.
        /// </summary>
        public static Runspace Runspace
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

        public IDebugEngineCallback CallbackService
        {
            get
            {
                return _callback;
            }
            set
            {
                _callback = value;
            }
        }

        #region Debugging service calls

        /// <summary>
        /// Initialization of the PowerShell runspace
        /// </summary>
        public void SetRunspace(bool overrideExecutionPolicy)
        {
            if (overrideExecutionPolicy)
            {
                SetupExecutionPolicy();
            }

            SetRunspace(_runspace);
        }

        /// <summary>
        /// Client respond with resume action to service
        /// </summary>
        /// <param name="action">Resumeaction from client</param>
        /// <returns>Output from debugging command</returns>
        public string ExecuteDebuggingCommandOutDefault(string debuggingCommand)
        {
            return ExecuteDebuggingCommand(debuggingCommand, true); // also print output to user
        }

        /// <summary>
        /// Client respond with resume action to service
        /// </summary>
        /// <param name="action">Resumeaction from client</param>
        /// <returns>Output from debugging command</returns>
        public string ExecuteDebuggingCommandOutNull(string debuggingCommand)
        {
            return ExecuteDebuggingCommand(debuggingCommand, false); // dont need print output to user
        }

        /// <summary>
        /// Sets breakpoint for the current runspace.
        /// </summary>
        /// <param name="bp">Breakpoint to set</param>
        public void SetBreakpoint(PowershellBreakpoint bp)
        {
            IEnumerable<PSObject> breakpoints;

            ServiceCommon.Log("Setting breakpoint ...");
            try
            {
                using (var pipeline = (_runspace.CreatePipeline()))
                {
                    var command = new Command("Set-PSBreakpoint");

                    string file = bp.ScriptFullPath;
                    if (_runspace.ConnectionInfo != null && _mapLocalToRemote.ContainsKey(bp.ScriptFullPath))
                    {
                        file = _mapLocalToRemote[bp.ScriptFullPath];
                    }

                    command.Parameters.Add("Script", file);

                    command.Parameters.Add("Line", bp.Line);

                    pipeline.Commands.Add(command);

                    breakpoints = pipeline.Invoke();
                }

                var pobj = breakpoints.FirstOrDefault();
                if (pobj != null)
                {
                    _psBreakpointTable.Add(
                        new PowerShellBreakpointRecord(
                            bp,
                            ((LineBreakpoint)pobj.BaseObject).Id));
                }
            }
            catch (InvalidOperationException)
            {
                ServiceCommon.Log("Invalid breakpoint location!");
            }
        }

        /// <summary>
        /// Remove breakpoint for the current runspace.
        /// </summary>
        /// <param name="bp">Breakpoint to set</param>
        public void RemoveBreakpoint(PowershellBreakpoint bp)
        {
            int id = GetPSBreakpointId(bp);

            if (id >= 0)
            {
                ServiceCommon.Log("Removing breakpoint ...");

                using (var pipeline = (_runspace.CreatePipeline()))
                {
                    var command = new Command("Remove-PSBreakpoint");

                    string file = bp.ScriptFullPath;
                    if (_runspace.ConnectionInfo != null && _mapLocalToRemote.ContainsKey(bp.ScriptFullPath))
                    {
                        file = _mapLocalToRemote[bp.ScriptFullPath];
                    }

                    command.Parameters.Add("Id", id);

                    pipeline.Commands.Add(command);

                    pipeline.Invoke();
                }

                foreach (var p in _psBreakpointTable.Where(b => b.PSBreakpoint.Equals(bp)))
                {
                    _psBreakpointTable.Remove(p);
                }
            }
        }

        /// <summary>
        /// Enable/Disable breakpoint for the current runspace.
        /// </summary>
        /// <param name="bp">Breakpoint to set</param>
        public void EnableBreakpoint(PowershellBreakpoint bp, bool enable)
        {
            int id = GetPSBreakpointId(bp);

            if (id >= 0)
            {
                ServiceCommon.Log(string.Format("{0} breakpoint ...", enable ? "Enable" : "Disable"));

                using (var pipeline = (_runspace.CreatePipeline()))
                {
                    string cmd = enable ? "Enable-PSBreakpoint" : "Disable-PSBreakpoint";

                    var command = new Command(cmd);

                    string file = bp.ScriptFullPath;
                    if (_runspace.ConnectionInfo != null && _mapLocalToRemote.ContainsKey(bp.ScriptFullPath))
                    {
                        file = _mapLocalToRemote[bp.ScriptFullPath];
                    }

                    command.Parameters.Add("Id", id);

                    pipeline.Commands.Add(command);

                    pipeline.Invoke();
                }
            }
            else
            {
                ServiceCommon.Log("Can not locate the breakpoint!");
            }
        }

        /// <summary>
        /// Clears existing breakpoints for the current runspace.
        /// </summary>
        public void ClearBreakpoints()
        {
            ServiceCommon.Log("ClearBreakpoints");

            IEnumerable<PSObject> breakpoints;

            using (var pipeline = (_runspace.CreatePipeline()))
            {
                var command = new Command("Get-PSBreakpoint");
                pipeline.Commands.Add(command);
                breakpoints = pipeline.Invoke();
            }

            if (!breakpoints.Any()) return;

            using (var pipeline = (_runspace.CreatePipeline()))
            {
                var command = new Command("Remove-PSBreakpoint");
                command.Parameters.Add("Breakpoint", breakpoints);
                pipeline.Commands.Add(command);

                pipeline.Invoke();
            }
        }

        /// <summary>
        /// Get powershell breakpoint Id
        /// </summary>
        /// <param name="bp">Powershell breakpoint</param>
        /// <returns>Id of breakpoint if found, otherwise -1</returns>
        public int GetPSBreakpointId(PowershellBreakpoint bp)
        {
            ServiceCommon.Log("Getting PSBreakpoint ...");
            var bpr = _psBreakpointTable.FirstOrDefault(b => b.PSBreakpoint.Equals(bp));

            return bpr != null ? bpr.Id : -1;
        }

        /// <summary>
        /// Get runspace availability
        /// </summary>
        /// <returns>runspace availability enum</returns>
        public RunspaceAvailability GetRunspaceAvailability()
        {
            RunspaceAvailability state = _runspace.RunspaceAvailability;
            ServiceCommon.Log("Checking runspace availability: " + state.ToString());

            return state;
        }

        /// <summary>
        /// Execute the specified command line from client
        /// </summary>
        /// <param name="commandLine">Command line to execute</param>
        public bool Execute(string commandLine)
        {
            ServiceCommon.Log("Start executing ps script ...");

            try
            {
                _pausedEvent.Reset();

                // Retrieve callback context
                if (_callback == null)
                {
                    _callback = OperationContext.Current.GetCallbackChannel<IDebugEngineCallback>();
                }

                if (_callback == null)
                {
                    ServiceCommon.Log("No instance context retrieved.");
                    return false;
                }

                if (_runspace.RunspaceAvailability != RunspaceAvailability.Available)
                {
                    _callback.OutputStringLine(Resources.Error_PipelineBusy);
                    return true;
                }

                bool error = false;
                if (_runspace.ConnectionInfo != null && Regex.IsMatch(commandLine, DebugEngineConstants.ExecutionCommandPattern))
                {
                    string localFile = _rgx.Match(commandLine).Value;

                    if (_mapLocalToRemote.ContainsKey(localFile))
                    {
                        commandLine = _rgx.Replace(commandLine, _mapLocalToRemote[localFile]);
                    }
                    else
                    {
                        _callback.OutputStringLine(string.Format(Resources.Error_LocalScriptInRemoteSession, localFile));

                        ServiceCommon.Log(Resources.Error_LocalScriptInRemoteSession + Environment.NewLine, localFile);

                        return false;
                    }
                }

                lock (ServiceCommon.RunspaceLock)
                {
                    using (_currentPowerShell = PowerShell.Create())
                    {
                        _currentPowerShell.Runspace = _runspace;
                        _currentPowerShell.AddScript(commandLine);

                        _currentPowerShell.AddCommand("out-default");
                        _currentPowerShell.Commands.Commands[0].MergeMyResults(PipelineResultTypes.Error, PipelineResultTypes.Output);

                        _currentPowerShell.Invoke();
                        error = _currentPowerShell.HadErrors;
                    }
                }

                return !error;
            }
            catch (TypeLoadException ex)
            {
                ServiceCommon.Log("Type,  Exception: {0}", ex.Message);
                OnTerminatingException(ex);
                return false;
            }
            catch (Exception ex)
            {
                ServiceCommon.Log("Terminating error,  Exception: {0}", ex.Message);
                OnTerminatingException(ex);
                return false;
            }
            finally
            {
                DebuggerFinished();
            }
        }

        /// <summary>
        /// Stop the current executiong
        /// </summary>
        public void Stop()
        {
            ReleaseWaitHandler();

            if (_currentPowerShell != null)
            {
                _currentPowerShell.Stop();
            }
        }

        /// <summary>
        /// Get all local scoped variables for client
        /// </summary>
        /// <returns>Collection of variable to client</returns>
        public Collection<Variable> GetScopedVariable()
        {
            Collection<Variable> variables = new Collection<Variable>();

            foreach (var psobj in _varaiables)
            {
                PSVariable psVar = null;
                if (_runspace.ConnectionInfo == null)
                {
                    // Local debugging variable
                    psVar = psobj.BaseObject as PSVariable;

                    if (psVar != null)
                    {
                        if (psVar.Value is PSObject &&
                            !(((PSObject)psVar.Value).ImmediateBaseObject is PSCustomObject))
                        {
                            psVar = new PSVariable(
                                (string)psVar.Name,
                                ((PSObject)psVar.Value).ImmediateBaseObject,
                                ScopedItemOptions.None);
                        }

                        variables.Add(new Variable(psVar));
                    }
                }
                else
                {
                    // Remote debugging variable
                    dynamic dyVar = (dynamic)psobj;

                    if (dyVar.Value == null)
                    {
                        variables.Add(new Variable(dyVar.Name, string.Empty, string.Empty, false, false));
                    }
                    else
                    {
                        // Variable was wrapped into Deserialized.PSObject, which contains a deserialized representation of public properties of the corresponding remote, live objects.
                        if (dyVar.Value is PSObject)
                        {
                            // Non-primitive types
                            if (((PSObject)dyVar.Value).ImmediateBaseObject is string)
                            {
                                // BaseObject is string indicates the original object is real PSObject
                                psVar = new PSVariable(
                                    (string)dyVar.Name,
                                    (PSObject)dyVar.Value,
                                    ScopedItemOptions.None);
                            }
                            else
                            {
                                // Otherwise we should look into its BaseObject to obtain the original object
                                psVar = new PSVariable(
                                    (string)dyVar.Name,
                                    ((PSObject)dyVar.Value).ImmediateBaseObject,
                                    ScopedItemOptions.None);
                            }

                            variables.Add(new Variable(psVar));
                        }
                        else
                        {
                            // Primitive types
                            psVar = new PSVariable(
                                (string)dyVar.Name,
                                dyVar.Value.ToString(),
                                ScopedItemOptions.None);
                            variables.Add(new Variable(psVar.Name, psVar.Value.ToString(), dyVar.Value.GetType().ToString(), false, false));
                        }
                    }
                }

                if (psVar != null)
                {
                    PSVariable existingVar = _localVariables.FirstOrDefault(v => v.Name == psVar.Name);
                    if (existingVar != null)
                    {
                        _localVariables.Remove(existingVar);
                    }

                    _localVariables.Add(psVar);
                }
            }

            return variables;
        }


        /// <summary>
        /// Expand IEnumerable to retrieve all elements
        /// </summary>
        /// <param name="varName">IEnumerable object name</param>
        /// <returns>Collection of variable to client</returns>
        public Collection<Variable> GetExpandedIEnumerableVariable(string varName)
        {
            ServiceCommon.Log("Client tries to watch an IEnumerable variable, dump its content ...");

            Collection<Variable> expandedVariable = new Collection<Variable>();

            object psVariable = RetrieveVariable(varName);

            if (psVariable != null && psVariable is IEnumerable)
            {
                int i = 0;
                foreach (var item in (IEnumerable)psVariable)
                {
                    object obj = item;
                    var psObj = obj as PSObject;
                    if (psObj != null && _runspace.ConnectionInfo != null && !(psObj.ImmediateBaseObject is string))
                    {
                        obj = psObj.ImmediateBaseObject;
                    }

                    expandedVariable.Add(new Variable(String.Format("[{0}]", i), obj.ToString(), obj.GetType().ToString(), obj is IEnumerable, obj is PSObject));

                    if (!obj.GetType().IsPrimitive)
                    {
                        string key = string.Format("{0}\\{1}", varName, String.Format("[{0}]", i));
                        _propVariables[key] = obj;
                    }

                    i++;
                }
            }

            return expandedVariable;
        }

        /// <summary>
        /// Expand object to retrieve all properties
        /// </summary>
        /// <param name="varName">Object name</param>
        /// <returns>Collection of variable to client</returns>
        public Collection<Variable> GetObjectVariable(string varName)
        {
            ServiceCommon.Log("Client tries to watch an object variable, dump its content ...");

            Collection<Variable> expandedVariable = new Collection<Variable>();

            object psVariable = RetrieveVariable(varName);

            if (psVariable != null && !(psVariable is IEnumerable) && !(psVariable is PSObject))
            {
                var props = psVariable.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

                foreach (var propertyInfo in props)
                {
                    try
                    {
                        object val = propertyInfo.GetValue(psVariable, null);
                        if (val != null)
                        {
                            expandedVariable.Add(new Variable(propertyInfo.Name, val.ToString(), val.GetType().ToString(), val is IEnumerable, val is PSObject));

                            if (!val.GetType().IsPrimitive)
                            {
                                string key = string.Format("{0}\\{1}", varName, propertyInfo.Name);
                                _propVariables[key] = val;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        ServiceCommon.Log("Property infomation is not able to be retrieved through reflection due to exception: {0} {2} InnerException: {1}", ex.Message, ex.InnerException, Environment.NewLine);
                    }
                }
            }

            return expandedVariable;
        }

        /// <summary>
        /// Expand PSObject to retrieve all its properties
        /// </summary>
        /// <param name="varName">PSObject name</param>
        /// <returns>Collection of variable to client</returns>
        public Collection<Variable> GetPSObjectVariable(string varName)
        {
            ServiceCommon.Log("Client tries to watch an PSObject variable, dump its content ...");

            Collection<Variable> propsVariable = new Collection<Variable>();

            object psVariable = RetrieveVariable(varName);

            if (psVariable != null && psVariable is PSObject)
            {
                foreach (var prop in ((PSObject)psVariable).Properties)
                {
                    if (propsVariable.Any(m => m.VarName == prop.Name))
                    {
                        continue;
                    }

                    object val;
                    try
                    {
                        val = prop.Value;
                        var psObj = val as PSObject;
                        if (psObj != null && _runspace.ConnectionInfo != null && !(psObj.ImmediateBaseObject is string))
                        {
                            val = psObj.ImmediateBaseObject;
                        }
                    }
                    catch
                    {
                        val = "Failed to evaluate value.";
                    }

                    propsVariable.Add(new Variable(prop.Name, val.ToString(), val.GetType().ToString(), val is IEnumerable, val is PSObject));

                    if (!val.GetType().IsPrimitive)
                    {
                        string key = string.Format("{0}\\{1}", varName, prop.Name);
                        _propVariables[key] = val;
                    }
                }
            }

            return propsVariable;
        }

        /// <summary>
        /// Respond client request for callstack frames of current execution context
        /// </summary>
        /// <returns>Collection of callstack to client</returns>
        public IEnumerable<CallStack> GetCallStack()
        {
            ServiceCommon.Log("Obtaining the callstack");
            List<CallStack> callStackFrames = new List<CallStack>();

            foreach (var psobj in _callstack)
            {
                if (_runspace.ConnectionInfo == null)
                {
                    var frame = psobj.BaseObject as CallStackFrame;
                    if (frame != null)
                    {
                        callStackFrames.Add(new CallStack(frame.ScriptName, frame.FunctionName, frame.ScriptLineNumber));
                    }
                }
                else
                {
                    dynamic psFrame = (dynamic)psobj;

                    callStackFrames.Add(
                        new CallStack(
                            psFrame.ScriptName == null ? string.Empty : _mapRemoteToLocal[(string)psFrame.ScriptName.ToString()],
                            (string)psFrame.FunctionName.ToString(),
                            (int)psFrame.ScriptLineNumber));
                }
            }

            return callStackFrames;
        }

        /// <summary>
        /// Get prompt string
        /// </summary>
        /// <returns>Prompt string</returns>
        public string GetPrompt()
        {
            using (_currentPowerShell = PowerShell.Create())
            {
                _currentPowerShell.Runspace = _runspace;
                _currentPowerShell.AddCommand("prompt");

                string prompt = _currentPowerShell.Invoke<string>().FirstOrDefault();
                if (_runspace.ConnectionInfo != null)
                {
                    prompt = string.Format("[{0}] {1}", _runspace.ConnectionInfo.ComputerName, prompt);
                }

                return prompt;
            }
        }

        /// <summary>
        /// Client set resume action for debugger
        /// </summary>
        /// <param name="resumeAction">DebuggerResumeAction</param>
        public void SetDebuggerResumeAction(DebuggerResumeAction resumeAction)
        {
            lock (_executeDebugCommandLock)
            {
                ServiceCommon.Log("Client asks for resuming debugger");
                _resumeAction = resumeAction;
                _pausedEvent.Set();
            }
        }

        #endregion
    }
}
