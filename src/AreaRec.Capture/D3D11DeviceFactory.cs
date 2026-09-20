using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace AreaRec.Capture;

internal sealed class D3D11DeviceHandle : IDisposable
{
    private IntPtr _nativeDevice;
    private IntPtr _nativeContext;

    public D3D11DeviceHandle(IDirect3DDevice device, IntPtr nativeDevice, IntPtr nativeContext)
    {
        Device = device;
        _nativeDevice = nativeDevice;
        _nativeContext = nativeContext;
    }

    public IDirect3DDevice Device { get; }

    public nint NativeDevice => _nativeDevice;

    public nint NativeContext => _nativeContext;

    public void Dispose()
    {
        Device.Dispose();
        Release(ref _nativeContext);
        Release(ref _nativeDevice);
    }

    private static void Release(ref IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
        {
            return;
        }

        Marshal.Release(pointer);
        pointer = IntPtr.Zero;
    }
}

internal static class D3D11DeviceFactory
{
    private const uint D3D11SdkVersion = 7;
    private const uint D3D11CreateDeviceBgSupport = 0x20;
    private const int D3DDriverTypeHardware = 1;
    private const int D3DDriverTypeWarp = 5;
    private static readonly Guid DxgiDeviceIid =
        new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    public static D3D11DeviceHandle Create(nint adapter = 0)
    {
        var flags = D3D11CreateDeviceBgSupport;
        var driverType = adapter == 0 ? D3DDriverTypeHardware : 0;
        uint[]? featureLevels = null;
        var hr = D3D11CreateDevice(
            adapter,
            driverType,
            IntPtr.Zero,
            flags,
            featureLevels,
            0,
            D3D11SdkVersion,
            out var nativeDevice,
            out _,
            out var nativeContext);

        if (hr < 0 && adapter == 0)
        {
            Release(ref nativeDevice);
            Release(ref nativeContext);
            hr = D3D11CreateDevice(
                IntPtr.Zero,
                D3DDriverTypeWarp,
                IntPtr.Zero,
                flags,
                featureLevels,
                0,
                D3D11SdkVersion,
                out nativeDevice,
                out _,
                out nativeContext);
        }

        Marshal.ThrowExceptionForHR(hr);

        var dxgiIid = DxgiDeviceIid;
        var queryHr = Marshal.QueryInterface(nativeDevice, ref dxgiIid, out var dxgiDevice);
        if (queryHr < 0)
        {
            Release(ref nativeContext);
            Release(ref nativeDevice);
            Marshal.ThrowExceptionForHR(queryHr);
        }

        try
        {
            var deviceHr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable);
            Marshal.ThrowExceptionForHR(deviceHr);
            try
            {
                var device = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                var handle = new D3D11DeviceHandle(device, nativeDevice, nativeContext);
                // Ownership moves to D3D11DeviceHandle only after construction
                // succeeds. The finally block below therefore cleans up every
                // failure path without releasing resources owned by the handle.
                nativeDevice = IntPtr.Zero;
                nativeContext = IntPtr.Zero;
                return handle;
            }
            finally
            {
                Release(ref inspectable);
            }
        }
        finally
        {
            Release(ref dxgiDevice);
            Release(ref nativeContext);
            Release(ref nativeDevice);
        }
    }

    private static void Release(ref IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
        {
            return;
        }

        Marshal.Release(pointer);
        pointer = IntPtr.Zero;
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        [In] uint[]? featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out uint featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);
}
