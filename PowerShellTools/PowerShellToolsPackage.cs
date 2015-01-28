using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using EnvDTE;
using EnvDTE80;
using log4net;
using Microsoft;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudioTools;
using Microsoft.VisualStudioTools.Navigation;
using PowerShellTools.Classification;
using PowerShellTools.Commands;
using PowerShellTools.Common.ServiceManagement.IntelliSenseContract;
using PowerShellTools.DebugEngine;
using PowerShellTools.Diagnostics;
using PowerShellTools.LanguageService;
using PowerShellTools.Project;
using PowerShellTools.ServiceManagement;
using Engine = PowerShellTools.DebugEngine.Engine;

namespace PowerShellTools
{
    /// <summary>
    /// This is the class that implements the package exposed by this assembly.
    ///
    /// The minimum requirement for a class to be considered a valid package for Visual Studio
    /// is to implement the IVsPackage interface and register itself with the shell.
    /// This package uses the helper classes defined inside the Managed Package Framework (MPF)
    /// to do it: it derives from the Package class that provides the implementation of the 
    /// IVsPackage interface and uses the registration attributes defined in the framework to 
    /// register itself and its components with the shell.
    /// </summary>
    // This attribute tells the PkgDef creation utility (CreatePkgDef.exe) that this class is
    // a package.
    [PackageRegistration(UseManagedResourcesOnly = true)]
    // This attribute is used to register the information needed to show this package
    // in the Help/About dialog of Visual Studio.
    [InstalledProductRegistration("#110", "#112", "1.0", IconResourceID = 400)]
    [ProvideAutoLoad(UIContextGuids.NoSolution)]
    [ProvideAutoLoad(UIContextGuids.SolutionExists)]
    [ProvideLanguageService(typeof(PowerShellLanguageInfo), "PowerShell", 101, ShowDropDownOptions = true,
        EnableCommenting = true)]
    // This attribute is needed to let the shell know that this package exposes some menus.
    [ProvideMenuResource("Menus.ctmenu", 1)]
    // This attribute registers a tool window exposed by this package.
    //[ProvideEditorExtension(typeof(EditorFactory), ".xml", 50,
    //         ProjectGuid = "{A2FE74E1-B743-11d0-AE1A-00A0C90FFFC3}",
    //         TemplateDir = "Templates",
    //         NameResourceID = 105,
    //         DefaultName = "CustomEditor")]
    [ProvideKeyBindingTable(GuidList.guidCustomEditorEditorFactoryString, 102)]
    [Guid(GuidList.PowerShellToolsPackageGuid)]
    //[ProvideObject(typeof (PowerShellGeneralPropertyPage))]
    [ProvideObject(typeof(PowerShellModulePropertyPage))]
    [ProvideObject(typeof(PowerShellDebugPropertyPage))]
    [Microsoft.VisualStudio.Shell.ProvideDebugEngine("{43ACAB74-8226-4920-B489-BFCF05372437}", "PowerShell",
        PortSupplier = "{708C1ECA-FF48-11D2-904F-00C04FA302A1}",
        ProgramProvider = "{08F3B557-C153-4F6C-8745-227439E55E79}", Attach = true,
        CLSID = "{C7F9F131-53AB-4FD0-8517-E54D124EA392}")]
    [Clsid(Clsid = "{C7F9F131-53AB-4FD0-8517-E54D124EA392}", Assembly = "PowerGuiVsx.Core.DebugEngine",
        Class = "PowerGuiVsx.Core.DebugEngine.Engine")]
    [Clsid(Clsid = "{08F3B557-C153-4F6C-8745-227439E55E79}", Assembly = "PowerGuiVsx.Core.DebugEngine",
        Class = "PowerGuiVsx.Core.DebugEngine.ScriptProgramProvider")]
    [Microsoft.VisualStudioTools.ProvideDebugEngine("PowerShell", typeof(ScriptProgramProvider), typeof(Engine),
        "{43ACAB74-8226-4920-B489-BFCF05372437}")]
    [ProvideIncompatibleEngineInfo("{92EF0900-2251-11D2-B72E-0000F87572EF}")]
    //[ProvideIncompatibleEngineInfo("{449EC4CC-30D2-4032-9256-EE18EB41B62B}")]
    //[ProvideIncompatibleEngineInfo("{449EC4CC-30D2-4032-9256-EE18EB41B62B}")]
    [ProvideIncompatibleEngineInfo("{F200A7E7-DEA5-11D0-B854-00A0244A1DE2}")]
    [ProvideOptionPage(typeof(GeneralDialogPage), "PowerShell Tools", "General", 101, 106, true)]
    [ProvideOptionPage(typeof(DiagnosticsDialogPage), "PowerShell Tools", "Diagnostics", 101, 106, true)]
    [ProvideDiffSupportedContentType(".ps1;.psm1;.psd1", ";")]
    [ProvideLanguageExtension(typeof(PowerShellLanguageInfo), ".ps1")]
    [ProvideLanguageExtension(typeof(PowerShellLanguageInfo), ".psm1")]
    [ProvideLanguageExtension(typeof(PowerShellLanguageInfo), ".psd1")]
    [ProvideLanguageCodeExpansion(
         typeof(PowerShellLanguageInfo),
         "PowerShell",          // Name of language used as registry key
         0,                               // Resource ID of localized name of language service
         "PowerShell",        // Name of Language attribute in snippet template
         @"%TestDocs%\Code Snippets\PowerShel\SnippetsIndex.xml",  // Path to snippets index
         SearchPaths = @"%TestDocs%\Code Snippets\PowerShell\")]    // Path to snippets
    public sealed class PowerShellToolsPackage : CommonPackage
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(PowerShellToolsPackage));
        private static ConnectionManager _connectionManager;

        /// <summary>
        /// Default constructor of the package.
        /// Inside this method you can place any initialization code that does not require 
        /// any Visual Studio service because at this point the package object is created but 
        /// not sited yet inside Visual Studio environment. The place to do all the other 
        /// initialization is the Initialize method.
        /// </summary>
        public PowerShellToolsPackage()
        {
            Log.Info(string.Format(CultureInfo.CurrentCulture, "Entering constructor for: {0}", this));
            Instance = this;
            _commands = new Dictionary<ICommand, MenuCommand>();
        }

        private ITextBufferFactoryService _textBufferFactoryService;
        private static Dictionary<ICommand, MenuCommand> _commands;
        private static GotoDefinitionCommand _gotoDefinitionCommand;
        private VisualStudioEvents _visualStudioEvents;

        /// <summary>
        /// Returns the PowerShell host for the package.
        /// </summary>
        internal static ScriptDebugger Debugger { get; private set; }

        /// <summary>
        /// Returns the current package instance.
        /// </summary>
        public static PowerShellToolsPackage Instance { get; private set; }

        internal static IPowershellIntelliSenseService IntelliSenseService
        { 
            get
            {
                return _connectionManager.PowershellIntelliSenseSerivce;
            }
        }

        public new object GetService(Type type)
        {
            return base.GetService(type);
        }

        public override Type GetLibraryManagerType()
        {
            return null;
        }

        internal override LibraryManager CreateLibraryManager(CommonPackage package)
        {
            throw new NotImplementedException();
        }

        public override bool IsRecognizedFile(string filename)
        {
            throw new NotImplementedException();
        }

        private void RefreshCommands(params ICommand[] commands)
        {
            // Add our command handlers for menu (commands must exist in the .vsct file)
            var mcs = GetService(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (null != mcs)
            {
                foreach (var command in commands)
                {
                    var menuCommand = new OleMenuCommand(command.Execute, command.CommandId);
                    menuCommand.BeforeQueryStatus += command.QueryStatus;
                    mcs.AddCommand(menuCommand);
                    _commands[command] = menuCommand;
                }
            }
        }

        private void InitializeInternal()
        {
            var page = (DiagnosticsDialogPage)GetDialogPage(typeof(DiagnosticsDialogPage));

            if (page.EnableDiagnosticLogging)
            {
                DiagnosticConfiguration.EnableDiagnostics();
            }

            Log.Info(string.Format(CultureInfo.CurrentCulture, "Entering Initialize() of: {0}", this));
            base.Initialize();

            var langService = new PowerShellLanguageInfo(this);
            ((IServiceContainer)this).AddService(langService.GetType(), langService, true);

            var componentModel = (IComponentModel)GetGlobalService(typeof(SComponentModel));
            _textBufferFactoryService = componentModel.GetService<ITextBufferFactoryService>();
            EditorImports.ClassificationTypeRegistryService = componentModel.GetService<IClassificationTypeRegistryService>();
            EditorImports.ClassificationFormatMap = componentModel.GetService<IClassificationFormatMapService>();
            _visualStudioEvents = componentModel.GetService<VisualStudioEvents>();

            _visualStudioEvents.SettingsChanged += _visualStudioEvents_SettingsChanged;

            if (_textBufferFactoryService != null)
            {
                _textBufferFactoryService.TextBufferCreated += TextBufferFactoryService_TextBufferCreated;
            }

            InitializePowerShellHost();
            EstablishServiceConnection();

            _gotoDefinitionCommand = new GotoDefinitionCommand();
            RefreshCommands(new ExecuteSelectionCommand(),
                            new ExecuteAsScriptCommand(),
                            _gotoDefinitionCommand,
                            new PrettyPrintCommand(Debugger.Runspace),
                            new OpenDebugReplCommand());
        }

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited, so this is the place
        /// where you can put all the initialization code that rely on services provided by VisualStudio.
        /// </summary>
        protected override void Initialize()
        {
            try
            {
                InitializeInternal();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to initialize PowerShell Tools for Visual Studio." + ex,
                    "PowerShell Tools for Visual Studio Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private IContentType _contentType;
        public IContentType ContentType
        {
            get
            {
                if (_contentType == null)
                {
                    _contentType = ComponentModel.GetService<IContentTypeRegistryService>().GetContentType(PowerShellConstants.LanguageName);
                }
                return _contentType;
            }
        }

        void _visualStudioEvents_SettingsChanged(object sender, DialogPage e)
        {
            if (e is DiagnosticsDialogPage)
            {
                var page = (DiagnosticsDialogPage)e;
                if (page.EnableDiagnosticLogging)
                {
                    DiagnosticConfiguration.EnableDiagnostics();
                }
                else
                {
                    DiagnosticConfiguration.DisableDiagnostics();
                }
            }
        }

        private static void TextBufferFactoryService_TextBufferCreated(object sender, TextBufferCreatedEventArgs e)
        {
            e.TextBuffer.ContentTypeChanged += TextBuffer_ContentTypeChanged;

            if (e.TextBuffer.ContentType.IsOfType("PowerShell"))
            {
                var psts = new PowerShellTokenizationService(e.TextBuffer);
                _gotoDefinitionCommand.AddTextBuffer(e.TextBuffer);
                e.TextBuffer.ChangedLowPriority += (o, args) => psts.StartTokenization();
                e.TextBuffer.Properties.AddProperty("HasTokenizer", true);
            }
        }

        static void TextBuffer_ContentTypeChanged(object sender, ContentTypeChangedEventArgs e)
        {
            var buffer = sender as ITextBuffer;
            if (buffer == null) return;

            if (e.AfterContentType.IsOfType("PowerShell") && !buffer.Properties.ContainsProperty("HasTokenizer"))
            {
                var psts = new PowerShellTokenizationService(buffer);
                _gotoDefinitionCommand.AddTextBuffer(buffer);
                buffer.ChangedLowPriority += (o, args) => psts.StartTokenization();
                buffer.Properties.AddProperty("HasTokenizer", true);
            }
        }

        /// <summary>
        /// Initialize the PowerShell host.
        /// </summary>
        private void InitializePowerShellHost()
        {
            var page = (GeneralDialogPage)GetDialogPage(typeof(GeneralDialogPage));

            Log.Info("InitializePowerShellHost");

            Debugger = new ScriptDebugger(page.OverrideExecutionPolicyConfiguration, (DTE2)GetService(typeof(DTE)));
            Debugger.HostUi.OutputProgress = (label, percentage) =>
            {
                Log.DebugFormat("Output progress: {0} {1}", label, percentage);
                var statusBar = (IVsStatusbar)GetService(typeof(SVsStatusbar));
                uint cookie = 0;
                statusBar.Progress(ref cookie, 1, label, (uint)percentage, 100);

                if (percentage == 100)
                {
                    statusBar.Progress(ref cookie, 1, "", 0, 0);
                }
            };
        }

        private void EstablishServiceConnection()
        {
            _connectionManager = ConnectionManager.Instance;
        }

        public T GetDialogPage<T>() where T : DialogPage
        {
            return (T)GetDialogPage(typeof(T));
        }
    }
}
