using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.VisualStudio.PlatformUI;
using PowerShellTools.Common;

namespace PowerShellTools.DebugEngine.RunspacePickerUI
{
    /// <summary>
    /// Interaction logic for RunspacePicker.xaml
    /// </summary>
    internal partial class RunspacePickerWindow : VsShellDialogWindow
    {
        public RunspacePickerWindow(RunspacePickerWindowViewModel viewModel)
        {
            if (viewModel == null)
            {
                throw new ArgumentNullException("viewModel");
            }

            InitializeComponent();
            RunspacesListView.ItemsSource = viewModel.Runspaces;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
