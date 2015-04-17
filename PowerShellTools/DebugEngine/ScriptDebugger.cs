using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Threading;
using log4net;
using System.Collections.ObjectModel;
using PowerShellTools.Common.ServiceManagement.DebuggingContract;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using DTE = EnvDTE;
using DTE80 = EnvDTE80;
using PowerShellTools.Common.Debugging;

namespace PowerShellTools.DebugEngine
{
    public class EventArgs<T> : EventArgs
    {
        public EventArgs(T value)
        {
            Value = value;
        }

        public T Value { get; private set; }
    }

    /// <summary>
    /// This is the main debugger for PowerShell Tools for Visual Studio
    /// </summary>
    public partial class ScriptDebugger
    {
        private List<ScriptStackFrame> _callstack;
        private readonly AutoResetEvent _stoppingCompleteEvent = new AutoResetEvent(false);
        private static readonly ILog Log = LogManager.GetLogger(typeof(ScriptDebugger));

        /// <summary>
        /// Event is fired when a debugger is paused.
        /// </summary>
        public event EventHandler<EventArgs<ScriptLocation>> DebuggerPaused;

        /// <summary>
        /// Event is fired when a string is output from the PowerShell host.
        /// </summary>
        public event EventHandler<EventArgs<string>> OutputString;

        /// <summary>
        /// Event is fired when the debugger has finished.
        /// </summary>
        public event EventHandler DebuggingFinished;

        /// <summary>
        /// Event is fired when the debugger has began.
        /// </summary>
        public event EventHandler DebuggingBegin;

        /// <summary>
        /// Event is fired when a terminating exception is thrown.
        /// </summary>
        public event EventHandler<EventArgs<PowerShellRunTerminatingException>> TerminatingException;

        /// <summary>
        /// The current set of variables for the current runspace.
        /// </summary>
        public IDictionary<string, Variable> Variables { get; private set; }

        /// <summary>
        /// The current call stack for the runspace.
        /// </summary>
        public IEnumerable<ScriptStackFrame> CallStack { get { return _callstack; } }

        /// <summary>
        /// The currently executing <see cref="ScriptProgramNode"/>
        /// </summary>
        public ScriptProgramNode CurrentExecutingNode { get; private set; }

        /// <summary>
        /// Indicate if debugger is ready for accepting command
        /// </summary>
        public bool IsDebuggingCommandReady { get; private set; }

        /// <summary>
        /// Indicate if there is on-going debugging, coz we should only allow one debugging session 
        /// </summary>
        public bool IsDebugging { get; set; }

        /// <summary>
        /// Indicate if runspace is hosting remote session
        /// </summary>
        public bool RemoteSession { get; set; }

        public BreakpointManager BreakpointManager { get; set; }

        public string DebuggingCommand { get; set; }

        #region Debugging service event handlers

        /// <summary>
        /// Debugger stopped handler
        /// </summary>
        /// <param name="e"></param>
        public void DebuggerStop(DebuggerStoppedEventArgs e)
        {
            Log.InfoFormat("Debugger stopped");
            try
            {
                RefreshScopedVariables();
                RefreshCallStack();

                if (!BreakpointManager.ProcessLineBreakpoints(e.ScriptFullPath, e.Line, e.Column))
                {
                    if (DebuggerPaused != null)
                    {
                        var scriptLocation = new ScriptLocation(e.ScriptFullPath, e.Line, 0);

                        DebuggerPaused(this, new EventArgs<ScriptLocation>(scriptLocation));
                    }
                }
            }
            catch (DebugEngineInternalException dbgEx)
            {
                Log.Debug(dbgEx.Message);
                DebuggingService.SetDebuggerResumeAction(DebugEngineConstants.Debugger_Stop);

                IsDebuggingCommandReady = false;
            }
            catch (Exception ex)
            {
                Log.Debug(ex.Message);
                DebuggingService.SetDebuggerResumeAction(DebugEngineConstants.Debugger_Stop);

                IsDebuggingCommandReady = false;
                throw;
            }
            finally
            {
                Log.Debug("Waiting for debuggee to resume.");

                IsDebuggingCommandReady = true;
                RefreshPrompt();
            }
        }

        /// <summary>
        /// PS execution terminating excpetion handler
        /// </summary>
        /// <param name="ex"></param>
        public void TerminateException(PowerShellRunTerminatingException ex)
        {
            if (TerminatingException != null)
            {
                // from editor debug run
                TerminatingException(this, new EventArgs<PowerShellRunTerminatingException>(ex));
            }
            else
            {
                // from REPL execution
                HostUi.VsOutputString(ex.Message);
            }
        }

        /// <summary>
        /// PSDebugger event finished handler
        /// </summary>
        public void DebuggerFinished()
        {
            IsDebuggingCommandReady = false;

            if (DebuggingFinished != null)
            {
                DebuggingFinished(this, new EventArgs());
                _stoppingCompleteEvent.Set();
            }
        }

        public void DebuggerBegin()
        {
            if (DebuggingBegin != null)
            {
                DebuggingBegin(this, EventArgs.Empty);
            }
        }

        private void ConnectionExceptionHandler(object sender, EventArgs e)
        {
            Log.Error("Connection to host service is broken, terminating debugging.");
            DebuggerFinished();
        }

        #endregion

        /// <summary>
        /// Retrieve local scoped variable from debugger(in PSHost proc)
        /// </summary>
        private void RefreshScopedVariables()
        {
            try
            {
                Collection<Variable> vars = DebuggingService.GetScopedVariable();
                Variables = new Dictionary<string, Variable>();
                foreach (Variable v in vars)
                {
                    Variables.Add(v.VarName, v);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Failed to refresh scoped variables.", ex);
                throw;
            }
        }

        /// <summary>
        /// Retrieve callstack info from debugger(in PSHost proc)
        /// </summary>
        private void RefreshCallStack()
        {
            IEnumerable<CallStack> result = null;
            try
            {
                if (IsDebugging)
                {
                    result = DebuggingService.GetCallStack();
                }
                else
                {
                    throw new DebugEngineInternalException();
                }

                _callstack = new List<ScriptStackFrame>();
                if (result == null) return;

                foreach (var psobj in result)
                {
                    _callstack.Add(new ScriptStackFrame(CurrentExecutingNode, psobj.ScriptFullPath, psobj.Line, psobj.FrameString));
                }
            }
            catch (Exception ex)
            {
                Log.Error("Failed to refresh callstack", ex);
                throw;
            }
        }

        /// <summary>
        /// Stops execution of the current script.
        /// </summary>
        public void Stop()
        {
            Log.Info("Stop");

            try
            {
                _stoppingCompleteEvent.Reset();
                if (IsDebuggingCommandReady)
                {
                    DebuggingService.SetDebuggerResumeAction(DebugEngineConstants.Debugger_Stop);
                    IsDebuggingCommandReady = false;
                }
                else
                {
                    DebuggingService.Stop();
                }
                _stoppingCompleteEvent.WaitOne();
            }
            catch (Exception ex)
            {
                //BUGBUG: Suppressing an exception that is thrown when stopping...
                Log.Debug("Error while stopping script...", ex);
            }
            finally
            {
                DebuggerFinished();
            }
        }

        /// <summary>
        /// Stop over block. 
        /// </summary>
        public void StepOver()
        {
            Log.Info("StepOver");
            DebuggingService.SetDebuggerResumeAction(DebugEngineConstants.Debugger_StepOver);
            IsDebuggingCommandReady = false;
        }

        /// <summary>
        /// Step into block.
        /// </summary>
        public void StepInto()
        {
            Log.Info("StepInto");
            DebuggingService.SetDebuggerResumeAction(DebugEngineConstants.Debugger_StepInto);
            IsDebuggingCommandReady = false;
        }

        /// <summary>
        /// Step out of block.
        /// </summary>
        public void StepOut()
        {
            Log.Info("StepOut");
            DebuggingService.SetDebuggerResumeAction(DebugEngineConstants.Debugger_StepOut);
            IsDebuggingCommandReady = false;
        }

        /// <summary>
        /// Continue execution.
        /// </summary>
        public void Continue()
        {
            Log.Info("Continue");
            DebuggingService.SetDebuggerResumeAction(DebugEngineConstants.Debugger_Continue);
            IsDebuggingCommandReady = false;
        }

        /// <summary>
        /// Execute the specified command line.
        /// </summary>
        /// <param name="commandLine">Command line to execute.</param>
        public bool Execute(string commandLine)
        {
            Log.Info("Execute");

            try
            {
                return ExecuteInternal(commandLine);
            }
            catch (Exception ex)
            {
                Log.Error("Failed to execute script", ex);
                HostUi.VsOutputString(ex.Message);
                return false;
            }
            finally
            {
                DebuggerFinished();
            }
        }

        /// <summary>
        /// Execute the specified command line 
        /// </summary>
        /// <param name="commandLine">Command line to execute.</param>
        public bool ExecuteInternal(string commandLine)
        {
            IsDebuggingCommandReady = false;
            return DebuggingService.Execute(commandLine);
        }

        /// <summary>
        /// Execute the specified command line as debugging command.
        /// </summary>
        /// <param name="commandLine">Command line to execute.</param>
        public void ExecuteDebuggingCommand(string commandLine)
        {
            Log.Info("Execute debugging command");

            if (IsDebuggingCommandReady)
            {
                try
                {
                    DebuggingService.ExecuteDebuggingCommandOutDefault(commandLine);
                }
                catch (Exception ex)
                {
                    Log.Error("Failed to execute debugging command", ex);
                }
            }
        }

        /// <summary>
        /// Execute the current program node.
        /// </summary>
        /// <remarks>
        /// The node will either be a script file or script content; depending on the node 
        /// passed to this function.
        /// </remarks>
        /// <param name="node"></param>
        public void Execute(ScriptProgramNode node)
        {
            CurrentExecutingNode = node;

            if (node.IsAttachedProgram)
            {
                Execute(String.Format("Enter-PSHostProcess -Id {0};", node.Process.ProcessId));
                Execute("Debug-Runspace 1");
            }
            else
            {
                string commandLine = node.FileName;

                if (node.IsFile)
                {
                    commandLine = String.Format(DebugEngineConstants.ExecutionCommandFormat, node.FileName, node.Arguments);
                }
                Execute(commandLine);
            }
        }

        public void SetVariable(string name, string value)
        {
            try
            {
                using (var pipeline = (_runspace.CreateNestedPipeline()))
                {
                    var command = new Command("Set-Variable");
                    command.Parameters.Add("Name", name);
                    command.Parameters.Add("Value", value);

                    pipeline.Commands.Add(command);
                    pipeline.Invoke();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Failed to set variable.", ex);
            }
        }

        public Variable GetVariable(string name)
        {
            if (name.StartsWith("$"))
            {
                name = name.Remove(0, 1);
            }

            if (Variables.ContainsKey(name))
            {
                var var = Variables[name];
                return var;
            }

            return null;
        }

        public void SignalStoppingComplete()
        {
            _stoppingCompleteEvent.Set();
        }

        internal void OpenRemoteFile(string fullName)
        {
            var dte2 = (DTE80.DTE2)Package.GetGlobalService(typeof(DTE.DTE));

            if (dte2 != null)
            {
                try
                {
                    dte2.ItemOperations.OpenFile(fullName);
                }
                catch (Exception ex)
                {
                    Log.Error("Failed to open remote file through powershell remote session", ex);
                    HostUi.VsOutputString(ex.Message);
                }
            }
        }
    }

    /// <summary>
    /// Location within a script.
    /// </summary>
    public class ScriptLocation
    {
        /// <summary>
        /// The full path to the file.
        /// </summary>
        public string File { get; set; }
        /// <summary>
        /// Line number within the file.
        /// </summary>
        public int Line { get; set; }
        /// <summary>
        /// Column within the file.
        /// </summary>
        public int Column { get; set; }

        public ScriptLocation(string file, int line, int column)
        {
            File = file;
            Line = line;
            Column = column;
        }
    }
}