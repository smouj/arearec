namespace AreaRec.Core.Recording;

public sealed class RecordingSession : IRecordingSession
{
    private readonly ICaptureSource _capture;
    private readonly IRecordingSink _sink;
    private readonly IAudioSource? _audioSource;
    private IFrameProcessor? _processor;
    private readonly Func<ICaptureSource, IFrameProcessor>? _processorFactory;
    private readonly IMonotonicClock _clock;
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _consumer;
    private Task? _audioConsumer;
    private RecordingState _state = RecordingState.Idle;
    private RecordingStatistics _statistics;
    private Exception? _failure;
    private TimeSpan _startedAt;
    private TimeSpan? _mediaOriginTimestamp;
    private bool _disposed;

    public RecordingSession(
        ICaptureSource capture,
        IRecordingSink sink,
        IMonotonicClock? clock = null,
        IFrameProcessor? processor = null,
        Func<ICaptureSource, IFrameProcessor>? processorFactory = null,
        IAudioSource? audioSource = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _audioSource = audioSource;
        _clock = clock ?? new StopwatchClock();
        _processor = processor;
        _processorFactory = processorFactory;
    }

    public RecordingState State
    {
        get { lock (_gate) return _state; }
    }

    public RecordingStatistics Statistics
    {
        get { lock (_gate) return _statistics; }
    }

    public async ValueTask StartAsync(CaptureSettings settings, CancellationToken cancellationToken)
    {
        settings.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state is not RecordingState.Idle)
            {
                throw new InvalidOperationException($"Cannot start a session from {_state}.");
            }

            _state = RecordingState.Starting;
            _failure = null;
            _statistics = default;
            _mediaOriginTimestamp = null;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        try
        {
            IAudioRecordingSink? audioSink = null;
            if (_audioSource is not null)
            {
                audioSink = _sink as IAudioRecordingSink
                    ?? throw new InvalidOperationException(
                        "An audio source requires a recording sink that supports an audio track.");
                var audioFormat = await _audioSource.InitializeAsync(cancellationToken).ConfigureAwait(false);
                audioFormat.Validate();
                await audioSink.OpenWithAudioAsync(settings, audioFormat, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _sink.OpenAsync(settings, cancellationToken).ConfigureAwait(false);
            }

            await _capture.StartAsync(settings, cancellationToken).ConfigureAwait(false);
            _processor ??= _processorFactory?.Invoke(_capture);
            lock (_gate)
            {
                _startedAt = _clock.Now;
                _state = RecordingState.Recording;
                _consumer = ConsumeAsync(settings, _cancellation.Token);
                _audioConsumer = audioSink is null
                    ? null
                    : ConsumeAudioAsync(audioSink, _cancellation.Token);
            }
        }
        catch
        {
            lock (_gate)
            {
                _state = RecordingState.Error;
            }

            await AbortResourcesAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Task? consumer;
        Task? audioConsumer;
        lock (_gate)
        {
            if (_state is RecordingState.Idle or RecordingState.Completed)
            {
                return;
            }

            if (_state is RecordingState.Error)
            {
                throw new InvalidOperationException("The recording session failed.", _failure);
            }

            _state = RecordingState.Stopping;
            _cancellation?.Cancel();
            consumer = _consumer;
            audioConsumer = _audioConsumer;
        }

        if (consumer is not null)
        {
            await consumer.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (audioConsumer is not null)
        {
            await audioConsumer.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        Exception? failure;
        lock (_gate) failure = _failure;
        if (failure is not null)
        {
            lock (_gate) _state = RecordingState.Error;
            await _sink.AbortAsync(CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("The recording session failed.", failure);
        }

        lock (_gate)
        {
            if (_statistics.EncodedFrames == 0)
            {
                _failure = new InvalidOperationException("The capture source produced no video frames.");
                _state = RecordingState.Error;
            }
        }

        if (State is RecordingState.Error)
        {
            await _sink.AbortAsync(CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("The recording did not contain any video frames.", _failure);
        }

        lock (_gate) _state = RecordingState.Saving;
        try
        {
            await _sink.CompleteAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                UpdateCaptureDuration();
                _state = RecordingState.Completed;
            }
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _failure = exception;
                _state = RecordingState.Error;
            }

            await _sink.AbortAsync(CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("The recording could not be finalized safely.", exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        if (State is RecordingState.Recording or RecordingState.Starting or RecordingState.Stopping)
        {
            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                await AbortResourcesAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        if (_processor is not null)
        {
            await _processor.DisposeAsync().ConfigureAwait(false);
            _processor = null;
        }
        await _capture.DisposeAsync().ConfigureAwait(false);
        if (_audioSource is not null)
        {
            await _audioSource.DisposeAsync().ConfigureAwait(false);
        }
        await _sink.DisposeAsync().ConfigureAwait(false);
        _cancellation?.Dispose();
    }

    private async Task ConsumeAsync(CaptureSettings settings, CancellationToken cancellationToken)
    {
        var targetPeriod = TimeSpan.FromSeconds(1d / settings.FramesPerSecond);
        var nextSourceDeadline = targetPeriod;
        var haveOutputFrame = false;
        var pacingDroppedFrames = 0L;
        var pacingDuplicatedFrames = 0L;
        var nextOutputTimestamp = TimeSpan.Zero;
        IFrameSurface? lastOutputSurface = null;
        var lastFrameTemplate = default(CapturedFrame);
        try
        {
            await foreach (var frame in _capture.CaptureAsync(cancellationToken).ConfigureAwait(false))
            {
                using (frame.Surface)
                {
                    var timestamp = NormalizeTimestamp(frame.Timestamp);
                    lock (_gate)
                    {
                        _statistics = _statistics with { CapturedFrames = _statistics.CapturedFrames + 1 };
                    }

                    if (haveOutputFrame && timestamp < nextSourceDeadline)
                    {
                        pacingDroppedFrames++;
                        continue;
                    }

                    if (haveOutputFrame)
                    {
                        while (lastOutputSurface is not null && nextOutputTimestamp < timestamp)
                        {
                            var duplicate = (lastOutputSurface as ICloneableFrameSurface)?.Clone();
                            if (duplicate is null)
                            {
                                break;
                            }

                            using (duplicate)
                            {
                                await EncodeAndAccountAsync(
                                    frame,
                                    duplicate,
                                    nextOutputTimestamp,
                                    isDuplicate: true,
                                    cancellationToken).ConfigureAwait(false);
                            }

                            pacingDuplicatedFrames++;
                            nextOutputTimestamp += targetPeriod;
                        }
                    }

                    haveOutputFrame = true;
                    var outputTimestamp = nextOutputTimestamp;
                    nextOutputTimestamp += targetPeriod;
                    while (nextSourceDeadline <= timestamp)
                    {
                        nextSourceDeadline += targetPeriod;
                    }

                    IFrameSurface? processedSurface = null;
                    if (_processor is not null)
                    {
                        processedSurface = await _processor.ProcessAsync(
                            frame.Surface,
                            settings.Region,
                            cancellationToken).ConfigureAwait(false);
                    }

                    using (processedSurface)
                    {
                        var outputSurface = processedSurface ?? frame.Surface;
                        await EncodeAndAccountAsync(
                            frame,
                            outputSurface,
                            outputTimestamp,
                            isDuplicate: false,
                            cancellationToken).ConfigureAwait(false);

                        var retained = (outputSurface as ICloneableFrameSurface)?.Clone();
                        lastOutputSurface?.Dispose();
                        lastOutputSurface = retained;
                        lastFrameTemplate = frame;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _failure = exception;
                _statistics = _statistics with { CaptureDuration = _clock.Now - _startedAt };
            }
        }
        finally
        {
            Exception? flushFailure = null;
            lock (_gate)
            {
                flushFailure = _failure;
            }

            if (flushFailure is null && lastOutputSurface is not null && haveOutputFrame)
            {
                try
                {
                    var captureDuration = _clock.Now - _startedAt;
                    while (nextOutputTimestamp < captureDuration)
                    {
                        var duplicate = (lastOutputSurface as ICloneableFrameSurface)?.Clone();
                        if (duplicate is null)
                        {
                            break;
                        }

                        using (duplicate)
                        {
                            await EncodeAndAccountAsync(
                                lastFrameTemplate,
                                duplicate,
                                nextOutputTimestamp,
                                isDuplicate: true,
                                CancellationToken.None).ConfigureAwait(false);
                        }

                        pacingDuplicatedFrames++;
                        nextOutputTimestamp += targetPeriod;
                    }
                }
                catch (Exception exception)
                {
                    lock (_gate)
                    {
                        _failure = exception;
                    }
                }
            }

            lock (_gate)
            {
                var sourceDroppedFrames = _capture is IFrameDropMetrics metrics ? metrics.DroppedFrames : 0;
                var sourceDuplicatedFrames = _capture is IFrameDropMetrics duplicateMetrics
                    ? duplicateMetrics.DuplicatedFrames
                    : 0;
                _statistics = _statistics with
                {
                    DroppedFrames = sourceDroppedFrames + pacingDroppedFrames,
                    DuplicatedFrames = sourceDuplicatedFrames + pacingDuplicatedFrames,
                };

                UpdateCaptureDuration();
            }

            lastOutputSurface?.Dispose();
        }
    }

    private async ValueTask EncodeAndAccountAsync(
        CapturedFrame frame,
        IFrameSurface surface,
        TimeSpan timestamp,
        bool isDuplicate,
        CancellationToken cancellationToken)
    {
        var normalized = frame with { Timestamp = timestamp, Surface = surface };
        var encodeStart = _clock.Now;
        await _sink.WriteAsync(normalized, cancellationToken).ConfigureAwait(false);
        var encodeDuration = _clock.Now - encodeStart;
        lock (_gate)
        {
            _statistics = _statistics with
            {
                EncodedFrames = _statistics.EncodedFrames + 1,
                EncodeDuration = _statistics.EncodeDuration + encodeDuration,
                DuplicatedFrames = isDuplicate ? _statistics.DuplicatedFrames + 1 : _statistics.DuplicatedFrames,
            };
        }
    }

    private TimeSpan NormalizeTimestamp(TimeSpan timestamp)
    {
        lock (_gate)
        {
            _mediaOriginTimestamp ??= timestamp;
            return timestamp - _mediaOriginTimestamp.Value;
        }
    }

    private async Task ConsumeAudioAsync(
        IAudioRecordingSink sink,
        CancellationToken cancellationToken)
    {
        try
        {
            // Audio sources such as WASAPI may perform a blocking wait before
            // their first packet. Do not let that synchronous prefix delay the
            // video consumer or the completion of StartAsync.
            await Task.Yield();
            await foreach (var chunk in _audioSource!.CaptureAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = chunk with { Timestamp = NormalizeTimestamp(chunk.Timestamp) };
                await sink.WriteAudioAsync(normalized, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _failure ??= exception;
            }
        }
    }

    private void UpdateCaptureDuration()
    {
        _statistics = _statistics with { CaptureDuration = _clock.Now - _startedAt };
    }

    private async ValueTask AbortResourcesAsync(CancellationToken cancellationToken)
    {
        _cancellation?.Cancel();
        await _sink.AbortAsync(cancellationToken).ConfigureAwait(false);
        await _capture.DisposeAsync().ConfigureAwait(false);
        if (_audioSource is not null)
        {
            await _audioSource.DisposeAsync().ConfigureAwait(false);
        }
    }
}
