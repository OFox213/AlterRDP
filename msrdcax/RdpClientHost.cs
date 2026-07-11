using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using MsRdcAx.AxMsTscLib;
using MSTSCLib;
using WF = System.Windows.Forms;

namespace MsRdcAx
{
    public sealed class RdpClientHost : WindowsFormsHost, IDisposable
    {
        private const int OverlayBarHeight = 40;
        private const int OverlayRevealZoneHeight = 6;
        private const int OverlayAutoHideDelayInMilliseconds = 2000;
        private const int OverlayFadeDurationInMilliseconds = 200;
        private const int OverlayFadeFrameIntervalInMilliseconds = 16;
        private const int ResizeUpdateDelayInMilliseconds = 300;
        private const int UnexpectedComError = -2147418113;
        private const int OverlayHorizontalMargin = 12;
        private const int OverlayPreferredWidth = 420;
        private const int OverlayMinimumWidth = 240;
        private const int OverlayButtonHeight = 28;
        private const int OverlayButtonSpacing = 8;
        private const int OverlayCloseButtonWidth = 32;
        private const int OverlayExitButtonPreferredWidth = 118;

        private static readonly Color OverlayBarBackgroundColor = Color.FromArgb(230, 32, 32, 32);
        private static readonly Color OverlayTextColor = Color.White;

        private readonly WF.Panel _rootPanel;
        private readonly WF.Panel _overlayBarPanel;
        private readonly WF.Panel _overlayRevealPanel;
        private readonly WF.Label _overlayBarLabel;
        private readonly WF.Label _overlayExitFullScreenButton;
        private readonly WF.Label _overlayCloseButton;
        private readonly DispatcherTimer _overlayHideTimer;
        private readonly DispatcherTimer _overlayFadeTimer;
        private readonly DispatcherTimer _resizeUpdateTimer;
        private AxMsRdpClient? _axMsRdpClient;
        private bool _isRdpClientInitialized;
        private string _fullScreenTitle = string.Empty;
        private string _connectionBarText = string.Empty;
        private string _overlayBarTitle = string.Empty;
        private bool _useConnectionBar = true;
        private bool _pinConnectionBar = true;
        private bool _enableWindowsKey = true;
        private bool _useMultiMon = false;
        private bool _disableConnectionBar = false;
        private bool _smartSizing = true;
        private bool _grabFocusOnConnect = true;
        private bool _isOverlayBarEnabled = false;
        private RdpKeyboardHookMode _keyboardHookMode = RdpKeyboardHookMode.OnServerInFullScreen;
        private int _overlayBarLeft;
        private bool _overlayBarPositionInitialized;
        private bool _isOverlayBarDragging;
        private int _overlayDragStartScreenX;
        private int _overlayDragStartBarLeft;
        private WF.Control? _overlayDragCaptureControl;
        private double _overlayCurrentOpacity;
        private double _overlayFadeStartOpacity;
        private double _overlayFadeTargetOpacity;
        private DateTime _overlayFadeStartedAtUtc;

        public RdpClientHost()
        {
            _rootPanel = new WF.Panel { BackColor = Color.Black, Margin = new WF.Padding(0), Padding = new WF.Padding(0) };
            _rootPanel.Resize += (_, _) => LayoutOverlayBar();

            _overlayBarLabel = new WF.Label
            {
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Padding = new WF.Padding(12, 0, 12, 0),
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Cursor = WF.Cursors.SizeWE,
                Text = "Alter",
            };
            _overlayBarLabel.MouseDown += OverlayBar_MouseDown;
            _overlayBarLabel.MouseMove += OverlayBar_MouseMove;
            _overlayBarLabel.MouseUp += OverlayBar_MouseUp;

            _overlayExitFullScreenButton = CreateOverlayButton("Exit full screen", OverlayExitFullScreenButton_Click);
            _overlayCloseButton = CreateOverlayButton("X", OverlayCloseButton_Click);

            _overlayBarPanel = new WF.Panel
            {
                BackColor = OverlayBarBackgroundColor,
                Cursor = WF.Cursors.SizeWE,
                Height = OverlayBarHeight,
                Visible = false,
            };
            _overlayBarPanel.Controls.Add(_overlayCloseButton);
            _overlayBarPanel.Controls.Add(_overlayExitFullScreenButton);
            _overlayBarPanel.Controls.Add(_overlayBarLabel);
            _overlayBarPanel.MouseDown += OverlayBar_MouseDown;
            _overlayBarPanel.MouseMove += OverlayBar_MouseMove;
            _overlayBarPanel.MouseUp += OverlayBar_MouseUp;

            _overlayRevealPanel = new WF.Panel
            {
                Height = OverlayRevealZoneHeight,
                Visible = false,
                BackColor = Color.Transparent,
                Cursor = WF.Cursors.Default,
            };
            _overlayRevealPanel.MouseEnter += OverlayRevealPanel_MouseEnter;
            _overlayRevealPanel.MouseMove += OverlayRevealPanel_MouseMove;

            _overlayHideTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(OverlayAutoHideDelayInMilliseconds),
            };
            _overlayHideTimer.Tick += (_, _) => HideOverlayBar();

            _overlayFadeTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(OverlayFadeFrameIntervalInMilliseconds),
            };
            _overlayFadeTimer.Tick += OverlayFadeTimer_Tick;

            _resizeUpdateTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(ResizeUpdateDelayInMilliseconds),
            };
            _resizeUpdateTimer.Tick += (_, _) =>
            {
                _resizeUpdateTimer.Stop();
                if (IsLoginCompleted)
                {
                    UpdateSessionDisplaySettingsWithRetry();
                }
            };

            _rootPanel.Controls.Add(_overlayRevealPanel);
            _rootPanel.Controls.Add(_overlayBarPanel);
            ApplyOverlayBarOpacity(0.0);
            LayoutOverlayBar();

            BeginInit();
            Child = _rootPanel;
            EndInit();

            Loaded += (_, _) =>
            {
                EnsureRdpClientActiveXControlInitialized();
                LayoutOverlayBar();
            };
            UpdateOverlayBarTitle();
        }

        public string RemoteComputer { get; set; } = string.Empty;

        public int RemotePort { get; set; }

        public string UserName { get; set; } = string.Empty;

        public int DesktopWidth { get; set; }

        public int DesktopHeight { get; set; }

        public bool IsLoginCompleted { get; private set; }

        public string FullScreenTitle
        {
            get => _fullScreenTitle;
            set
            {
                _fullScreenTitle = value;
                ApplyDynamicClientSettings();
            }
        }

        public string ConnectionBarText
        {
            get => _connectionBarText;
            set
            {
                _connectionBarText = value;
                ApplyDynamicClientSettings();
            }
        }

        public string OverlayBarTitle
        {
            get => _overlayBarTitle;
            set
            {
                _overlayBarTitle = value;
                UpdateOverlayBarTitle();
            }
        }

        public bool UseConnectionBar { get => _useConnectionBar; set => _useConnectionBar = value; }

        public bool PinConnectionBar { get => _pinConnectionBar; set => _pinConnectionBar = value; }

        public bool EnableWindowsKey { get => _enableWindowsKey; set => _enableWindowsKey = value; }

        public bool UseMultiMon { get => _useMultiMon; set => _useMultiMon = value; }

        public bool DisableConnectionBar { get => _disableConnectionBar; set => _disableConnectionBar = value; }

        public bool SmartSizing
        {
            get => _smartSizing;
            set
            {
                _smartSizing = value;
                ApplyDynamicClientSettings();
            }
        }

        public bool GrabFocusOnConnect { get => _grabFocusOnConnect; set => _grabFocusOnConnect = value; }

        public bool IsOverlayBarEnabled
        {
            get => _isOverlayBarEnabled;
            set
            {
                _isOverlayBarEnabled = value;
                UpdateOverlayBarState();
            }
        }

        public RdpKeyboardHookMode KeyboardHookMode { get => _keyboardHookMode; set => _keyboardHookMode = value; }

        public RdpClientConnectionState ConnectionState => _axMsRdpClient == null
            ? RdpClientConnectionState.NotConnected
            : ConvertEnumRawValue.ToEnumMember<RdpClientConnectionState>(_axMsRdpClient.Connected);

        public RdpClientDisconnectReason LastDisconnectReason { get; private set; } = new();

        public event EventHandler? OnConnecting;
        public event EventHandler? OnConnected;
        public event EventHandler? OnRequestGoFullScreen;
        public event EventHandler? OnRequestLeaveFullScreen;
        public event MsRdcAx.AxMsTscLib.IMsTscAxEvents_OnDisconnectedEventHandler? OnDisconnected;
        public event EventHandler? OverlayBarCloseRequested;
        public event EventHandler? OverlayBarExitFullScreenRequested;

        public new void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _overlayHideTimer.Stop();
                _overlayFadeTimer.Stop();
                _resizeUpdateTimer.Stop();
                if (_axMsRdpClient != null)
                {
                    UnregisterRdpClientAxEventHandlers();
                    _axMsRdpClient.Dispose();
                    _axMsRdpClient = null;
                }
                _rootPanel.Dispose();
            }

            base.Dispose(disposing);
        }

        public void Connect()
        {
            EnsureRdpClientActiveXControlInitialized();
            if (_axMsRdpClient == null) throw new InvalidOperationException("The RDP client ActiveX control is not instantiated.");

            double desktopScaleFactor = _axMsRdpClient.GetDesktopScaleFactor();
            _axMsRdpClient.DesktopWidth = (int)Math.Ceiling(DesktopWidth * desktopScaleFactor);
            _axMsRdpClient.DesktopHeight = (int)Math.Ceiling(DesktopHeight * desktopScaleFactor);
            _axMsRdpClient.SetRdpExtendedSetting("DesktopScaleFactor", (uint)(desktopScaleFactor * 100));
            _axMsRdpClient.SetRdpExtendedSetting("DeviceScaleFactor", (uint)100);
            _axMsRdpClient.Server = RemoteComputer;
            _axMsRdpClient.AdvancedSettings2.RDPPort = RemotePort;
            _axMsRdpClient.UserName = UserName;
            _axMsRdpClient.AdvancedSettings9.EnableCredSspSupport = true;

            var client = _axMsRdpClient.GetOcxAs<IMsRdpClient9>();
            var advanced = client.AdvancedSettings8;
            var secured = client.SecuredSettings2;
            var nonScriptable = _axMsRdpClient.GetOcxAs<IMsRdpClientNonScriptable5>();

            client.FullScreenTitle = FullScreenTitle;
            advanced.ContainerHandledFullScreen = 1;
            advanced.GrabFocusOnConnect = GrabFocusOnConnect;
            advanced.EnableWindowsKey = EnableWindowsKey ? 1 : 0;
            advanced.DisplayConnectionBar = UseConnectionBar && !DisableConnectionBar;
            advanced.PinConnectionBar = PinConnectionBar;
            advanced.SmartSizing = SmartSizing;
            secured.KeyboardHookMode = (int)KeyboardHookMode;
            nonScriptable.DisableConnectionBar = DisableConnectionBar;
            nonScriptable.ConnectionBarText = ConnectionBarText;
            nonScriptable.UseMultimon = UseMultiMon;

            UpdateOverlayBarTitle();
            _axMsRdpClient.Connect();
        }

        public void Disconnect()
        {
            if (_axMsRdpClient == null) throw new InvalidOperationException("The RDP client ActiveX control is not instantiated.");
            _axMsRdpClient.Disconnect();
        }

        public void SendKeys(bool[] keyUpStates, int[] keyData)
        {
            if (_axMsRdpClient == null) throw new InvalidOperationException("The RDP client ActiveX control is not instantiated.");
            if (keyUpStates.Length != keyData.Length) throw new ArgumentException("The key arrays must be the same length.");
            if (keyData.Length == 0) return;
            if (keyData.Length > 20) throw new ArgumentOutOfRangeException(nameof(keyData), "The maximum number of keys in one operation is 20.");
            _axMsRdpClient.GetOcxAs<IMsRdpClientNonScriptable5>().SendKeys(keyData.Length, ref keyUpStates[0], ref keyData[0]);
        }

        private void EnsureRdpClientActiveXControlInitialized()
        {
            if (_isRdpClientInitialized)
            {
                return;
            }

            _axMsRdpClient = new AxMsRdpClient();
            _axMsRdpClient.BeginInit();
            _axMsRdpClient.EndInit();
            _axMsRdpClient.Dock = WF.DockStyle.Fill;
            _axMsRdpClient.Margin = new WF.Padding(0);

            _rootPanel.Controls.Add(_axMsRdpClient);
            _axMsRdpClient.SendToBack();
            LayoutOverlayBar();
            RegisterRdpClientAxEventHandlers();
            _axMsRdpClient.CreateControl();
            _isRdpClientInitialized = true;
        }

        private void RegisterRdpClientAxEventHandlers()
        {
            if (_axMsRdpClient == null) throw new InvalidOperationException("The RDP client ActiveX control is not instantiated.");
            _axMsRdpClient.OnConnecting += (_, e) => OnConnecting?.Invoke(this, e);
            _axMsRdpClient.OnConnected += AxRdpClient_OnConnected;
            _axMsRdpClient.OnLoginComplete += (_, e) =>
            {
                IsLoginCompleted = true;
                UpdateSessionDisplaySettingsWithRetry();
            };
            _axMsRdpClient.OnDisconnected += AxRdpClient_OnDisconnected;
            _axMsRdpClient.Resize += (_, _) =>
            {
                if (IsLoginCompleted)
                {
                    _resizeUpdateTimer.Stop();
                    _resizeUpdateTimer.Start();
                }
            };
            _axMsRdpClient.OnRequestGoFullScreen += (_, e) => OnRequestGoFullScreen?.Invoke(this, e);
            _axMsRdpClient.OnRequestLeaveFullScreen += (_, e) => OnRequestLeaveFullScreen?.Invoke(this, e);
        }

        private void UnregisterRdpClientAxEventHandlers()
        {
            if (_axMsRdpClient == null)
            {
                return;
            }

            _axMsRdpClient.OnConnected -= AxRdpClient_OnConnected;
            _axMsRdpClient.OnDisconnected -= AxRdpClient_OnDisconnected;
        }

        private void AxRdpClient_OnConnected(object? sender, EventArgs e)
        {
            ApplyDynamicClientSettings();
            OnConnected?.Invoke(this, e);
        }

        private void AxRdpClient_OnDisconnected(object? sender, MsRdcAx.AxMsTscLib.IMsTscAxEvents_OnDisconnectedEvent e)
        {
            if (_axMsRdpClient == null) throw new InvalidOperationException("The RDP client ActiveX control is not instantiated.");

            IsLoginCompleted = false;
            _resizeUpdateTimer.Stop();
            HideOverlayBar();
            string description = _axMsRdpClient.GetErrorDescription((uint)e.discReason, (uint)_axMsRdpClient.ExtendedDisconnectReason);
            LastDisconnectReason = new RdpClientDisconnectReason(e.discReason, _axMsRdpClient.ExtendedDisconnectReason, description);
            OnDisconnected?.Invoke(this, e);
        }

        private void ApplyDynamicClientSettings()
        {
            if (_axMsRdpClient == null)
            {
                return;
            }

            try
            {
                var client = _axMsRdpClient.GetOcxAs<IMsRdpClient9>();
                client.FullScreenTitle = FullScreenTitle;
                client.AdvancedSettings8.SmartSizing = SmartSizing;
                _axMsRdpClient.GetOcxAs<IMsRdpClientNonScriptable5>().ConnectionBarText = ConnectionBarText;
            }
            catch (InvalidOperationException)
            {
            }
            catch (COMException)
            {
            }

            UpdateOverlayBarTitle();
        }

        private async void UpdateSessionDisplaySettingsWithRetry()
        {
            if (_axMsRdpClient == null)
            {
                return;
            }

            for (int retry = 0; retry <= 2; retry++)
            {
                try
                {
                    UpdateSessionDisplaySettings();
                    return;
                }
                catch (COMException ex) when (ex.HResult == UnexpectedComError && retry < 2)
                {
                    Debug.WriteLine("Retrying UpdateSessionDisplaySettings after COM error: {0}", ex.HResult);
                    await Task.Delay(2000);
                }
            }
        }

        private void UpdateSessionDisplaySettings()
        {
            if (_axMsRdpClient == null) throw new InvalidOperationException("The RDP client ActiveX control is not instantiated.");

            int widthDelta = Math.Abs(_axMsRdpClient.Width - _axMsRdpClient.DesktopWidth);
            int heightDelta = Math.Abs(_axMsRdpClient.Height - _axMsRdpClient.DesktopHeight);
            if (widthDelta <= 1 && heightDelta <= 1)
            {
                return;
            }

            uint desktopWidth = (uint)_axMsRdpClient.Width;
            uint desktopHeight = (uint)_axMsRdpClient.Height;
            uint desktopScaleFactor = (uint)(_axMsRdpClient.GetDesktopScaleFactor() * 100.0);
            uint physicalWidth = (uint)(desktopWidth / (double)desktopScaleFactor * 25.4);
            uint physicalHeight = (uint)(desktopHeight / (double)desktopScaleFactor * 25.4);
            _axMsRdpClient.UpdateSessionDisplaySettings(desktopWidth, desktopHeight, physicalWidth, physicalHeight, 0, desktopScaleFactor, 100);
        }

        private void OverlayRevealPanel_MouseEnter(object? sender, EventArgs e)
        {
            if (IsOverlayBarEnabled)
            {
                ShowOverlayBar();
            }
        }

        private void OverlayRevealPanel_MouseMove(object? sender, WF.MouseEventArgs e)
        {
            if (IsOverlayBarEnabled)
            {
                ShowOverlayBar();
            }
        }

        private void OverlayBar_MouseMove(object? sender, WF.MouseEventArgs e)
        {
            if (!IsOverlayBarEnabled)
            {
                return;
            }

            if (_isOverlayBarDragging)
            {
                if ((WF.Control.MouseButtons & WF.MouseButtons.Left) == 0)
                {
                    EndOverlayBarDrag();
                    return;
                }

                int deltaX = WF.Cursor.Position.X - _overlayDragStartScreenX;
                _overlayBarLeft = ClampOverlayBarLeft(_overlayDragStartBarLeft + deltaX, _overlayBarPanel.Width);
                LayoutOverlayBar();
            }

            RestartOverlayHideTimer();
        }

        private void OverlayBar_MouseDown(object? sender, WF.MouseEventArgs e)
        {
            if (!IsOverlayBarEnabled || e.Button != WF.MouseButtons.Left)
            {
                return;
            }

            RestartOverlayHideTimer();
            _isOverlayBarDragging = true;
            _overlayDragStartScreenX = WF.Cursor.Position.X;
            _overlayDragStartBarLeft = _overlayBarLeft;
            _overlayDragCaptureControl = sender as WF.Control;
            if (_overlayDragCaptureControl != null)
            {
                _overlayDragCaptureControl.Capture = true;
            }
        }

        private void OverlayBar_MouseUp(object? sender, WF.MouseEventArgs e)
        {
            if (e.Button == WF.MouseButtons.Left)
            {
                EndOverlayBarDrag();
            }
        }

        private void OverlayExitFullScreenButton_Click(object? sender, EventArgs e)
        {
            RestartOverlayHideTimer();
            OverlayBarExitFullScreenRequested?.Invoke(this, EventArgs.Empty);
        }

        private void OverlayCloseButton_Click(object? sender, EventArgs e)
        {
            RestartOverlayHideTimer();
            OverlayBarCloseRequested?.Invoke(this, EventArgs.Empty);
        }

        private void LayoutOverlayBar()
        {
            int overlayWidth = GetOverlayBarWidth();
            if (overlayWidth <= 0)
            {
                _overlayFadeTimer.Stop();
                ApplyOverlayBarOpacity(0.0);
                _overlayBarPanel.Visible = false;
                _overlayRevealPanel.Visible = false;
                return;
            }

            EnsureOverlayBarPosition(overlayWidth);

            _overlayRevealPanel.Left = _overlayBarLeft;
            _overlayRevealPanel.Top = 0;
            _overlayRevealPanel.Width = overlayWidth;
            _overlayRevealPanel.Height = OverlayRevealZoneHeight;

            _overlayBarPanel.Left = _overlayBarLeft;
            _overlayBarPanel.Top = 0;
            _overlayBarPanel.Width = overlayWidth;
            _overlayBarPanel.Height = OverlayBarHeight;
            LayoutOverlayBarContents(overlayWidth);

            if (_overlayBarPanel.Visible)
            {
                _overlayBarPanel.BringToFront();
            }
            else if (_overlayRevealPanel.Visible)
            {
                _overlayRevealPanel.BringToFront();
            }
        }

        private void UpdateOverlayBarTitle()
        {
            _overlayBarLabel.Text = string.IsNullOrWhiteSpace(OverlayBarTitle) ? "Alter" : OverlayBarTitle;
        }

        private void UpdateOverlayBarState()
        {
            if (IsOverlayBarEnabled)
            {
                ShowOverlayBar();
            }
            else
            {
                HideOverlayBar();
            }
        }

        private void ShowOverlayBar()
        {
            if (!IsOverlayBarEnabled)
            {
                return;
            }

            LayoutOverlayBar();
            if (_overlayBarPanel.Width <= 0)
            {
                return;
            }

            StartOverlayFade(1.0);
            RestartOverlayHideTimer();
        }

        private void HideOverlayBar()
        {
            EndOverlayBarDrag();
            _overlayHideTimer.Stop();
            StartOverlayFade(0.0);
        }

        private void RestartOverlayHideTimer()
        {
            _overlayHideTimer.Stop();
            _overlayHideTimer.Start();
        }

        private void OverlayFadeTimer_Tick(object? sender, EventArgs e)
        {
            double progress = (DateTime.UtcNow - _overlayFadeStartedAtUtc).TotalMilliseconds / OverlayFadeDurationInMilliseconds;
            if (progress >= 1.0)
            {
                _overlayFadeTimer.Stop();
                ApplyOverlayBarOpacity(_overlayFadeTargetOpacity);
                CompleteOverlayFade();
                return;
            }

            double opacity = _overlayFadeStartOpacity + ((_overlayFadeTargetOpacity - _overlayFadeStartOpacity) * progress);
            ApplyOverlayBarOpacity(opacity);
        }

        private void StartOverlayFade(double targetOpacity)
        {
            targetOpacity = ClampOpacity(targetOpacity);
            _overlayFadeTimer.Stop();

            if (targetOpacity > 0.0)
            {
                LayoutOverlayBar();
                if (_overlayBarPanel.Width <= 0)
                {
                    return;
                }

                _overlayRevealPanel.Visible = false;
                _overlayBarPanel.Visible = true;
                _overlayBarPanel.BringToFront();
            }

            if (Math.Abs(_overlayCurrentOpacity - targetOpacity) < 0.001)
            {
                ApplyOverlayBarOpacity(targetOpacity);
                _overlayFadeTargetOpacity = targetOpacity;
                CompleteOverlayFade();
                return;
            }

            _overlayFadeStartOpacity = _overlayCurrentOpacity;
            _overlayFadeTargetOpacity = targetOpacity;
            _overlayFadeStartedAtUtc = DateTime.UtcNow;

            if (!_overlayBarPanel.Visible)
            {
                _overlayBarPanel.Visible = true;
                _overlayBarPanel.BringToFront();
            }

            _overlayFadeTimer.Start();
        }

        private void CompleteOverlayFade()
        {
            if (_overlayFadeTargetOpacity <= 0.0)
            {
                _overlayBarPanel.Visible = false;
                _overlayRevealPanel.Visible = IsOverlayBarEnabled;
                if (_overlayRevealPanel.Visible)
                {
                    _overlayRevealPanel.BringToFront();
                }
            }
            else
            {
                _overlayRevealPanel.Visible = false;
                _overlayBarPanel.Visible = true;
                _overlayBarPanel.BringToFront();
            }
        }

        private void ApplyOverlayBarOpacity(double opacity)
        {
            _overlayCurrentOpacity = ClampOpacity(opacity);
            _overlayBarPanel.BackColor = ApplyOpacity(OverlayBarBackgroundColor, _overlayCurrentOpacity);
            _overlayBarLabel.ForeColor = ApplyOpacity(OverlayTextColor, _overlayCurrentOpacity);
            _overlayExitFullScreenButton.ForeColor = ApplyOpacity(OverlayTextColor, _overlayCurrentOpacity);
            _overlayCloseButton.ForeColor = ApplyOpacity(OverlayTextColor, _overlayCurrentOpacity);
            _overlayBarPanel.Invalidate();
            _overlayBarLabel.Invalidate();
            _overlayExitFullScreenButton.Invalidate();
            _overlayCloseButton.Invalidate();
        }

        private WF.Label CreateOverlayButton(string text, EventHandler onClick)
        {
            var button = new WF.Label
            {
                Text = text,
                Height = OverlayButtonHeight,
                AutoEllipsis = true,
                BackColor = Color.Transparent,
                ForeColor = OverlayTextColor,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = WF.Cursors.Hand,
            };
            button.Click += onClick;
            button.MouseEnter += OverlayActionButton_MouseEnter;
            button.MouseMove += OverlayActionButton_MouseMove;
            return button;
        }

        private void OverlayActionButton_MouseEnter(object? sender, EventArgs e)
        {
            if (IsOverlayBarEnabled)
            {
                RestartOverlayHideTimer();
            }
        }

        private void OverlayActionButton_MouseMove(object? sender, WF.MouseEventArgs e)
        {
            if (IsOverlayBarEnabled)
            {
                RestartOverlayHideTimer();
            }
        }

        private void LayoutOverlayBarContents(int overlayWidth)
        {
            const int panelPadding = 6;

            int contentTop = (OverlayBarHeight - OverlayButtonHeight) / 2;
            int closeButtonLeft = overlayWidth - panelPadding - OverlayCloseButtonWidth;
            int exitButtonWidth = Math.Min(OverlayExitButtonPreferredWidth, Math.Max(88, overlayWidth / 3));
            int exitButtonLeft = closeButtonLeft - OverlayButtonSpacing - exitButtonWidth;

            _overlayCloseButton.SetBounds(closeButtonLeft, contentTop, OverlayCloseButtonWidth, OverlayButtonHeight);
            _overlayExitFullScreenButton.SetBounds(exitButtonLeft, contentTop, exitButtonWidth, OverlayButtonHeight);

            int labelLeft = panelPadding + 8;
            int labelRight = Math.Max(labelLeft + 32, exitButtonLeft - OverlayButtonSpacing);
            _overlayBarLabel.SetBounds(labelLeft, 0, Math.Max(32, labelRight - labelLeft), OverlayBarHeight);
        }

        private int GetOverlayBarWidth()
        {
            int availableWidth = Math.Max(0, _rootPanel.ClientSize.Width - (OverlayHorizontalMargin * 2));
            if (availableWidth <= 0)
            {
                return 0;
            }

            if (availableWidth < OverlayMinimumWidth)
            {
                return availableWidth;
            }

            return Math.Min(OverlayPreferredWidth, availableWidth);
        }

        private void EnsureOverlayBarPosition(int overlayWidth)
        {
            if (!_overlayBarPositionInitialized)
            {
                _overlayBarLeft = GetDefaultOverlayBarLeft(overlayWidth);
                _overlayBarPositionInitialized = true;
                return;
            }

            _overlayBarLeft = ClampOverlayBarLeft(_overlayBarLeft, overlayWidth);
        }

        private int GetDefaultOverlayBarLeft(int overlayWidth)
        {
            int centeredLeft = (_rootPanel.ClientSize.Width - overlayWidth) / 2;
            return ClampOverlayBarLeft(centeredLeft, overlayWidth);
        }

        private int ClampOverlayBarLeft(int overlayBarLeft, int overlayWidth)
        {
            int minLeft = Math.Min(OverlayHorizontalMargin, Math.Max(0, _rootPanel.ClientSize.Width - overlayWidth));
            int maxLeft = Math.Max(minLeft, _rootPanel.ClientSize.Width - overlayWidth - OverlayHorizontalMargin);
            return Math.Max(minLeft, Math.Min(maxLeft, overlayBarLeft));
        }

        private static double ClampOpacity(double opacity)
        {
            return Math.Max(0.0, Math.Min(1.0, opacity));
        }

        private static Color ApplyOpacity(Color color, double opacity)
        {
            int alpha = (int)Math.Round(color.A * ClampOpacity(opacity));
            return Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        private void EndOverlayBarDrag()
        {
            if (!_isOverlayBarDragging)
            {
                return;
            }

            _isOverlayBarDragging = false;
            if (_overlayDragCaptureControl != null)
            {
                _overlayDragCaptureControl.Capture = false;
                _overlayDragCaptureControl = null;
            }
        }
    }
}