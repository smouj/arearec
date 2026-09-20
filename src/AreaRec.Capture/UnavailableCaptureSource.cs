using AreaRec.Core.Recording;

namespace AreaRec.Capture;

public sealed class UnavailableCaptureSource : ICaptureSource
{
    public string BackendName => "unavailable";

    public ValueTask StartAsync(CaptureSettings settings, CancellationToken cancellationToken)
    {
        settings.Validate();
        throw new PlatformNotSupportedException(
            "No Windows capture backend is available for the current platform or display session.");
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
