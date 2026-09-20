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
        if (_monitorRegions.Count < 2)
        {
            throw new ArgumentException(
                "The selected region must intersect at least two display monitors for a composite source.",
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
        while (!cancellationToken.IsCancellationRequested)
        {
            var nextFrames = await Task.WhenAll(
                enumerators.Select(enumerator => enumerator.MoveNextAsync().AsTask())).ConfigureAwait(false);
            if (nextFrames.Any(hasFrame => !hasFrame))
            {
                for (var index = 0; index < enumerators.Count; index++)
                {
                    if (nextFrames[index])
                    {
                        enumerators[index].Current.Surface.Dispose();
                    }
                }

                yield break;
            }

            var frames = enumerators.Select(enumerator => enumerator.Current).ToArray();
            var components = frames
                .Select(frame => frame.Surface as INativeTextureFrameSurface
                    ?? throw new InvalidOperationException("A monitor backend returned a non-native frame."))
                .ToArray();
            var timestamp = frames.Max(frame => frame.Timestamp);
            yield return new CapturedFrame(
                Interlocked.Increment(ref _sequence),
                timestamp,
                new CompositeNativeFrameSurface(_targetRegion, components));
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
}

public static class CaptureSourceFactory
{
    public static ICaptureSource Create(PhysicalRegion targetRegion)
    {
        var monitors = MonitorRegionEnumerator.FindIntersecting(targetRegion);
        return monitors.Count > 1
            ? new MultiMonitorCaptureSource(targetRegion)
            : new PreferredCaptureSource();
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
