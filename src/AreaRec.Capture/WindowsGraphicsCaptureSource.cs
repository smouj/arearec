using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using AreaRec.Core.Recording;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace AreaRec.Capture;

public sealed class WindowsGraphicsCaptureSource : ICaptureSource, IFrameDropMetrics, INativeGraphicsContext
{
    private readonly Channel<CapturedFrame> _frames = Channel.CreateBounded<CapturedFrame>(
        new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly object _lifecycleGate = new();
    private D3D11DeviceHandle? _device;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private long _sequence;
    private long _droppedFrames;
    private PhysicalRegion _monitorRegion;
    private bool _started;
    private bool _disposed;

    public string BackendName => "windows-graphics-capture";

    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    public long DuplicatedFrames => 0;

    public nint NativeDevice => _device?.NativeDevice ?? 0;

    public nint NativeContext => _device?.NativeContext ?? 0;

    public ValueTask StartAsync(CaptureSettings settings, CancellationToken cancellationToken)
    {
        settings.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("The capture source has already started.");
            }

            if (!WindowsGraphicsCaptureSupport.IsSupported())
            {
                throw new PlatformNotSupportedException("Windows Graphics Capture is not available on this Windows build.");
            }

            try
            {
                var monitor = MonitorFromPoint(new PointNative(settings.Region.X, settings.Region.Y), MonitorDefaultToNearest);
                _monitorRegion = GetMonitorRegion(monitor);
                _device = D3D11DeviceFactory.Create();
                _item = GraphicsCaptureInterop.CreateForMonitor(monitor);
                _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    _device.Device,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    4,
                    _item.Size);
                _session = _framePool.CreateCaptureSession(_item);
                _session.IsCursorCaptureEnabled = settings.IncludeCursor;
                _framePool.FrameArrived += OnFrameArrived;
                _item.Closed += OnCaptureItemClosed;
                _session.StartCapture();
                _started = true;
            }
            catch
            {
                _disposed = true;
                ReleaseResourcesNoLock();
                throw;
            }
        }

        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<CapturedFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await _frames.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_frames.Reader.TryRead(out var frame))
            {
                yield return frame;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            ReleaseResourcesNoLock();
        }

        return ValueTask.CompletedTask;
    }

    private void ReleaseResourcesNoLock()
    {
        if (_framePool is not null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
        }

        if (_item is not null)
        {
            _item.Closed -= OnCaptureItemClosed;
        }

        _session?.Dispose();
        _framePool?.Dispose();
        _device?.Dispose();
        _session = null;
        _framePool = null;
        _item = null;
        _device = null;
        _started = false;
        _frames.Writer.TryComplete();

        while (_frames.Reader.TryRead(out var frame))
        {
            frame.Surface.Dispose();
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Direct3D11CaptureFrame? frame = null;
        try
        {
            frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            var captured = new WindowsGraphicsFrameSurface(frame, _monitorRegion, NativeDevice, NativeContext);
            frame = null;
            var sequence = Interlocked.Increment(ref _sequence);
            if (!_frames.Writer.TryWrite(new CapturedFrame(sequence, captured.Timestamp, captured)))
            {
                Interlocked.Increment(ref _droppedFrames);
                captured.Dispose();
            }
        }
        finally
        {
            frame?.Dispose();
        }
    }

    private void OnCaptureItemClosed(GraphicsCaptureItem sender, object args)
    {
        _frames.Writer.TryComplete(new InvalidOperationException("The captured display was closed."));
    }

    private sealed class WindowsGraphicsFrameSurface : INativeTextureFrameSurface
    {
        private Direct3D11CaptureFrame? _frame;
        private nint _nativeTexture;
        private readonly PhysicalRegion _sourceRegion;
        private readonly nint _nativeDevice;
        private readonly nint _nativeContext;

        public WindowsGraphicsFrameSurface(
            Direct3D11CaptureFrame frame,
            PhysicalRegion sourceRegion,
            nint nativeDevice,
            nint nativeContext)
        {
            _frame = frame;
            _sourceRegion = sourceRegion;
            _nativeDevice = nativeDevice;
            _nativeContext = nativeContext;
        }

        public int Width => _frame?.ContentSize.Width ?? 0;

        public int Height => _frame?.ContentSize.Height ?? 0;

        public TimeSpan Timestamp => _frame?.SystemRelativeTime ?? TimeSpan.Zero;

        public nint NativeTexture
        {
            get
            {
                if (_nativeTexture != 0)
                {
                    return _nativeTexture;
                }

                var frame = _frame ?? throw new ObjectDisposedException(nameof(WindowsGraphicsFrameSurface));
                _nativeTexture = Direct3DSurfaceInterop.GetTexturePointer(frame.Surface);
                return _nativeTexture;
            }
        }

        public nint NativeDevice => _frame is null ? 0 : _nativeDevice;

        public nint NativeContext => _frame is null ? 0 : _nativeContext;

        public PhysicalRegion SourceRegion => _sourceRegion;

        public void Dispose()
        {
            if (_nativeTexture != 0)
            {
                Marshal.Release(_nativeTexture);
                _nativeTexture = 0;
            }

            Interlocked.Exchange(ref _frame, null)?.Dispose();
        }
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(PointNative point, uint flags);

    private static PhysicalRegion GetMonitorRegion(IntPtr monitor)
    {
        var info = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            throw new InvalidOperationException("The selected display monitor could not be resolved.");
        }

        return PhysicalRegion.FromPoints(
            info.Monitor.Left,
            info.Monitor.Top,
            info.Monitor.Right,
            info.Monitor.Bottom);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int CbSize;
        public RectNative Monitor;
        public RectNative Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RectNative(int Left, int Top, int Right, int Bottom);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PointNative(int X, int Y);
}
