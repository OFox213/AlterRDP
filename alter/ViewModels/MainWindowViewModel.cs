using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MsRdcAx;
using MsRdcAx.AxMsTscLib;
using AlterApp.Services.Interfaces;
using AlterApp.ViewModels.Interfaces;
using AlterApp.Models;

namespace AlterApp.ViewModels
{
    internal partial class MainWindowViewModel : ObservableObject, IWindowClosing, IWindowContentRendered
    {
        private readonly IMainWindowViewModelService _viewModelService;
        private readonly IAppSettingsService _appSettingsService;
        private readonly bool _shouldStartConnect;
        private RdpClientHost? _rdpClientHost;
        private string _remotePort = string.Empty;
        private RdpClientConnectionState _rdpClientConnectionState = RdpClientConnectionState.NotConnected;
        private RdpClientDisconnectReason _rdpClientLastDisconnectReason = new();
        private bool _shouldShowDisconnectReasonDetails;
        private bool _isElementEnabled = true;
        private Visibility _rdpClientHostVisibility = Visibility.Hidden;

        public MainWindowViewModel(
            IMainWindowViewModelService viewModelService,
            IAppSettingsService appSettingsService,
            ICommandLineArgsService commandLineArgsService,
            IUsageNoticeService usageNoticeService)
        {
            _viewModelService = viewModelService;
            _appSettingsService = appSettingsService;
            SessionOptions = LoadSessionOptions();
            SessionOptions.PropertyChanged += SessionOptions_PropertyChanged;

            RdpClientHost = _viewModelService.GetRdpClientInstance();
            RdpClientHost.OnConnecting += RdpClientHost_OnConnecting;
            RdpClientHost.OnConnected += RdpClientHost_OnConnected;
            RdpClientHost.OnDisconnected += RdpClientHost_OnDisconnected;

            WindowWidth = _appSettingsService.GetSettingValue("mainWindow.width", AppConstants.DefaultMainWindowWidth);
            WindowHeight = _appSettingsService.GetSettingValue("mainWindow.height", AppConstants.DefaultMainWindowHeight);
            RemoteComputer = commandLineArgsService.RemoteComputer ?? string.Empty;
            RemotePort = commandLineArgsService.RemotePort ?? _appSettingsService.GetSettingValue("defaultRdpPort", AppConstants.DefaultRdpPort).ToString();
            UserName = commandLineArgsService.UserName ?? string.Empty;
            ConnectionTitle = commandLineArgsService.ConnectionTitle ?? string.Empty;
            _shouldStartConnect = !commandLineArgsService.ShouldShowUsage && commandLineArgsService.ShouldStartConnect;

            RefreshRdpClientHostPresentation();

            if (commandLineArgsService.ShouldShowUsage)
            {
                usageNoticeService.ShowUsage();
            }
        }

        public RdpSessionOptions SessionOptions { get; }

        public IReadOnlyList<KeyboardHookModeOption> KeyboardHookModeOptions { get; } = new[]
        {
            new KeyboardHookModeOption(RdpKeyboardHookMode.OnServerInFullScreen, "Send to remote session in full-screen"),
            new KeyboardHookModeOption(RdpKeyboardHookMode.OnServer, "Send to remote session always"),
            new KeyboardHookModeOption(RdpKeyboardHookMode.OnClient, "Keep on the local computer"),
        };

        public bool IsConnectionBarPinOptionEnabled => SessionOptions.UseConnectionBar;

        public bool OnClosing()
        {
            SaveSessionOptions();
            _appSettingsService.SetSettingValue("mainWindow.width", WindowWidth);
            _appSettingsService.SetSettingValue("mainWindow.height", WindowHeight);
            return false;
        }

        public void UpdateWindowBoundsForPersistence(double width, double height)
        {
            WindowWidth = width;
            WindowHeight = height;
        }

        public void OnContentRendered()
        {
            if (_shouldStartConnect && ConnectToRemoteComputerCommand.CanExecute(null))
            {
                ConnectToRemoteComputerCommand.Execute(null);
            }
        }

        [ObservableProperty]
        private double _windowWidth;

        [ObservableProperty]
        private double _windowHeight;

        [ObservableProperty]
        private MainWindowDisplayMode _windowDisplayMode = MainWindowDisplayMode.Normal;

        public static double WindowMinWidth => AppConstants.MainWindowMinWidth;

        public static double WindowMinHeight => AppConstants.MainWindowMinHeight;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(WindowTitle))]
        [NotifyCanExecuteChangedFor(nameof(ConnectToRemoteComputerCommand))]
        private string _remoteComputer = string.Empty;

        public string RemotePort
        {
            get => _remotePort;
            set
            {
                if (value.Length == 0 || _viewModelService.IsValidRemotePort(value))
                {
                    SetProperty(ref _remotePort, value);
                    OnPropertyChanged(nameof(WindowTitle));
                    ConnectToRemoteComputerCommand.NotifyCanExecuteChanged();
                    RefreshRdpClientHostPresentation();
                }
            }
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(WindowTitle))]
        [NotifyCanExecuteChangedFor(nameof(ConnectToRemoteComputerCommand))]
        private string _userName = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(WindowTitle))]
        private string _connectionTitle = string.Empty;

        public string WindowTitle => _viewModelService.GetWindowTitle(ConnectionTitle, RemoteComputer, RemotePort, UserName);

        public RdpClientHost? RdpClientHost
        {
            get => _rdpClientHost;
            private set => SetProperty(ref _rdpClientHost, value);
        }

        [ObservableProperty]
        private double _rdpClientHostWidth;

        [ObservableProperty]
        private double _rdpClientHostHeight;

        public RdpClientConnectionState RdpClientConnectionState
        {
            get => _rdpClientConnectionState;
            private set => SetProperty(ref _rdpClientConnectionState, value);
        }

        public RdpClientDisconnectReason RdpClientLastDisconnectReason
        {
            get => _rdpClientLastDisconnectReason;
            private set
            {
                SetProperty(ref _rdpClientLastDisconnectReason, value);
                OnPropertyChanged(nameof(ShouldShowDisconnectReason));
            }
        }

        public bool ShouldShowDisconnectReason => _viewModelService.ShouldShowDisconnectReason(RdpClientLastDisconnectReason);

        public bool ShouldShowDisconnectReasonDetails
        {
            get => _shouldShowDisconnectReasonDetails;
            private set => SetProperty(ref _shouldShowDisconnectReasonDetails, value);
        }

        public bool IsElementEnabled
        {
            get => _isElementEnabled;
            private set => SetProperty(ref _isElementEnabled, value);
        }

        public Visibility RdpClientHostVisibility
        {
            get => _rdpClientHostVisibility;
            private set
            {
                SetProperty(ref _rdpClientHostVisibility, value);
                RefreshRdpClientHostPresentation();
            }
        }

        public string VersionInfoText => _viewModelService.GetVersionInfoText();

        public void SetWindowDisplayMode(MainWindowDisplayMode displayMode)
        {
            WindowDisplayMode = displayMode;
        }

        [RelayCommand]
        private void ToggleDisconnectReasonDetailsVisibility()
        {
            ShouldShowDisconnectReasonDetails = !ShouldShowDisconnectReasonDetails;
        }

        [RelayCommand(CanExecute = nameof(CanConnectToRemoteComputer))]
        private void ConnectToRemoteComputer()
        {
            SwitchToRdpClientView();
            RdpClientLastDisconnectReason = new();
            StartConnect();
        }

        [RelayCommand]
        private void SetFocusToVersionInfoLink(ContentElement? element)
        {
            element?.Focus();
        }

        [RelayCommand]
        private void OpenProjectWebsite()
        {
            _viewModelService.OpenProjectWebsite();
        }

        partial void OnRemoteComputerChanged(string value)
        {
            RefreshRdpClientHostPresentation();
        }

        partial void OnUserNameChanged(string value)
        {
            RefreshRdpClientHostPresentation();
        }

        partial void OnConnectionTitleChanged(string value)
        {
            RefreshRdpClientHostPresentation();
        }

        partial void OnWindowDisplayModeChanged(MainWindowDisplayMode value)
        {
            RefreshRdpClientHostPresentation();
        }

        private void SessionOptions_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RdpSessionOptions.UseConnectionBar))
            {
                OnPropertyChanged(nameof(IsConnectionBarPinOptionEnabled));
            }

            RefreshRdpClientHostPresentation();
            SaveSessionOptions();
        }

        private void SwitchToRdpClientView()
        {
            IsElementEnabled = false;
        }

        private void SwitchToSessionSetupView()
        {
            IsElementEnabled = true;
            RdpClientHostVisibility = Visibility.Hidden;
        }

        private void StartConnect()
        {
            if (RdpClientHost == null) throw new InvalidOperationException("The RDP client host is not instantiated.");

            RdpClientHost.RemoteComputer = RemoteComputer.Trim();
            RdpClientHost.RemotePort = int.Parse(RemotePort.Trim());
            RdpClientHost.UserName = UserName.Trim();
            RdpClientHost.DesktopWidth = (int)RdpClientHostWidth;
            RdpClientHost.DesktopHeight = (int)RdpClientHostHeight;
            RefreshRdpClientHostPresentation();
            RdpClientHost.Connect();
        }

        private bool CanConnectToRemoteComputer()
        {
            return _viewModelService.IsValidRemoteComputer(RemoteComputer)
                && _viewModelService.IsValidRemotePort(RemotePort)
                && _viewModelService.IsValidUserName(UserName);
        }

        private void RefreshRdpClientHostPresentation()
        {
            if (RdpClientHost == null)
            {
                return;
            }

            RdpClientHost.FullScreenTitle = WindowTitle;
            RdpClientHost.ConnectionBarText = WindowTitle;
            RdpClientHost.OverlayBarTitle = WindowTitle;
            RdpClientHost.UseConnectionBar = SessionOptions.UseConnectionBar;
            RdpClientHost.PinConnectionBar = SessionOptions.PinConnectionBar;
            RdpClientHost.EnableWindowsKey = SessionOptions.EnableWindowsKey;
            RdpClientHost.KeyboardHookMode = SessionOptions.KeyboardHookMode;
            RdpClientHost.UseMultiMon = SessionOptions.UseMultiMon;
            RdpClientHost.SmartSizing = SessionOptions.UseSmartSizing;
            RdpClientHost.GrabFocusOnConnect = SessionOptions.GrabFocusOnConnect;
            RdpClientHost.IsOverlayBarEnabled = RdpClientHostVisibility == Visibility.Visible && WindowDisplayMode != MainWindowDisplayMode.Normal;
        }

        private RdpSessionOptions LoadSessionOptions()
        {
            return new RdpSessionOptions
            {
                StartInFullScreen = _appSettingsService.GetSettingValue("rdp.display.startInFullScreen", false),
                UseConnectionBar = _appSettingsService.GetSettingValue("rdp.connectionBar.enabled", true),
                PinConnectionBar = _appSettingsService.GetSettingValue("rdp.connectionBar.pinned", true),
                EnableWindowsKey = _appSettingsService.GetSettingValue("rdp.keyboard.enableWindowsKey", true),
                KeyboardHookMode = (RdpKeyboardHookMode)_appSettingsService.GetSettingValue("rdp.keyboard.hookMode", (int)RdpKeyboardHookMode.OnServerInFullScreen),
                UseMultiMon = _appSettingsService.GetSettingValue("rdp.display.useMultiMon", false),
                UseSmartSizing = _appSettingsService.GetSettingValue("rdp.display.smartSizing", true),
                GrabFocusOnConnect = _appSettingsService.GetSettingValue("rdp.display.grabFocusOnConnect", true),
            };
        }

        private void SaveSessionOptions()
        {
            _appSettingsService.SetSettingValue("rdp.display.startInFullScreen", SessionOptions.StartInFullScreen);
            _appSettingsService.SetSettingValue("rdp.connectionBar.enabled", SessionOptions.UseConnectionBar);
            _appSettingsService.SetSettingValue("rdp.connectionBar.pinned", SessionOptions.PinConnectionBar);
            _appSettingsService.SetSettingValue("rdp.keyboard.enableWindowsKey", SessionOptions.EnableWindowsKey);
            _appSettingsService.SetSettingValue("rdp.keyboard.hookMode", (int)SessionOptions.KeyboardHookMode);
            _appSettingsService.SetSettingValue("rdp.display.useMultiMon", SessionOptions.UseMultiMon);
            _appSettingsService.SetSettingValue("rdp.display.smartSizing", SessionOptions.UseSmartSizing);
            _appSettingsService.SetSettingValue("rdp.display.grabFocusOnConnect", SessionOptions.GrabFocusOnConnect);
        }

        private void RdpClientHost_OnConnecting(object? sender, EventArgs e)
        {
            if (sender is RdpClientHost rdpClientHost)
            {
                RdpClientConnectionState = rdpClientHost.ConnectionState;
            }
        }

        private void RdpClientHost_OnConnected(object? sender, EventArgs e)
        {
            if (sender is RdpClientHost rdpClientHost)
            {
                RdpClientConnectionState = rdpClientHost.ConnectionState;
                RdpClientHostVisibility = Visibility.Visible;
            }
        }

        private void RdpClientHost_OnDisconnected(object sender, IMsTscAxEvents_OnDisconnectedEvent e)
        {
            if (sender is RdpClientHost rdpClientHost)
            {
                RdpClientLastDisconnectReason = rdpClientHost.LastDisconnectReason;
                RdpClientConnectionState = rdpClientHost.ConnectionState;
            }

            SwitchToSessionSetupView();
            GC.Collect();
        }
    }
}