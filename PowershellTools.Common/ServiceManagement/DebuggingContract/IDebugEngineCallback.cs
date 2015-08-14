using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Runspaces;
using System.ServiceModel;
using System.Text;
using System.Threading.Tasks;

namespace PowerShellTools.Common.ServiceManagement.DebuggingContract
{
    public interface IDebugEngineCallback
    {
        [OperationContract(IsOneWay = true)]
        void DebuggerStopped(DebuggerStoppedEventArgs args);

        [OperationContract(IsOneWay = true)]
        void BreakpointUpdated(DebuggerBreakpointUpdatedEventArgs args);

        [OperationContract(IsOneWay = false)]
        void OutputString(string output);

        [OperationContract(IsOneWay = false)]
        void OutputStringLine(string output);

        [OperationContract(IsOneWay = true)]
        [ServiceKnownType(typeof(RemotingProgressRecord))]
        void OutputProgress(long sourceId, ProgressRecord record);

        [OperationContract(IsOneWay = false)]
        void TerminatingException(PowerShellRunTerminatingException ex);

        [OperationContract(IsOneWay = true)]
        void DebuggerFinished();

        [OperationContract(IsOneWay = true)]
        void RefreshPrompt();

        [OperationContract(IsOneWay = false)]
        string ReadHostPrompt(string message, string name);

        [OperationContract(IsOneWay = false)]
        int ReadHostPromptForChoices(string caption, string message, IList<ChoiceItem> choices, int defaultChoice);

        [OperationContract(IsOneWay = false)]
        PSCredential ReadSecureStringPrompt(
            string message, 
            string name);

        [OperationContract(IsOneWay = false)]
        PSCredential GetPSCredentialPrompt(
            string caption, 
            string message, 
            string userName,
            string targetName, 
            PSCredentialTypes allowedCredentialTypes, 
            PSCredentialUIOptions options);

        [OperationContract(IsOneWay = false)]
        void RequestUserInputOnStdIn();

        [OperationContract(IsOneWay = false)]
        void OpenRemoteFile(string fullName);

        [OperationContract(IsOneWay = false)]
        void SetRemoteRunspace(bool enabled);

        [OperationContract(IsOneWay = false)]
        void ClearHostScreen();

        [OperationContract(IsOneWay = false)]
        VsKeyInfo VsReadKey();

        [OperationContract(IsOneWay = false)]
        bool IsKeyAvailable();

        [OperationContract(IsOneWay = false)]
        int GetREPLWindowWidth();

        [OperationContract]
        int PromptUserToPickRunspace(Collection<PSObject> runspaces);
    }
}
