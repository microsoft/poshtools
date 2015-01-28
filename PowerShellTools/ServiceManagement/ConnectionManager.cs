using System;
using System.ServiceModel;
using PowerShellTools.Common;
using PowerShellTools.Common.ServiceManagement.IntelliSenseContract;

namespace PowerShellTools.ServiceManagement
{
    /// <summary>
    /// Manage the process and channel creation.
    /// </summary>
    internal sealed class ConnectionManager
    {
        private IPowershellIntelliSenseService _powershellIntelliSenseService;
        private static object _syncObject = new object();
        private static ConnectionManager _instance;

        private ConnectionManager()
        {
            OpenClientConnection();
        }

        public static ConnectionManager Instance 
        { 
            get
            {
                if (_instance == null)
                {
                    _instance = new ConnectionManager();
                }
                return _instance;
            }
        }

        public IPowershellIntelliSenseService PowershellIntelliSenseSerivce
        {
            get
            {
                lock (_syncObject)
                {
                    if (_powershellIntelliSenseService == null)
                    {
                        OpenClientConnection();
                    }
                }
                
                return _powershellIntelliSenseService;
            }
        }

        private void OpenClientConnection()
        {
            var hostProcess = PowershellHostProcessFactory.Instance.HostProcess;

            // net.pipe://localhost/UniqueEndpointGuid/NamedPipePowershellProcess
            var endPointAddress = Constants.ProcessManagerHostUri + hostProcess.EndpointGuid + "/" + Constants.ProcessManagerHostRelativeUri;

            try
            {
                if (_powershellIntelliSenseService == null)
                {
                    var factoryMaker = new ChannelFactoryBuilder<IPowershellIntelliSenseService>();
                    var channelFactory = factoryMaker.CreateChannelFactory(endPointAddress);
                    channelFactory.Faulted += (s, e) => {              
                        ((ChannelFactory<IPowershellIntelliSenseService>)s).Abort();
                        _powershellIntelliSenseService = null;
                    };
                    channelFactory.Closed += (s, e) => {
                        ((ChannelFactory<IPowershellIntelliSenseService>)s).Abort();
                        _powershellIntelliSenseService = null;
                    };
                    channelFactory.Open();
                    _powershellIntelliSenseService = channelFactory.CreateChannel();

                    hostProcess.Process.Exited += (s, e) => {
                        channelFactory.Abort();
                        channelFactory = null;
                        _powershellIntelliSenseService = null;
                    };
                }
            }
            catch
            {
                // Connection has to be established...
                _powershellIntelliSenseService = null;
                throw;
            }
        }
    }
}
