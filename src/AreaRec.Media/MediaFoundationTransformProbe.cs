using System.Runtime.InteropServices;

namespace AreaRec.Media;

public sealed record MediaFoundationTransformInfo(
    Guid Category,
    string? FriendlyName,
    string? HardwareUrl)
{
    public bool IsHardware => !string.IsNullOrWhiteSpace(HardwareUrl);
}

/// <summary>
/// Reads the transform selected by Media Foundation Sink Writer when Windows exposes it.
/// Failure to expose the transform is diagnostic only and never changes recording behavior.
/// </summary>
internal static class MediaFoundationTransformProbe
{
    private static readonly Guid SinkWriterExIid = new("588D72AB-5BC1-496A-8714-B70617141B25");
    private static readonly Guid FriendlyNameAttribute = new("314FFBAE-5B41-4C95-9C19-4E7D586FACE3");
    private static readonly Guid HardwareUrlAttribute = new("2FB866AC-B078-4942-AB6C-003D05CDA674");

    public static MediaFoundationTransformInfo? TryGet(object sinkWriter, uint streamIndex)
    {
        ArgumentNullException.ThrowIfNull(sinkWriter);

        var unknown = Marshal.GetIUnknownForObject(sinkWriter);
        try
        {
            var sinkWriterExIid = SinkWriterExIid;
            if (Marshal.QueryInterface(unknown, ref sinkWriterExIid, out var sinkWriterEx) < 0)
            {
                return null;
            }

            try
            {
                var getTransform = GetDelegate<GetTransformForStreamDelegate>(sinkWriterEx, 14);
                var hresult = getTransform(sinkWriterEx, streamIndex, 0, out var category, out var transform);
                if (hresult < 0 || transform == 0)
                {
                    return null;
                }

                try
                {
                    var getAttributes = GetDelegate<GetAttributesDelegate>(transform, 8);
                    if (getAttributes(transform, out var attributes) < 0 || attributes == 0)
                    {
                        return new MediaFoundationTransformInfo(category, null, null);
                    }

                    try
                    {
                        return new MediaFoundationTransformInfo(
                            category,
                            ReadString(attributes, FriendlyNameAttribute),
                            ReadString(attributes, HardwareUrlAttribute));
                    }
                    finally
                    {
                        Marshal.Release(attributes);
                    }
                }
                finally
                {
                    Marshal.Release(transform);
                }
            }
            finally
            {
                Marshal.Release(sinkWriterEx);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    private static string? ReadString(nint attributes, Guid key)
    {
        var getAllocatedString = GetDelegate<GetAllocatedStringDelegate>(attributes, 13);
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

    private static T GetDelegate<T>(nint comObject, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size));
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetTransformForStreamDelegate(
        nint self,
        uint streamIndex,
        uint transformIndex,
        out Guid category,
        out nint transform);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAttributesDelegate(nint self, out nint attributes);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAllocatedStringDelegate(
        nint self,
        ref Guid key,
        out nint value,
        out uint length);
}
