using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using AlterApp.Models;
using AlterApp.ViewModels.Interfaces;
using AlterApp.ViewModels;
using MsRdcAx;
using MsRdcAx.AxMsTscLib;
using WF = System.Windows.Forms;

namespace AlterApp.Views
{
    public partial class MainWindow : Window
    {
        private bool _isFullScreen;
        private bool _suppressWindowStateHandling;
        private bool _skipNextSessionMaximizeTransition;
        private WindowState _windowStateBeforeFullScreen = WindowState.Normal;
        private WindowStyle _windowStyleBeforeFullScreen = WindowStyle.SingleBorderWindow;
        private ResizeMode _resizeModeBeforeFullScreen = ResizeMode.CanResize;
        private bool _topmostBeforeFullScreen;
        private Rect _boundsBeforeFullScreen;
        private RdpClientHost? _rdpClientHost;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = App.Current.Services.GetService<MainWindowViewModel>();
            AttachRdpClientHostHandlers();
            SyncWindowDisplayMode();
        }

        private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

        private bool IsRdpSessionVisible => ViewModel?.RdpClientHostVisibility == Visibility.Visible;

        private void AttachRdpClientHostHandlers()
        {
            if (ViewModel?.RdpClientHost == null)
            {
                return;
            }

            _rdpClientHost = ViewModel.RdpClientHost;
            _rdpClientHost.OnRequestGoFullScreen += (_, _) => EnterFullScreen();
            _rdpClientHost.OnRequestLeaveFullScreen += (_, _) => ExitFullScreen();
            _rdpClientHost.OnConnected += (_, _) =>
            {
                if (ViewModel?.SessionOptions.StartInFullScreen == true && !_isFullScreen)
                {
                    EnterFullScreen();
                }
            };
            _rdpClientHost.OnDisconnected += (_, _) =>
            {
                if (_isFullScreen)
                {
                    ExitFullScreen();
                }
            };
            _rdpClientHost.OverlayBarExitFullScreenRequested += (_, _) => HandleOverlayExitRequest();
            _rdpClientHost.OverlayBarCloseRequested += (_, _) => HandleOverlayCloseRequest();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (ViewModel == null)
            {
                return;
            }

            Rect bounds = _isFullScreen
                ? _boundsBeforeFullScreen
                : WindowState == WindowState.Normal
                    ? new Rect(Left, Top, Width, Height)
                    : RestoreBounds;

            ViewModel.UpdateWindowBoundsForPersistence(bounds.Width, bounds.Height);
            e.Cancel = ViewModel.OnClosing();
        }

        private void Window_ContentRendered(object sender, EventArgs e)
        {
            if (DataContext is IWindowContentRendered dataContext)
            {
                dataContext.OnContentRendered();
            }
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (_suppressWindowStateHandling)
            {
                return;
            }

            if (!_isFullScreen && IsRdpSessionVisible && WindowState == WindowState.Maximized)
            {
                if (_skipNextSessionMaximizeTransition)
                {
                    _skipNextSessionMaximizeTransition = false;
                    SyncWindowDisplayMode();
                    return;
                }

                EnterFullScreen(triggeredBySessionMaximize: true);
                return;
            }

            _skipNextSessionMaximizeTransition = false;
            if (!_isFullScreen)
            {
                SyncWindowDisplayMode();
            }
        }

        private void SyncWindowDisplayMode()
        {
            ViewModel?.SetWindowDisplayMode(
                _isFullScreen
                    ? MainWindowDisplayMode.FullScreen
                    : WindowState == WindowState.Maximized
                        ? MainWindowDisplayMode.Maximized
                        : MainWindowDisplayMode.Normal);
        }

        private void EnterFullScreen(bool triggeredBySessionMaximize = false)
        {
            if (_isFullScreen)
            {
                return;
            }

            _windowStateBeforeFullScreen = triggeredBySessionMaximize ? WindowState.Normal : WindowState;
            _windowStyleBeforeFullScreen = WindowStyle;
            _resizeModeBeforeFullScreen = ResizeMode;
            _topmostBeforeFullScreen = Topmost;
            _boundsBeforeFullScreen = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;

            Rect screenBounds = GetCurrentScreenBoundsInDip();
            _suppressWindowStateHandling = true;
            try
            {
                WindowState = WindowState.Normal;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                Topmost = true;
                Left = screenBounds.Left;
                Top = screenBounds.Top;
                Width = screenBounds.Width;
                Height = screenBounds.Height;
                _isFullScreen = true;
            }
            finally
            {
                _suppressWindowStateHandling = false;
            }

            SyncWindowDisplayMode();
        }

        private void ExitFullScreen()
        {
            if (!_isFullScreen)
            {
                return;
            }

            _isFullScreen = false;
            _suppressWindowStateHandling = true;
            try
            {
                WindowStyle = _windowStyleBeforeFullScreen;
                ResizeMode = _resizeModeBeforeFullScreen;
                Topmost = _topmostBeforeFullScreen;

                if (_windowStateBeforeFullScreen == WindowState.Maximized)
                {
                    _skipNextSessionMaximizeTransition = true;
                    WindowState = WindowState.Maximized;
                }
                else
                {
                    WindowState = WindowState.Normal;
                    Left = _boundsBeforeFullScreen.Left;
                    Top = _boundsBeforeFullScreen.Top;
                    Width = _boundsBeforeFullScreen.Width;
                    Height = _boundsBeforeFullScreen.Height;
                }
            }
            finally
            {
                _suppressWindowStateHandling = false;
            }

            SyncWindowDisplayMode();
        }

        private void HandleOverlayExitRequest()
        {
            if (_isFullScreen)
            {
                ExitFullScreen();
                return;
            }

            if (WindowState == WindowState.Maximized)
            {
                _suppressWindowStateHandling = true;
                try
                {
                    WindowState = WindowState.Normal;
                }
                finally
                {
                    _suppressWindowStateHandling = false;
                }

                SyncWindowDisplayMode();
            }
        }

        private void HandleOverlayCloseRequest()
        {
            if (_rdpClientHost == null)
            {
                return;
            }

            try
            {
                if (_rdpClientHost.ConnectionState != RdpClientConnectionState.NotConnected)
                {
                    _rdpClientHost.Disconnect();
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        private Rect GetCurrentScreenBoundsInDip()
        {
            var helper = new WindowInteropHelper(this);
            WF.Screen? screen = helper.Handle != IntPtr.Zero ? WF.Screen.FromHandle(helper.Handle) : WF.Screen.PrimaryScreen;
            if (screen == null)
            {
                return new Rect(Left, Top, Width, Height);
            }

            Rect bounds = new(screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, screen.Bounds.Height);
            Matrix transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            Point topLeft = transform.Transform(new Point(bounds.Left, bounds.Top));
            Point bottomRight = transform.Transform(new Point(bounds.Right, bounds.Bottom));
            return new Rect(topLeft, bottomRight);
        }
    }
}