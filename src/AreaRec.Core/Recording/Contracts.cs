namespace AreaRec.Core.Recording;

public interface IFrameSurface : IDisposable
{
    int Width { get; }
    int Height { get; }
}

/// <summary>
/// Optional CPU-readable BGRA surface used by software interop and encoder tests.
/// GPU-native surfaces do not need to implement this interface.
/// </summary>
public interface IBgra32FrameSurface : IFrameSurface
{
    int Stride { get; }
    ReadOnlyMemory<byte> Bgra32 { get; }
}

public interface ICloneableFrameSurface : IFrameSurface
{
    IFrameSurface Clone();
}

public interface INativeTextureFrameSurface : IFrameSurface
{
    nint NativeTexture { get; }
    nint NativeDevice { get; }
    nint NativeContext { get; }
    PhysicalRegion SourceRegion { get; }
}

public interface ICompositeNativeFrameSurface : IFrameSurface
{
    PhysicalRegion TargetRegion { get; }
    IReadOnlyList<INativeTextureFrameSurface> Components { get; }
}

public readonly record struct CapturedFrame(
    long Sequence,
    TimeSpan Timestamp,
    IFrameSurface Surface);

public interface ICaptureSource : IAsyncDisposable
{
    string BackendName { get; }
    ValueTask StartAsync(CaptureSettings settings, CancellationToken cancellationToken);
    IAsyncEnumerable<CapturedFrame> CaptureAsync(CancellationToken cancellationToken);
}

public interface IFrameDropMetrics
{
    long DroppedFrames { get; }
    long DuplicatedFrames { get; }
}

public interface IFrameProcessor : IAsyncDisposable
{
    ValueTask<IFrameSurface> ProcessAsync(
        IFrameSurface source,
        PhysicalRegion region,
        CancellationToken cancellationToken);
}

public interface IVideoEncoder : IAsyncDisposable
{
    string EncoderName { get; }
    ValueTask StartAsync(CaptureSettings settings, Stream output, CancellationToken cancellationToken);
    ValueTask EncodeAsync(CapturedFrame frame, CancellationToken cancellationToken);
    ValueTask CompleteAsync(CancellationToken cancellationToken);
}

public interface IRecordingSink : IAsyncDisposable
{
    ValueTask OpenAsync(CaptureSettings settings, CancellationToken cancellationToken);
    ValueTask WriteAsync(CapturedFrame frame, CancellationToken cancellationToken);
    ValueTask CompleteAsync(CancellationToken cancellationToken);
    ValueTask AbortAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Optional audio-capable recording sink. The audio format is supplied before
/// the sink starts writing so a container can declare both tracks atomically.
/// </summary>
public interface IAudioRecordingSink
{
    ValueTask OpenWithAudioAsync(
        CaptureSettings settings,
        AudioFormat audioFormat,
        CancellationToken cancellationToken);

    ValueTask WriteAudioAsync(AudioChunk chunk, CancellationToken cancellationToken);
}

public interface IAudioSource : IAsyncDisposable
{
    string SourceName { get; }
    AudioFormat Format { get; }
    ValueTask<AudioFormat> InitializeAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<AudioChunk> CaptureAsync(CancellationToken cancellationToken);
}

public interface IRecordingSession : IAsyncDisposable
{
    RecordingState State { get; }
    RecordingStatistics Statistics { get; }
    ValueTask StartAsync(CaptureSettings settings, CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
}
