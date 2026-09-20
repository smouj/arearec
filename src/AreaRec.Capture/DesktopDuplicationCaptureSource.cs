using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AreaRec.Core.Recording;

namespace AreaRec.Capture;

/// <summary>
/// DXGI Desktop Duplication fallback for displays where WGC is unavailable.
/// Frames remain GPU-native until the D3D11 processor reads the selected crop.
/// </summary>
public sealed class DesktopDuplicationCaptureSource : ICaptureSource, IFrameDropMetrics, INativeGraphicsContext
{
    private static readonly Guid DxgiFactory1Iid = new("770AAE78-F26F-4DBA-A829-253C83D1B387");
    private static readonly Guid DxgiOutput1Iid = new("00CDDEA8-939B-4B83-A340-A685226666CC");
    private static readonly Guid Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
    private const int DxgiErrorAccessLost = unchecked((int)0x887A0026);
    private const int DxgiErrorNotCurrentlyAvailable = unchecked((int)0x887A0022);

    private readonly object _gate = new();
    private D3D11DeviceHandle? _device;
    private nint _duplication;
    private nint _lastTexture;
    private PhysicalRegion _outputRegion;
    private TimeSpan _framePeriod;
    private TimeSpan _lastTimestamp;
    private long _sequence;
    private long _duplicatedFrames;
    private bool _started;
    private bool _disposed;

    private static bool TraceEnabled => string.Equals(
        Environment.GetEnvironmentVariable("AREAREC_DXGI_TRACE"),
        "1",
        StringComparison.Ordinal);

    public string BackendName => "dxgi-desktop-duplication";

    public long DroppedFrames => 0;

    public long DuplicatedFrames => Interlocked.Read(ref _duplicatedFrames);

    public nint NativeDevice => _device?.NativeDevice ?? 0;

    public nint NativeContext => _device?.NativeContext ?? 0;

    public ValueTask StartAsync(CaptureSettings settings, CancellationToken cancellationToken)
    {
        settings.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("The capture source has already started.");
            }

            var monitor = MonitorFromPoint(new PointNative(settings.Region.X, settings.Region.Y), MonitorDefaultToNearest);
            _framePeriod = TimeSpan.FromSeconds(1d / settings.FramesPerSecond);
            var output = FindOutputForMonitor(monitor, out var adapter, out _outputRegion);
            try
            {
                try
                {
                    _device = D3D11DeviceFactory.Create(adapter);
                    var output1 = QueryInterface(output, DxgiOutput1Iid);
                    try
                    {
                        _duplication = DuplicateOutput(output1, _device.NativeDevice);
                    }
                    finally
                    {
                        Marshal.Release(output1);
                    }
                }
                finally
                {
                    Marshal.Release(adapter);
                }

                _started = true;
            }
            catch
            {
                _device?.Dispose();
                _device = null;
                throw;
            }
            finally
            {
                Marshal.Release(output);
            }
        }

        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<CapturedFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var duplication = _duplication;
            if (duplication == 0)
            {
                yield break;
            }

            var frameInfo = default(DxgiOutduplFrameInfo);
            var timeoutMilliseconds = _lastTexture == 0
                ? 500u
                : Math.Max(1u, (uint)Math.Round(_framePeriod.TotalMilliseconds));
            var hr = AcquireNextFrame(duplication, timeoutMilliseconds, ref frameInfo, out var resource);
            if (TraceEnabled)
            {
                Console.Error.WriteLine(
                    $"DXGI AcquireNextFrame hr=0x{hr:X8} resource=0x{resource:X} accumulated={frameInfo.AccumulatedFrames} present={frameInfo.LastPresentTime.Value} mouse={frameInfo.LastMouseUpdateTime.Value}");
            }
            if (hr == DxgiErrorWaitTimeout)
            {
                if (_lastTexture != 0)
                {
                    var duplicateTexture = AddReference(_lastTexture);
                    _lastTimestamp += _framePeriod;
                    Interlocked.Increment(ref _duplicatedFrames);
                    yield return new CapturedFrame(
                        Interlocked.Increment(ref _sequence),
                        _lastTimestamp,
                        new DesktopDuplicationFrameSurface(
                            duplicateTexture,
                            _outputRegion,
                            _lastTimestamp,
                            NativeDevice,
                            NativeContext,
                            static () => { }));
                }

                // Keep timeout polling on the thread that owns the duplication
                // object. DXGI duplication is COM-backed and moving between
                // pool threads here can make an otherwise valid duplication
                // appear permanently idle on some drivers.
                await ValueTask.CompletedTask;
                continue;
            }

            if (hr == DxgiErrorAccessLost)
            {
                throw new InvalidOperationException(
                    "DXGI desktop duplication lost access to the display; the source must be recreated after a display mode change.");
            }

            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            if (resource == 0)
            {
                ReleaseFrame(duplication);
                continue;
            }

            try
            {
                var texture = QueryInterface(resource, Texture2DIid);
                var timestamp = frameInfo.LastPresentTime.ToTimeSpan();
                if (timestamp == TimeSpan.Zero)
                {
                    timestamp = TimeSpan.FromSeconds(
                        Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
                }
                _lastTimestamp = timestamp;
                ReplaceLastTexture(texture);
                var surface = new DesktopDuplicationFrameSurface(
                    texture,
                    _outputRegion,
                    timestamp,
                    NativeDevice,
                    NativeContext,
                    () => ReleaseFrame(duplication));
                resource = 0;
                var sequence = Interlocked.Increment(ref _sequence);
                yield return new CapturedFrame(sequence, timestamp, surface);
            }
            finally
            {
                if (resource != 0)
                {
                    Marshal.Release(resource);
                    ReleaseFrame(duplication);
                }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            Release(ref _duplication);
            Release(ref _lastTexture);
            _device?.Dispose();
            _device = null;
            _started = false;
        }

        return ValueTask.CompletedTask;
    }

    private static nint FindOutputForMonitor(nint monitor, out nint adapter, out PhysicalRegion outputRegion)
    {
        var factory = CreateFactory();
        try
        {
            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                var currentAdapter = EnumAdapters(factory, adapterIndex);
                if (currentAdapter == 0)
                {
                    break;
                }

                for (uint outputIndex = 0; ; outputIndex++)
                {
                    var output = EnumOutputs(currentAdapter, outputIndex);
                    if (output == 0)
                    {
                        break;
                    }

                    var desc = GetOutputDesc(output);
                    if (desc.Monitor == monitor && desc.AttachedToDesktop)
                    {
                        adapter = currentAdapter;
                        outputRegion = PhysicalRegion.FromPoints(
                            desc.DesktopCoordinates.Left,
                            desc.DesktopCoordinates.Top,
                            desc.DesktopCoordinates.Right,
                            desc.DesktopCoordinates.Bottom);
                        return output;
                    }

                    Marshal.Release(output);
                }

                Marshal.Release(currentAdapter);
            }
        }
        finally
        {
            Marshal.Release(factory);
        }

        throw new InvalidOperationException("The selected display output could not be duplicated by DXGI.");
    }

    private static nint QueryInterface(nint source, Guid iid)
    {
        var hr = Marshal.QueryInterface(source, ref iid, out var result);
        Marshal.ThrowExceptionForHR(hr);
        return result;
    }

    private static nint CreateFactory()
    {
        var iid = DxgiFactory1Iid;
        var hr = CreateDXGIFactory1(ref iid, out var factory);
        Marshal.ThrowExceptionForHR(hr);
        return factory;
    }

    private static nint EnumAdapters(nint factory, uint index)
    {
        var method = GetDelegate<EnumAdaptersDelegate>(factory, 7);
        var hr = method(factory, index, out var adapter);
        if (hr == unchecked((int)0x887A0002))
        {
            return 0;
        }

        Marshal.ThrowExceptionForHR(hr);
        return adapter;
    }

    private static nint EnumOutputs(nint adapter, uint index)
    {
        var method = GetDelegate<EnumOutputsDelegate>(adapter, 7);
        var hr = method(adapter, index, out var output);
        if (hr == unchecked((int)0x887A0002))
        {
            return 0;
        }

        Marshal.ThrowExceptionForHR(hr);
        return output;
    }

    private static DxgiOutputDesc GetOutputDesc(nint output)
    {
        var method = GetDelegate<GetOutputDescDelegate>(output, 7);
        var hr = method(output, out var desc);
        Marshal.ThrowExceptionForHR(hr);
        return desc;
    }

    private static nint DuplicateOutput(nint output1, nint nativeDevice)
    {
        var method = GetDelegate<DuplicateOutputDelegate>(output1, 22);
        var hr = method(output1, nativeDevice, out var duplication);
        if (hr == DxgiErrorNotCurrentlyAvailable)
        {
            throw new InvalidOperationException(
                "DXGI desktop duplication is temporarily unavailable because another duplicator owns the display.");
        }

        Marshal.ThrowExceptionForHR(hr);
        return duplication;
    }

    private static int AcquireNextFrame(
        nint duplication,
        uint timeoutMilliseconds,
        ref DxgiOutduplFrameInfo frameInfo,
        out nint resource)
    {
        var method = GetDelegate<AcquireNextFrameDelegate>(duplication, 8);
        return method(duplication, timeoutMilliseconds, ref frameInfo, out resource);
    }

    private static void ReleaseFrame(nint duplication)
    {
        var method = GetDelegate<ReleaseFrameDelegate>(duplication, 14);
        _ = method(duplication);
    }

    private static T GetDelegate<T>(nint comObject, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size));
    }

    private static nint AddReference(nint pointer)
    {
        Marshal.AddRef(pointer);
        return pointer;
    }

    private void ReplaceLastTexture(nint texture)
    {
        var retained = AddReference(texture);
        Release(ref _lastTexture);
        _lastTexture = retained;
    }

    private static void Release(ref nint pointer)
    {
        if (pointer == 0)
        {
            return;
        }

        Marshal.Release(pointer);
        pointer = 0;
    }

    private sealed class DesktopDuplicationFrameSurface : INativeTextureFrameSurface
    {
        private readonly Action _releaseFrame;
        private nint _texture;
        private bool _disposed;

        public DesktopDuplicationFrameSurface(
            nint texture,
            PhysicalRegion sourceRegion,
            TimeSpan timestamp,
            nint nativeDevice,
            nint nativeContext,
            Action releaseFrame)
        {
            _texture = texture;
            SourceRegion = sourceRegion;
            Timestamp = timestamp;
            NativeDevice = nativeDevice;
            NativeContext = nativeContext;
            _releaseFrame = releaseFrame;
        }

        public int Width => SourceRegion.Width;

        public int Height => SourceRegion.Height;

        public TimeSpan Timestamp { get; }

        public nint NativeTexture => _disposed ? throw new ObjectDisposedException(nameof(DesktopDuplicationFrameSurface)) : _texture;

        public nint NativeDevice { get; }

        public nint NativeContext { get; }

        public PhysicalRegion SourceRegion { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Release(ref _texture);
            _releaseFrame();
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdaptersDelegate(nint self, uint index, out nint adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumOutputsDelegate(nint self, uint index, out nint output);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetOutputDescDelegate(nint self, out DxgiOutputDesc desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DuplicateOutputDelegate(nint self, nint device, out nint duplication);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AcquireNextFrameDelegate(
        nint self,
        uint timeoutMilliseconds,
        ref DxgiOutduplFrameInfo frameInfo,
        out nint resource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseFrameDelegate(nint self);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiOutputDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        public RectNative DesktopCoordinates;

        [MarshalAs(UnmanagedType.Bool)]
        public bool AttachedToDesktop;

        public int Rotation;
        public nint Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiOutduplFrameInfo
    {
        public LargeInteger LastPresentTime;
        public LargeInteger LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public DxgiPointerPosition PointerPosition;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LargeInteger
    {
        public long Value;

        public TimeSpan ToTimeSpan() => Value == 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(Value / (double)Stopwatch.Frequency);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiPointerPosition
    {
        public PointNative Position;
        public int Visible;
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(PointNative point, uint flags);

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out nint factory);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RectNative(int Left, int Top, int Right, int Bottom);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PointNative(int X, int Y);
}
