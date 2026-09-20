using System.Runtime.InteropServices;

namespace AreaRec.Media;

public sealed record MediaFoundationCapabilities(
    bool Available,
    bool HardwareEncodingProbeCompleted,
    bool HardwareEncodingAvailable,
    string? HardwareEncoderName,
    string? Error)
{
    public static MediaFoundationCapabilities Probe()
    {
        try
        {
            using var runtime = new MediaFoundationRuntime();
            var hardware = MediaFoundationHardwareEncoderProbe.Probe();
            return new MediaFoundationCapabilities(
                true,
                hardware.Completed,
                hardware.Available,
                hardware.FriendlyName,
                hardware.Error);
        }
        catch (Exception exception) when (exception is DllNotFoundException or COMException or ArgumentException)
        {
            return new MediaFoundationCapabilities(false, false, false, null, exception.Message);
        }
    }
}
