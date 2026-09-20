using System.Buffers.Binary;
using AreaRec.Core.Recording;

namespace AreaRec.Core.Tests;

internal static class Program
{
    private static int Main()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("normalizes direction and H.264 dimensions", RegionNormalizes),
            ("intersects physical monitor regions", RegionIntersects),
            ("partitions physical regions across monitor boundaries", RegionLayoutPartitions),
            ("preserves offsets for partially off-screen regions", RegionLayoutPreservesOffscreenOffset),
            ("accepts configured frame rates", SettingsValidate),
            ("rejects unsupported frame rates", SettingsRejectsUnsupportedRate),
            ("rejects unsupported video quality", SettingsRejectsUnsupportedQuality),
            ("pacer advances by a stable period", PacerAdvances),
            ("session normalizes timestamps and accounts frames", SessionAccountsFrames),
            ("session drops frames above the requested cadence", SessionDropsFrames),
            ("session duplicates cloneable frames across capture gaps", SessionDuplicatesFrames),
            ("session controls finalization failures", SessionControlsFinalizationFailure),
            ("session rejects empty captures", SessionRejectsEmptyCapture),
            ("session shares one timestamp origin with audio", SessionSharesAudioClock),
            ("audio mixer saturates aligned PCM chunks", AudioMixerSaturates),
            ("audio resampler interpolates PCM frames", AudioResamplerInterpolates),
        };

        foreach (var test in tests)
        {
            try
            {
                test.Body();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
                return 1;
            }
        }

        Console.WriteLine($"{tests.Length} core tests passed.");
        return 0;
    }

    private static void RegionNormalizes()
    {
        var region = PhysicalRegion.FromPoints(200, 100, 101, 51);
        Assert(region == new PhysicalRegion(101, 51, 98, 48), "unexpected normalized region");
    }

    private static void SettingsValidate()
    {
        new CaptureSettings(new PhysicalRegion(-100, 20, 1280, 720), 60).Validate();
    }

    private static void RegionIntersects()
    {
        var selected = new PhysicalRegion(-100, 50, 300, 200);
        var monitor = new PhysicalRegion(-200, 0, 150, 300);
        var intersection = selected.Intersection(monitor);
        Assert(intersection == new PhysicalRegion(-100, 50, 50, 200), "negative-coordinate intersection mismatch");
        Assert(!selected.Intersection(new PhysicalRegion(500, 0, 100, 100)).HasValue, "disjoint regions intersected");
    }

    private static void RegionLayoutPartitions()
    {
        var target = new PhysicalRegion(-200, 100, 2_400, 400);
        var monitors = new[]
        {
            new PhysicalRegion(-1_920, 0, 1_920, 1_080),
            new PhysicalRegion(0, 0, 1_920, 1_080),
            new PhysicalRegion(1_920, 0, 1_920, 1_080),
        };

        var segments = PhysicalRegionLayout.Partition(target, monitors);

        Assert(segments.Count == 3, "physical selection was not partitioned across all monitors");
        Assert(segments[0].Intersection == new PhysicalRegion(-200, 100, 200, 400), "negative monitor segment mismatch");
        Assert(segments[1].DestinationX == 200 && segments[1].Intersection.Width == 1_920, "middle monitor offset mismatch");
        Assert(segments[2].DestinationX == 2_120 && segments[2].Intersection.Width == 280, "right monitor segment mismatch");
    }

    private static void RegionLayoutPreservesOffscreenOffset()
    {
        var target = new PhysicalRegion(-100, 20, 200, 100);
        var monitor = new PhysicalRegion(0, 0, 1_920, 1_080);
        var segments = PhysicalRegionLayout.Partition(target, [monitor]);

        Assert(segments.Count == 1, "partially off-screen region lost its visible monitor segment");
        Assert(segments[0].Intersection == new PhysicalRegion(0, 20, 100, 100), "off-screen intersection mismatch");
        Assert(segments[0].DestinationX == 100, "off-screen black-fill offset mismatch");
    }

    private static void SettingsRejectsUnsupportedRate()
    {
        try
        {
            new CaptureSettings(new PhysicalRegion(0, 0, 100, 100), 24).Validate();
            throw new InvalidOperationException("unsupported rate was accepted");
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }

    private static void SettingsRejectsUnsupportedQuality()
    {
        try
        {
            new CaptureSettings(
                new PhysicalRegion(0, 0, 100, 100),
                Quality: (VideoQuality)999).Validate();
            throw new InvalidOperationException("unsupported video quality was accepted");
        }
        catch (ArgumentOutOfRangeException exception) when (exception.ParamName == "Quality")
        {
        }
    }

    private static void PacerAdvances()
    {
        var clock = new FakeClock();
        var pacer = new FramePacer(30, clock);
        pacer.Start();
        var delay = pacer.Advance();
        Assert(delay == TimeSpan.FromSeconds(1d / 30), "unexpected frame period");
    }

    private static void SessionAccountsFrames()
    {
        var source = new FakeCaptureSource(40);
        var sink = new FakeSink();
        var session = new RecordingSession(source, sink, new FakeClock());
        var settings = new CaptureSettings(new PhysicalRegion(0, 0, 100, 100));

        session.StartAsync(settings, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        session.StopAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var statistics = session.Statistics;
        session.DisposeAsync().AsTask().GetAwaiter().GetResult();

        Assert(session.State == RecordingState.Completed, "session did not complete");
        Assert(statistics.CapturedFrames == 3, "captured frame count mismatch");
        Assert(statistics.EncodedFrames == 3, "encoded frame count mismatch");
        Assert(sink.Timestamps[0] == TimeSpan.Zero, "first timestamp was not normalized");
        Assert(sink.Timestamps[2] == TimeSpan.FromSeconds(2d / 30), "timestamp drifted");
    }

    private static void SessionDropsFrames()
    {
        var source = new FakeCaptureSource(10);
        var sink = new FakeSink();
        var session = new RecordingSession(source, sink, new FakeClock());
        var settings = new CaptureSettings(new PhysicalRegion(0, 0, 100, 100));

        session.StartAsync(settings, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        session.StopAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var statistics = session.Statistics;
        session.DisposeAsync().AsTask().GetAwaiter().GetResult();

        Assert(statistics.CapturedFrames == 3, "cadence test captured frame count mismatch");
        Assert(statistics.EncodedFrames == 1, "cadence did not drop high-rate frames");
        Assert(statistics.DroppedFrames == 2, "cadence drop count mismatch");
    }

    private static void SessionControlsFinalizationFailure()
    {
        var source = new FakeCaptureSource(40);
        var sink = new FakeSink { FailComplete = true };
        var session = new RecordingSession(source, sink, new FakeClock());
        var settings = new CaptureSettings(new PhysicalRegion(0, 0, 100, 100));

        session.StartAsync(settings, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        try
        {
            session.StopAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            throw new InvalidOperationException("finalization failure was not reported");
        }
        catch (InvalidOperationException)
        {
        }

        Assert(session.State == RecordingState.Error, "finalization failure did not enter Error state");
        Assert(sink.AbortCount == 1, "failed finalization did not abort the sink");
        session.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static void SessionDuplicatesFrames()
    {
        var source = new FakeCaptureSource(100, cloneable: true);
        var sink = new FakeSink();
        var session = new RecordingSession(source, sink, new FakeClock());
        var settings = new CaptureSettings(new PhysicalRegion(0, 0, 100, 100));

        session.StartAsync(settings, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        session.StopAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var statistics = session.Statistics;
        session.DisposeAsync().AsTask().GetAwaiter().GetResult();

        Assert(statistics.EncodedFrames > statistics.CapturedFrames, "capture gaps were not filled");
        Assert(statistics.DuplicatedFrames > 0, "duplicate frame count was not recorded");
    }

    private static void SessionRejectsEmptyCapture()
    {
        var source = new EmptyCaptureSource();
        var sink = new FakeSink();
        var session = new RecordingSession(source, sink, new FakeClock());
        var settings = new CaptureSettings(new PhysicalRegion(0, 0, 100, 100));

        session.StartAsync(settings, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        try
        {
            session.StopAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            throw new InvalidOperationException("empty capture was accepted");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("video frames", StringComparison.Ordinal))
        {
        }

        Assert(session.State == RecordingState.Error, "empty capture did not enter Error state");
        Assert(sink.AbortCount == 1, "empty capture did not abort the sink");
        session.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static void AudioMixerSaturates()
    {
        var format = new AudioFormat(48_000, 1, 16);
        var mixer = new PcmAudioMixer(format);
        var first = new byte[2];
        var second = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(first, 16_384);
        BinaryPrimitives.WriteInt16LittleEndian(second, 16_384);

        var mixed = mixer.MixAsync(
            [
                new AudioChunk(TimeSpan.FromMilliseconds(20), first),
                new AudioChunk(TimeSpan.FromMilliseconds(10), second),
            ],
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

        Assert(mixed.Timestamp == TimeSpan.FromMilliseconds(10), "mixer did not preserve the earliest timestamp");
        Assert(BinaryPrimitives.ReadInt16LittleEndian(mixed.Data.Span) == short.MaxValue, "mixer did not saturate PCM16 output");
    }

    private static void SessionSharesAudioClock()
    {
        var source = new FakeCaptureSource(40);
        var audioSource = new FakeAudioSource();
        var sink = new FakeSink();
        var session = new RecordingSession(source, sink, new FakeClock(), audioSource: audioSource);
        var settings = new CaptureSettings(new PhysicalRegion(0, 0, 100, 100));

        session.StartAsync(settings, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        Assert(SpinWait.SpinUntil(() => sink.AudioTimestamps.Count > 0, TimeSpan.FromSeconds(1)), "audio chunks were not scheduled");
        session.StopAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        session.DisposeAsync().AsTask().GetAwaiter().GetResult();

        Assert(sink.AudioFormat == audioSource.Format, "audio format was not negotiated before writing");
        Assert(sink.AudioTimestamps[0] == TimeSpan.Zero, "audio timestamp did not share the media origin");
    }

    private static void AudioResamplerInterpolates()
    {
        var inputFormat = new AudioFormat(2, 1, 16);
        var outputFormat = new AudioFormat(4, 1, 16);
        var input = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(0, 2), 0);
        BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(2, 2), short.MaxValue);

        var result = new PcmAudioResampler(inputFormat, outputFormat).Resample(
            new AudioChunk(TimeSpan.Zero, input));

        Assert(result.Data.Length == 8, "resampler output frame count mismatch");
        var midpoint = BinaryPrimitives.ReadInt16LittleEndian(result.Data.Span.Slice(2, 2));
        Assert(midpoint is > 16_000 and < 17_000, "resampler did not interpolate the midpoint");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeClock : IMonotonicClock
    {
        public TimeSpan Now => TimeSpan.Zero;
    }

    private sealed class FakeCaptureSource : ICaptureSource
    {
        private readonly int _stepMilliseconds;
        private readonly bool _cloneable;

        public FakeCaptureSource(int stepMilliseconds, bool cloneable = false)
        {
            _stepMilliseconds = stepMilliseconds;
            _cloneable = cloneable;
        }

        public string BackendName => "test";

        public ValueTask StartAsync(CaptureSettings settings, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<CapturedFrame> CaptureAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            for (var i = 0; i < 3; i++)
            {
                yield return new CapturedFrame(
                    i + 1,
                    TimeSpan.FromMilliseconds(i * _stepMilliseconds + 100),
                    _cloneable ? new CloneableFakeSurface() : new FakeSurface());
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyCaptureSource : ICaptureSource
    {
        public string BackendName => "empty-test";

        public ValueTask StartAsync(CaptureSettings settings, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<CapturedFrame> CaptureAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAudioSource : IAudioSource
    {
        public string SourceName => "test-audio";
        public AudioFormat Format => new(48_000, 1, 16);

        public ValueTask<AudioFormat> InitializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Format);
        }

        public async IAsyncEnumerable<AudioChunk> CaptureAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            for (var index = 0; index < 2; index++)
            {
                yield return new AudioChunk(
                    TimeSpan.FromMilliseconds(100 + index * 10),
                    new byte[Format.BlockAlign * 4]);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSink : IRecordingSink, IAudioRecordingSink
    {
        public List<TimeSpan> Timestamps { get; } = [];
        public List<TimeSpan> AudioTimestamps { get; } = [];
        public AudioFormat AudioFormat { get; private set; }
        public bool FailComplete { get; init; }
        public int AbortCount { get; private set; }

        public ValueTask OpenAsync(CaptureSettings settings, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask OpenWithAudioAsync(
            CaptureSettings settings,
            AudioFormat audioFormat,
            CancellationToken cancellationToken)
        {
            AudioFormat = audioFormat;
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteAsync(CapturedFrame frame, CancellationToken cancellationToken)
        {
            Timestamps.Add(frame.Timestamp);
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteAudioAsync(AudioChunk chunk, CancellationToken cancellationToken)
        {
            AudioTimestamps.Add(chunk.Timestamp);
            return ValueTask.CompletedTask;
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            if (FailComplete)
            {
                throw new InvalidOperationException("synthetic finalization failure");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask AbortAsync(CancellationToken cancellationToken)
        {
            AbortCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSurface : IFrameSurface
    {
        public int Width => 100;
        public int Height => 100;
        public void Dispose() { }
    }

    private sealed class CloneableFakeSurface : ICloneableFrameSurface
    {
        public int Width => 100;
        public int Height => 100;
        public IFrameSurface Clone() => new CloneableFakeSurface();
        public void Dispose() { }
    }
}
