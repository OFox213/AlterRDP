using CommunityToolkit.Mvvm.ComponentModel;
using MsRdcAx;

namespace AlterApp.Models
{
    internal sealed partial class RdpSessionOptions : ObservableObject
    {
        [ObservableProperty]
        private bool _startInFullScreen = false;

        [ObservableProperty]
        private bool _useConnectionBar = true;

        [ObservableProperty]
        private bool _pinConnectionBar = true;

        [ObservableProperty]
        private bool _enableWindowsKey = true;

        [ObservableProperty]
        private RdpKeyboardHookMode _keyboardHookMode = RdpKeyboardHookMode.OnServerInFullScreen;

        [ObservableProperty]
        private bool _useMultiMon = false;

        [ObservableProperty]
        private bool _useSmartSizing = true;

        [ObservableProperty]
        private bool _grabFocusOnConnect = true;
    }
}