using System.Runtime.CompilerServices;
using AreaRec.Core.Recording;

namespace AreaRec.Capture;

/// <summary>
/// Selects WGC first and retries with DXGI Desktop Duplication when WGC cannot start.
/// </summary>
public sealed class PreferredCaptureSource : ICaptureSource, IFrameDropMetrics, INativeGraphicsContext
{
    private readonly WindowsGraphicsCaptureSource _primary = new();
    private readonly DesktopDuplicationCaptureSource _fallback = new();
    private ICaptureSource? _active;

    public string BackendName => _active?.BackendName ?? _primary.BackendName;

    public long DroppedFrames => (_active as IFrameDropMetrics)?.DroppedFrames ?? 0;

    public long DuplicatedFrames => (_active as IFrameDropMetrics)?.DuplicatedFrames ?? 0;

    public nint NativeDevice => (_active as INativeGraphicsContext)?.NativeDevice ?? 0;

    public nint NativeContext => (_active as INativeGraphicsContext)?.NativeContext ?? 0;

    public async ValueTask StartAsync(CaptureSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            await _primary.StartAsync(settings, cancellationToken).ConfigureAwait(false);
            _active = _primary;
            return;
        }
        catch (Exception primaryException) when (primaryException is not OperationCanceledException)
        {
            await _primary.DisposeAsync().ConfigureAwait(false);
            if (settings.IncludeCursor)
            {
                throw new PlatformNotSupportedException(
                    "The DXGI fallback cannot compose cursor pixels; disable cursor capture or restore Windows Graphics Capture.",
                    primaryException);
            }
        }

        await _fallback.StartAsync(settings, cancellationToken).ConfigureAwait(false);
        _active = _fallback;
    }

    public async IAsyncEnumerable<CapturedFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var active = _active ?? throw new InvalidOperationException("The capture source has not started.");
        await foreach (var frame in active.CaptureAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _primary.DisposeAsync().ConfigureAwait(false);
        await _fallback.DisposeAsync().ConfigureAwait(false);
        _active = null;
    }
}
