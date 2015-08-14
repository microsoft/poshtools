using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PowerShellTools.DebugEngine.RunspacePickerUI
{
    class RunspacePickerWindowViewModel
    {
        private IList<RunspaceInfo> _runspaces;

        public IList<RunspaceInfo> Runspaces
        {
            get
            {
                return _runspaces;
            }
        }

        public RunspacePickerWindowViewModel(IList<RunspaceInfo> runspaces)
        {
            _runspaces = runspaces;
        }
    }

    /// <summary>
    /// Wrapper class that contains all of the various runspace properties we want to display.
    /// </summary>
    class RunspaceInfo
    {
        public int Id { get; set; }

        public string Name  { get; set; }

        public string ComputerName { get; set; }

        public string State { get; set; }

        public string Availability { get; set; }

        public RunspaceInfo(int id, string name, string computerName, string state, string availability)
        {
            Id = id;
            Name = name;
            ComputerName = computerName;
            State = state;
            Availability = availability;
        }
    }
}
