using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AreaRec.Core.Recording;

namespace AreaRec.Capture;

/// <summary>
/// Captures every display intersecting a selected physical region and exposes
/// the native component textures as one compositable frame.
/// </summary>
public sealed class MultiMonitorCaptureSource : ICaptureSource, IFrameDropMetrics, INativeGraphicsContext
{
    private readonly PhysicalRegion _targetRegion;
    private readonly IReadOnlyList<PhysicalRegion> _monitorRegions;
    private readonly object _gate = new();
    private List<PreferredCaptureSource>? _sources;
    private List<IAsyncEnumerator<CapturedFrame>>? _enumerators;
    private long _sequence;
    private bool _disposed;

    public MultiMonitorCaptureSource(PhysicalRegion targetRegion)
    {
        _targetRegion = targetRegion.NormalizeForH264();
        _monitorRegions = MonitorRegionEnumerator.FindIntersecting(_targetRegion);
        if (_monitorRegions.Count == 0)
        {
            throw new ArgumentException(
                "The selected region must intersect at least one display monitor for a composite source.",
                nameof(targetRegion));
        }
    }

    public string BackendName => "multi-monitor-composite";

    public long DroppedFrames => _sources?.Sum(source => source.DroppedFrames) ?? 0;

    public long DuplicatedFrames => _sources?.Sum(source => source.DuplicatedFrames) ?? 0;

    public nint NativeDevice => _sources?.FirstOrDefault()?.NativeDevice ?? 0;

    public nint NativeContext => _sources?.FirstOrDefault()?.NativeContext ?? 0;

    public async ValueTask StartAsync(CaptureSettings settings, CancellationToken cancellationToken)
    {
        settings.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sources is not null)
            {
                throw new InvalidOperationException("The capture source has already started.");
            }
        }

        var sources = new List<PreferredCaptureSource>(_monitorRegions.Count);
        try
        {
            foreach (var monitorRegion in _monitorRegions)
            {
                var source = new PreferredCaptureSource();
                sources.Add(source);
                var anchor = new PhysicalRegion(
                    monitorRegion.X + 1,
                    monitorRegion.Y + 1,
                    Math.Min(2, monitorRegion.Width - 1),
                    Math.Min(2, monitorRegion.Height - 1));
                await source.StartAsync(
                    settings with { Region = anchor },
                    cancellationToken).ConfigureAwait(false);
            }

            var enumerators = sources
                .Select(source => source.CaptureAsync(cancellationToken).GetAsyncEnumerator(cancellationToken))
                .ToList();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _sources = sources;
                _enumerators = enumerators;
            }
        }
        catch
        {
            foreach (var source in sources)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    public async IAsyncEnumerable<CapturedFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enumerators = _enumerators ?? throw new InvalidOperationException("The capture source has not started.");
        var latest = new RetainedNativeTextureFrameSurface?[enumerators.Count];
        var latestTimestamps = new TimeSpan[enumerators.Count];
        var currentFrameOwned = new bool[enumerators.Count];
        var initialPending = Array.Empty<Task<bool>>();
        Task<bool>[]? pending = null;

        try
        {
            // Do not require all monitors to produce a new frame at the same
            // instant. Each monitor owns an independent capture clock; the
            // compositor uses the newest texture from each one.
            initialPending = enumerators
                .Select(enumerator => enumerator.MoveNextAsync().AsTask())
                .ToArray();
            var remainingInitial = new HashSet<int>(Enumerable.Range(0, enumerators.Count));
            while (remainingInitial.Count > 0)
            {
                var completed = await Task.WhenAny(
                    remainingInitial.Select(index => initialPending[index])).ConfigureAwait(false);
                var index = Array.IndexOf(initialPending, completed);
                var hasFrame = await completed.ConfigureAwait(false);
                if (!hasFrame)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        yield break;
                    }

                    throw new InvalidOperationException(
                        "A monitor capture ended before the multi-monitor compositor received its first frame.");
                }

                currentFrameOwned[index] = true;
                (latest[index], latestTimestamps[index]) = RetainCurrentFrame(enumerators[index]);
                currentFrameOwned[index] = false;
                remainingInitial.Remove(index);
            }

            yield return CreateCompositeFrame(latest, latestTimestamps);

            pending = enumerators
                .Select(enumerator => enumerator.MoveNextAsync().AsTask())
                .ToArray();
            while (!cancellationToken.IsCancellationRequested)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                var index = Array.IndexOf(pending, completed);
                var hasFrame = await completed.ConfigureAwait(false);
                if (!hasFrame)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        yield break;
                    }

                    throw new InvalidOperationException(
                        "A monitor capture ended while the multi-monitor compositor was recording.");
                }

                currentFrameOwned[index] = true;
                var retained = RetainCurrentFrame(enumerators[index]);
                currentFrameOwned[index] = false;
                latest[index]?.Dispose();
                latest[index] = retained.Surface;
                latestTimestamps[index] = retained.Timestamp;
                pending[index] = enumerators[index].MoveNextAsync().AsTask();
                yield return CreateCompositeFrame(latest, latestTimestamps);
            }
        }
        finally
        {
            for (var index = 0; index < latest.Length; index++)
            {
                var initialFrameNeedsDisposal = latest[index] is null &&
                    index < initialPending.Length &&
                    initialPending[index].IsCompletedSuccessfully &&
                    initialPending[index].Result;
                var pendingFrameNeedsDisposal = pending is not null &&
                    pending[index].IsCompletedSuccessfully &&
                    pending[index].Result;
                if (currentFrameOwned[index] || initialFrameNeedsDisposal || pendingFrameNeedsDisposal)
                {
                    enumerators[index].Current.Surface.Dispose();
                }

                latest[index]?.Dispose();
            }
        }
    }

    private CapturedFrame CreateCompositeFrame(
        IReadOnlyList<RetainedNativeTextureFrameSurface?> latest,
        IReadOnlyList<TimeSpan> timestamps)
    {
        var components = new List<INativeTextureFrameSurface>(latest.Count);
        try
        {
            foreach (var surface in latest)
            {
                components.Add(surface?.CloneLease()
                    ?? throw new InvalidOperationException("A monitor compositor component is unavailable."));
            }

            return new CapturedFrame(
                Interlocked.Increment(ref _sequence),
                timestamps.Max(),
                new CompositeNativeFrameSurface(_targetRegion, components.ToArray()));
        }
        catch
        {
            foreach (var component in components)
            {
                component.Dispose();
            }

            throw;
        }
    }

    private static (RetainedNativeTextureFrameSurface Surface, TimeSpan Timestamp) RetainCurrentFrame(
        IAsyncEnumerator<CapturedFrame> enumerator)
    {
        var frame = enumerator.Current;
        try
        {
            if (frame.Surface is not INativeTextureFrameSurface nativeSurface)
            {
                throw new InvalidOperationException("A monitor backend returned a non-native frame.");
            }

            return (RetainedNativeTextureFrameSurface.From(nativeSurface), frame.Timestamp);
        }
        finally
        {
            frame.Surface.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<IAsyncEnumerator<CapturedFrame>>? enumerators;
        List<PreferredCaptureSource>? sources;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            enumerators = _enumerators;
            sources = _sources;
            _enumerators = null;
            _sources = null;
        }

        if (enumerators is not null)
        {
            foreach (var enumerator in enumerators)
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (sources is not null)
        {
            foreach (var source in sources)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class CompositeNativeFrameSurface : ICompositeNativeFrameSurface
    {
        private readonly INativeTextureFrameSurface[] _components;
        private bool _disposed;

        public CompositeNativeFrameSurface(
            PhysicalRegion targetRegion,
            INativeTextureFrameSurface[] components)
        {
            TargetRegion = targetRegion;
            _components = components;
        }

        public int Width => TargetRegion.Width;

        public int Height => TargetRegion.Height;

        public PhysicalRegion TargetRegion { get; }

        public IReadOnlyList<INativeTextureFrameSurface> Components => _components;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var component in _components)
            {
                component.Dispose();
            }
        }
    }

    private sealed class RetainedNativeTextureFrameSurface : INativeTextureFrameSurface
    {
        private nint _nativeTexture;

        private RetainedNativeTextureFrameSurface(
            nint nativeTexture,
            nint nativeDevice,
            nint nativeContext,
            PhysicalRegion sourceRegion)
        {
            _nativeTexture = nativeTexture;
            NativeDevice = nativeDevice;
            NativeContext = nativeContext;
            SourceRegion = sourceRegion;
        }

        public int Width => SourceRegion.Width;

        public int Height => SourceRegion.Height;

        public nint NativeTexture => _nativeTexture == 0
            ? throw new ObjectDisposedException(nameof(RetainedNativeTextureFrameSurface))
            : _nativeTexture;

        public nint NativeDevice { get; }

        public nint NativeContext { get; }

        public PhysicalRegion SourceRegion { get; }

        public static RetainedNativeTextureFrameSurface From(INativeTextureFrameSurface source)
        {
            var nativeTexture = source.NativeTexture;
            if (nativeTexture == 0)
            {
                throw new InvalidOperationException("A monitor backend returned an empty native texture.");
            }

            Marshal.AddRef(nativeTexture);
            return new RetainedNativeTextureFrameSurface(
                nativeTexture,
                source.NativeDevice,
                source.NativeContext,
                source.SourceRegion);
        }

        public RetainedNativeTextureFrameSurface CloneLease()
        {
            var nativeTexture = NativeTexture;
            Marshal.AddRef(nativeTexture);
            return new RetainedNativeTextureFrameSurface(
                nativeTexture,
                NativeDevice,
                NativeContext,
                SourceRegion);
        }

        public void Dispose()
        {
            var nativeTexture = Interlocked.Exchange(ref _nativeTexture, 0);
            if (nativeTexture != 0)
            {
                Marshal.Release(nativeTexture);
            }
        }
    }
}

public static class CaptureSourceFactory
{
    public static ICaptureSource Create(PhysicalRegion targetRegion)
    {
        var normalizedTarget = targetRegion.NormalizeForH264();
        var monitors = MonitorRegionEnumerator.FindIntersecting(normalizedTarget);
        if (monitors.Count == 0)
        {
            throw new ArgumentException(
                "The selected region does not intersect an attached display monitor.",
                nameof(targetRegion));
        }

        var fitsOneMonitor = monitors.Count == 1 && monitors[0].Contains(normalizedTarget);
        return fitsOneMonitor
            ? new PreferredCaptureSource()
            : new MultiMonitorCaptureSource(normalizedTarget);
    }
}

internal static class MonitorRegionEnumerator
{
    public static IReadOnlyList<PhysicalRegion> FindIntersecting(PhysicalRegion targetRegion)
    {
        var monitors = new List<PhysicalRegion>();
        EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (monitor, _, _, _) =>
            {
                var info = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(monitor, ref info))
                {
                    return true;
                }

                var region = new PhysicalRegion(
                    info.Monitor.Left,
                    info.Monitor.Top,
                    info.Monitor.Right - info.Monitor.Left,
                    info.Monitor.Bottom - info.Monitor.Top);
                if (region.Intersection(targetRegion).HasValue)
                {
                    monitors.Add(region);
                }

                return true;
            },
            IntPtr.Zero);
        return PhysicalRegionLayout.Partition(targetRegion, monitors)
            .Select(segment => segment.MonitorRegion)
            .ToArray();
    }

    private delegate bool MonitorEnumProc(nint monitor, nint deviceContext, nint monitorRect, nint data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        nint deviceContext,
        nint clipRect,
        MonitorEnumProc callback,
        nint data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

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
}
