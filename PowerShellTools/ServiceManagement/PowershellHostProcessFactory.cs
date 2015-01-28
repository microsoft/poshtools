using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using PowerShellTools.Common;

namespace PowerShellTools.ServiceManagement
{
    /// <summary>
    /// Initilize a process for hosting the WCF service.
    /// </summary>
    internal sealed class PowershellHostProcessFactory
    {
        private Lazy<PowershellHostProcess> _powershellHostProcess;
        private static PowershellHostProcessFactory _factory;
        private static object _syncObject = new object();

        private PowershellHostProcessFactory()
        {
            LazyCreatePowershellHostProcess();
        }

        public static PowershellHostProcessFactory Instance
        {
            get
            {
                if (_factory == null)
                {
                    _factory = new PowershellHostProcessFactory();
                }
                return _factory;
            }
        }
        /// <summary>
        /// The host process we want.
        /// </summary>
        public PowershellHostProcess HostProcess
        {
            get
            {
                lock (_syncObject)
                {
                    if (_powershellHostProcess == null)
                    {
                        LazyCreatePowershellHostProcess();
                    }
                }
                
                return _powershellHostProcess.Value;
            }
        }

        private PowershellHostProcess CreatePowershellHostProcess()
        {
            Process powershellHostProcess = new Process();
            string hostProcessReadyEventName = Constants.ReadyEventPrefix + Guid.NewGuid();
            Guid endPointGuid = Guid.NewGuid();

            string exeName = Constants.PowershellHostExeName;
            string currentPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(currentPath, exeName);
            string hostArgs = String.Format(CultureInfo.InvariantCulture,
                                            "{0}{1} {2}{3} {4}{5}",
                                            Constants.UniqueEndpointArg, endPointGuid, // For generating a unique endpoint address 
                                            Constants.VsProcessIdArg, Process.GetCurrentProcess().Id,
                                            Constants.ReadyEventUniqueNameArg, hostProcessReadyEventName);

            powershellHostProcess.StartInfo.Arguments = hostArgs;
            powershellHostProcess.StartInfo.FileName = path;
            powershellHostProcess.StartInfo.CreateNoWindow = true;
            powershellHostProcess.StartInfo.UseShellExecute = true;
            powershellHostProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;

            EventWaitHandle readyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, hostProcessReadyEventName);

            powershellHostProcess.Start();
            powershellHostProcess.EnableRaisingEvents = true;
            powershellHostProcess.Exited += PowershellHostProcess_Exited;
            bool success = readyEvent.WaitOne(Constants.HostProcessStartupTimeout, false);
            readyEvent.Close();

            if (!success)
            {
                try
                {
                    powershellHostProcess.Kill();
                }
                catch (Exception)
                {
                }

                if (powershellHostProcess != null)
                {
                    powershellHostProcess.Dispose();
                    powershellHostProcess = null;
                }
                throw new PowershellHostProcessException(String.Format(CultureInfo.CurrentCulture,
                                                                       Resources.ErrorFailToCreateProcess,
                                                                       powershellHostProcess.Id));
            }

            return new PowershellHostProcess
            {
                Process = powershellHostProcess,
                EndpointGuid = endPointGuid
            };

        }

        /// <summary>
        /// In case the process is terminated somehow, such as manually ended by users, we need to re-create the process as long as VS process is still running.
        /// </summary>
        /// <param name="sender">The scource of the event.</param>
        /// <param name="e">An System.EventArgs that contains no event data.</param>
        private void PowershellHostProcess_Exited(object sender, EventArgs e)
        {
            Process p = sender as Process;

            lock (_syncObject)
            {
                if (_powershellHostProcess.IsValueCreated && _powershellHostProcess.Value.Process == p)
                {
                    LazyCreatePowershellHostProcess();
                }
            }
        }

        private void LazyCreatePowershellHostProcess()
        {
            _powershellHostProcess = new Lazy<PowershellHostProcess>(CreatePowershellHostProcess);
        }

        /// <summary>
        /// The structure containing the process we want and a guid used for the WCF client to establish connection to the service.
        /// </summary>
        internal struct PowershellHostProcess
        {
            public Process Process { get; set; }
            public Guid EndpointGuid { get; set; }
        }
    }
}
