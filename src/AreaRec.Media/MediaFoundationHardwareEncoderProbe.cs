using System.Runtime.InteropServices;

namespace AreaRec.Media;

public sealed record MediaFoundationHardwareEncoderProbeResult(
    bool Completed,
    bool Available,
    string? FriendlyName,
    string? HardwareUrl,
    string? Error);

internal static class MediaFoundationHardwareEncoderProbe
{
    private static readonly Guid VideoEncoderCategory = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    private static readonly Guid VideoMajorType = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid H264Subtype = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid Nv12Subtype = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid FriendlyNameAttribute = new("314FFBAE-5B41-4C95-9C19-4E7D586FACE3");
    private static readonly Guid HardwareUrlAttribute = new("2FB866AC-B078-4942-AB6C-003D05CDA674");

    private const uint MftEnumFlagHardware = 0x00000004;

    public static MediaFoundationHardwareEncoderProbeResult Probe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new MediaFoundationHardwareEncoderProbeResult(false, false, null, null, "Windows is required.");
        }

        var input = new MftRegisterTypeInfo(VideoMajorType, Nv12Subtype);
        var output = new MftRegisterTypeInfo(VideoMajorType, H264Subtype);
        nint activations = 0;
        try
        {
            var hresult = MFTEnumEx(
                VideoEncoderCategory,
                MftEnumFlagHardware,
                ref input,
                ref output,
                out activations,
                out var activationCount);
            if (hresult < 0)
            {
                return new MediaFoundationHardwareEncoderProbeResult(
                    true,
                    false,
                    null,
                    null,
                    hresult == HResults.MfENotFound ? null : $"MFTEnumEx failed with HRESULT 0x{hresult:X8}.");
            }

            string? firstName = null;
            string? firstHardwareUrl = null;
            for (var index = 0; index < activationCount; index++)
            {
                var activation = Marshal.ReadIntPtr(activations, checked((int)(index * (uint)IntPtr.Size)));
                if (activation == 0)
                {
                    continue;
                }

                try
                {
                    var friendlyName = ReadString(activation, FriendlyNameAttribute);
                    var hardwareUrl = ReadString(activation, HardwareUrlAttribute);
                    firstName ??= friendlyName;
                    firstHardwareUrl ??= hardwareUrl;
                }
                finally
                {
                    Marshal.Release(activation);
                }
            }

            return new MediaFoundationHardwareEncoderProbeResult(
                true,
                activationCount > 0,
                firstName,
                firstHardwareUrl,
                null);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            return new MediaFoundationHardwareEncoderProbeResult(true, false, null, null, exception.Message);
        }
        finally
        {
            if (activations != 0)
            {
                Marshal.FreeCoTaskMem(activations);
            }
        }
    }

    private static string? ReadString(nint attributes, Guid key)
    {
        var vtable = Marshal.ReadIntPtr(attributes);
        var getAllocatedString = Marshal.GetDelegateForFunctionPointer<GetAllocatedStringDelegate>(
            Marshal.ReadIntPtr(vtable, 13 * IntPtr.Size));
        var hresult = getAllocatedString(attributes, ref key, out var value, out _);
        if (hresult < 0 || value == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(value);
        }
        finally
        {
            Marshal.FreeCoTaskMem(value);
        }
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFTEnumEx(
        Guid guidCategory,
        uint flags,
        ref MftRegisterTypeInfo inputType,
        ref MftRegisterTypeInfo outputType,
        out nint activations,
        out uint activationCount);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct MftRegisterTypeInfo(Guid MajorType, Guid Subtype);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAllocatedStringDelegate(
        nint self,
        ref Guid key,
        out nint value,
        out uint length);

    private static class HResults
    {
        public const int MfENotFound = unchecked((int)0xC00D36D5);
    }
}
