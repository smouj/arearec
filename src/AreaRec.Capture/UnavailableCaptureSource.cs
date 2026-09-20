using AreaRec.Core.Recording;

namespace AreaRec.Capture;

public sealed class UnavailableCaptureSource : ICaptureSource
{
    public string BackendName => "unavailable";

    public ValueTask StartAsync(CaptureSettings settings, CancellationToken cancellationToken)
    {
        settings.Validate();
        throw new PlatformNotSupportedException(
            "No capture backend is registered. Windows Graphics Capture is implemented in the Windows platform project in a later migration phase.");
    }

    public async IAsyncEnumerable<CapturedFrame> CaptureAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            yield break;
        }

        await Task.Yield();
        throw new PlatformNotSupportedException("No capture backend is registered.");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
