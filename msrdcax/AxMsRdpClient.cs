using System;
using MsRdcAx.AxMsTscLib;

namespace MsRdcAx
{
    internal class AxMsRdpClient : AxMsRdpClient9NotSafeForScripting
    {
        public AxMsRdpClient() : base()
        {
        }

        public double GetDesktopScaleFactor()
        {
            const double nonScaledDpi = 96.0;  // DPI for 100%
            return this.DeviceDpi / nonScaledDpi;
        }

        public T GetOcxAs<T>() where T : class
        {
            return (GetOcx() as T) ?? throw new InvalidOperationException($@"Failed to cast the RDP ActiveX control to {typeof(T).FullName}.");
        }

        public void SetRdpExtendedSetting(string propertyName, object propertyValue)
        {
            var rdpExtendedSettings = GetOcxAs<MSTSCLib.IMsRdpExtendedSettings>();
            rdpExtendedSettings.set_Property(propertyName, ref propertyValue);
        }
    }
}
