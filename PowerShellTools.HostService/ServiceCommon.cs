using Microsoft.PowerShell;
using PowerShellTools.Common.Debugging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PowerShellTools.HostService
{
    public class ServiceCommon
    {
        public static object RunspaceLock = new object();

        /// <summary>
        /// TODO: Temporary logging before having logging infrastructure ready
        /// </summary>
        /// <param name="msg"></param>
        /// <param name="args"></param>
        public static void Log(string msg, params object[] args)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Log(string.Format(msg, args));
            Console.ResetColor();
        }

        /// <summary>
        /// TODO: Temporary logging before having logging infrastructure ready
        /// </summary>
        /// <param name="msg"></param>
        /// <param name="args"></param>
        public static void LogCallbackEvent(string msg, params object[] args)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Log(string.Format(msg, args));
            Console.ResetColor();
        }

        /// <summary>
        /// TODO: Temporary logging before having logging infrastructure ready
        /// </summary>
        /// <param name="msg"></param>
        private static void Log(string msg)
        {
            Console.WriteLine(string.Format(DebugEngineConstants.PowerShellHostProcessLogFormat, Program.EndpointGuid, msg));
        }
    }
}
