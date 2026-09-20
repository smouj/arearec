using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace AreaRec.Capture;

internal static class Direct3DSurfaceInterop
{
    private static readonly Guid DxgiInterfaceAccessIid =
        new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid Texture2DIid =
        new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    public static nint GetTexturePointer(IDirect3DSurface surface)
    {
        var marshaler = MarshalInterface<IDirect3DSurface>.CreateMarshaler(surface);
        try
        {
            var abi = MarshalInterface<IDirect3DSurface>.GetAbi(marshaler);
            var accessIid = DxgiInterfaceAccessIid;
            var queryHr = Marshal.QueryInterface(abi, ref accessIid, out var access);
            Marshal.ThrowExceptionForHR(queryHr);
            try
            {
                var vtable = Marshal.ReadIntPtr(access);
                var method = Marshal.GetDelegateForFunctionPointer<GetInterfaceDelegate>(
                    Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size));
                var texture = IntPtr.Zero;
                var textureIid = Texture2DIid;
                var hr = method(access, ref textureIid, out texture);
                Marshal.ThrowExceptionForHR(hr);
                return texture;
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            MarshalInterface<IDirect3DSurface>.DisposeMarshaler(marshaler);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetInterfaceDelegate(nint self, ref Guid iid, out nint value);
}
