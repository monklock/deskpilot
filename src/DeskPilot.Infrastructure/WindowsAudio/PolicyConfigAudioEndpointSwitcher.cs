using System.Runtime.InteropServices;
using DeskPilot.Modules.AudioControl;

namespace DeskPilot.Infrastructure.WindowsAudio;

/// <summary>Isolates the undocumented Windows PolicyConfig endpoint-switching COM API.</summary>
public sealed class PolicyConfigAudioEndpointSwitcher
{
    /// <summary>Sets the supplied endpoint as the default Windows output device for all roles.</summary>
    public AudioDeviceSwitchResult SetDefaultDevice(string endpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        try
        {
            var policyConfig = (IPolicyConfig)new PolicyConfigClient();
            try
            {
                foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
                {
                    Marshal.ThrowExceptionForHR(policyConfig.SetDefaultEndpoint(endpointId, role));
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(policyConfig);
            }

            return new AudioDeviceSwitchResult(true);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or TypeLoadException)
        {
            return new AudioDeviceSwitchResult(false, "endpoint-switch-not-supported", "Windows cannot switch the selected audio device automatically.");
        }
    }

    private enum ERole { Console, Multimedia, Communications }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        int GetMixFormat(string deviceId, IntPtr format);
        int GetDeviceFormat(string deviceId, int defaultFormat, IntPtr format);
        int ResetDeviceFormat(string deviceId);
        int SetDeviceFormat(string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        int GetProcessingPeriod(string deviceId, int defaultPeriod, IntPtr defaultPeriodValue, IntPtr minimumPeriodValue);
        int SetProcessingPeriod(string deviceId, IntPtr period);
        int GetShareMode(string deviceId, IntPtr mode);
        int SetShareMode(string deviceId, IntPtr mode);
        int GetPropertyValue(string deviceId, IntPtr key, IntPtr value);
        int SetPropertyValue(string deviceId, IntPtr key, IntPtr value);
        int SetDefaultEndpoint(string deviceId, ERole role);
        int SetEndpointVisibility(string deviceId, int visible);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient;
}
