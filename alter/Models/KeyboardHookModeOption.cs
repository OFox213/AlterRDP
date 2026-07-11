using MsRdcAx;

namespace AlterApp.Models
{
    internal sealed class KeyboardHookModeOption
    {
        public KeyboardHookModeOption(RdpKeyboardHookMode value, string displayName)
        {
            Value = value;
            DisplayName = displayName;
        }

        public RdpKeyboardHookMode Value { get; }

        public string DisplayName { get; }
    }
}