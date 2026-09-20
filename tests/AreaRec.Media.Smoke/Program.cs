using System.Diagnostics;
using System.Runtime.InteropServices;
using AreaRec.Core.Recording;
using AreaRec.Media;
using AreaRec.Platform.Windows;

namespace AreaRec.Media.Smoke;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("abort", StringComparer.OrdinalIgnoreCase))
        {
            return await RunAbortSmokeAsync();
        }

        if (args.Contains("performance", StringComparer.OrdinalIgnoreCase))
        {
            return await RunSyntheticPerformanceAsync();
        }

        if (args.Contains("audio-mp4", StringComparer.OrdinalIgnoreCase))
        {
            return await RunAudioMp4SmokeAsync();
        }

        if (args.Contains("audio", StringComparer.OrdinalIgnoreCase) || args.Contains("microphone", StringComparer.OrdinalIgnoreCase))
        {
            return await RunAudioSmokeAsync(args.Contains("microphone", StringComparer.OrdinalIgnoreCase));
        }

        var validatePlayback = args.Contains("playback", StringComparer.OrdinalIgnoreCase);
        var capabilities = MediaFoundationCapabilities.Probe();
        if (!capabilities.Available)
        {
            Console.WriteLine($"NOT VERIFIED: Media Foundation unavailable: {capabilities.Error}");
            return 2;
        }

        var profile = MediaFoundationEncoderProfile.FromSettings(
            new CaptureSettings(new PhysicalRegion(0, 0, 1920, 1080), 60, VideoQuality.High));
        Console.WriteLine($"PASS MF startup profile={profile.Quality} fps={profile.FramesPerSecond} bitrate={profile.TargetBitrate} hardwareMft={capabilities.HardwareEncodingAvailable} name={capabilities.HardwareEncoderName ?? "NOT VERIFIED"}");

        var settings = new CaptureSettings(new PhysicalRegion(0, 0, 320, 240), 30, VideoQuality.Balanced);
        var output = Path.Combine(Path.GetTempPath(), $"arearec-mf-smoke-{Guid.NewGuid():N}.mp4");
        try
        {
            var sink = new MediaFoundationMp4Sink(output, preferHardware: true);
            await sink.OpenAsync(settings, CancellationToken.None);
            for (var index = 0; index < 30; index++)
            {
                using var surface = new SyntheticSurface(settings.Region.Width, settings.Region.Height, index);
                await sink.WriteAsync(new CapturedFrame(index, TimeSpan.FromMilliseconds(index * 33), surface), CancellationToken.None);
            }

            await sink.CompleteAsync(CancellationToken.None);
            await sink.DisposeAsync();
            var selectedTransform = sink.SelectedTransform;
            if (!File.Exists(output) || new FileInfo(output).Length == 0)
            {
                Console.WriteLine("NOT VERIFIED: Media Foundation produced no MP4 output.");
                return 3;
            }

            var inspection = Mp4Inspector.Inspect(output);
            if (!inspection.HasFileTypeBox || !inspection.HasVideoTrack || inspection.Width != settings.Region.Width || inspection.Height != settings.Region.Height || inspection.Duration <= TimeSpan.Zero)
            {
                Console.WriteLine($"NOT VERIFIED: MP4 metadata ftyp={inspection.HasFileTypeBox} video={inspection.HasVideoTrack} size={inspection.Width}x{inspection.Height} duration={inspection.Duration}");
                return 5;
            }

            if (validatePlayback)
            {
                var playback = MediaFoundationPlaybackValidator.Validate(
                    output,
                    settings.Region.Width,
                    settings.Region.Height);
                Console.WriteLine($"PASS MF MP4 bytes={new FileInfo(output).Length} size={inspection.Width}x{inspection.Height} duration={inspection.Duration.TotalMilliseconds:0}ms decoded={playback.DecodedFrames} transform={selectedTransform?.FriendlyName ?? "NOT VERIFIED"} hardware={selectedTransform?.IsHardware.ToString() ?? "NOT VERIFIED"}");
            }
            else
            {
                Console.WriteLine($"PASS MF MP4 container bytes={new FileInfo(output).Length} size={inspection.Width}x{inspection.Height} duration={inspection.Duration.TotalMilliseconds:0}ms playback=NOT VERIFIED (run with 'playback') transform={selectedTransform?.FriendlyName ?? "NOT VERIFIED"} hardware={selectedTransform?.IsHardware.ToString() ?? "NOT VERIFIED"}");
            }
        }
        catch (Exception exception) when (exception is COMException or MediaFoundationUnavailableException or InvalidOperationException)
        {
            Console.WriteLine($"NOT VERIFIED: MF MP4 sink unavailable: {exception}");
            return 4;
        }
        finally
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }

        return 0;
    }

    private static async Task<int> RunAbortSmokeAsync()
    {
        var settings = new CaptureSettings(new PhysicalRegion(0, 0, 320, 240), 30, VideoQuality.Balanced);
        var output = Path.Combine(Path.GetTempPath(), $"arearec-mf-abort-{Guid.NewGuid():N}.mp4");
        var temporaryPrefix = $".{Path.GetFileNameWithoutExtension(output)}.";
        try
        {
            var sink = new MediaFoundationMp4Sink(output, preferHardware: true);
            await sink.OpenAsync(settings, CancellationToken.None);
            await sink.AbortAsync(CancellationToken.None);
            await sink.DisposeAsync();

            var leftovers = Directory.EnumerateFiles(
                    Path.GetDirectoryName(output)!,
                    $"{temporaryPrefix}*.mp4")
                .ToArray();
            if (File.Exists(output) || leftovers.Length != 0)
            {
                Console.WriteLine(
                    $"NOT VERIFIED: abort cleanup final={File.Exists(output)} temporaryFiles={leftovers.Length}");
                return 7;
            }

            Console.WriteLine("PASS MF abort cleanup final=absent temporaryFiles=0");
            return 0;
        }
        catch (Exception exception) when (exception is COMException or MediaFoundationUnavailableException or InvalidOperationException)
        {
            Console.WriteLine($"NOT VERIFIED: MF abort cleanup unavailable: {exception}");
            return 2;
        }
        finally
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    private static async Task<int> RunSyntheticPerformanceAsync()
    {
        var capabilities = MediaFoundationCapabilities.Probe();
        if (!capabilities.Available)
        {
            Console.WriteLine($"NOT VERIFIED: Media Foundation unavailable: {capabilities.Error}");
            return 2;
        }

        var cases = new[]
        {
            (Width: 640, Height: 360, Frames: 10),
            (Width: 1_920, Height: 1_080, Frames: 6),
            (Width: 2_560, Height: 1_440, Frames: 4),
            (Width: 3_840, Height: 2_160, Frames: 2),
        };

        try
        {
            foreach (var testCase in cases)
            {
                var settings = new CaptureSettings(
                    new PhysicalRegion(0, 0, testCase.Width, testCase.Height),
                    30,
                    VideoQuality.Balanced);
                var output = Path.Combine(Path.GetTempPath(), $"arearec-mf-perf-{Guid.NewGuid():N}.mp4");
                try
                {
                    using var process = Process.GetCurrentProcess();
                    process.Refresh();
                    var cpuBefore = process.TotalProcessorTime;
                    var wall = Stopwatch.StartNew();
                    await using var sink = new MediaFoundationMp4Sink(output, preferHardware: true);
                    await sink.OpenAsync(settings, CancellationToken.None);
                    for (var index = 0; index < testCase.Frames; index++)
                    {
                        using var surface = new SyntheticSurface(testCase.Width, testCase.Height, index);
                        await sink.WriteAsync(
                            new CapturedFrame(index, TimeSpan.FromSeconds(index / 30d), surface),
                            CancellationToken.None);
                    }

                    await sink.CompleteAsync(CancellationToken.None);
                    wall.Stop();
                    process.Refresh();
                    var cpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
                    var effectiveFps = testCase.Frames / wall.Elapsed.TotalSeconds;
                    var cpuPercent = wall.Elapsed.TotalMilliseconds > 0
                        ? cpuMilliseconds / wall.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100
                        : 0;
                    Console.WriteLine(
                        $"PASS synthetic-perf size={testCase.Width}x{testCase.Height} frames={testCase.Frames} wall={wall.Elapsed.TotalMilliseconds:0}ms effectiveFps={effectiveFps:0.0} cpu={cpuPercent:0.0}% workingSet={process.WorkingSet64 / (1024 * 1024)}MB hardware={sink.SelectedTransform?.IsHardware.ToString() ?? "NOT VERIFIED"}");
                }
                finally
                {
                    if (File.Exists(output))
                    {
                        File.Delete(output);
                    }
                }
            }

            return 0;
        }
        catch (Exception exception) when (exception is COMException or MediaFoundationUnavailableException or InvalidOperationException)
        {
            Console.WriteLine($"NOT VERIFIED: synthetic performance unavailable: {exception}");
            return 2;
        }
    }

    private static async Task<int> RunAudioMp4SmokeAsync()
    {
        var settings = new CaptureSettings(new PhysicalRegion(0, 0, 320, 240), 30, VideoQuality.Balanced);
        var audioFormat = new AudioFormat(48_000, 2, 32, AudioSampleEncoding.IeeeFloat);
        var output = Path.Combine(Path.GetTempPath(), $"arearec-mf-audio-{Guid.NewGuid():N}.mp4");
        try
        {
            var sink = new MediaFoundationMp4Sink(output, preferHardware: true);
            await sink.OpenWithAudioAsync(settings, audioFormat, CancellationToken.None);
            const int audioFramesPerVideoFrame = 1_600;
            for (var index = 0; index < 30; index++)
            {
                var timestamp = TimeSpan.FromSeconds(index / 30d);
                using var surface = new SyntheticSurface(settings.Region.Width, settings.Region.Height, index);
                await sink.WriteAsync(new CapturedFrame(index, timestamp, surface), CancellationToken.None);
                await sink.WriteAudioAsync(
                    new AudioChunk(timestamp, CreateTone(audioFramesPerVideoFrame, audioFormat, index * audioFramesPerVideoFrame)),
                    CancellationToken.None);
            }

            await sink.CompleteAsync(CancellationToken.None);
            await sink.DisposeAsync();
            var inspection = Mp4Inspector.Inspect(output);
            var playback = MediaFoundationPlaybackValidator.Validate(
                output,
                settings.Region.Width,
                settings.Region.Height);
            if (!inspection.HasFileTypeBox || !inspection.HasVideoTrack || !inspection.HasAudioTrack || playback.DecodedFrames < 1)
            {
                Console.WriteLine(
                    $"NOT VERIFIED: A/V MP4 ftyp={inspection.HasFileTypeBox} video={inspection.HasVideoTrack} audio={inspection.HasAudioTrack} decoded={playback.DecodedFrames}");
                return 6;
            }

            Console.WriteLine(
                $"PASS MF A/V MP4 bytes={new FileInfo(output).Length} video={inspection.Width}x{inspection.Height} audio={inspection.HasAudioTrack} duration={inspection.Duration.TotalMilliseconds:0}ms decoded={playback.DecodedFrames}");
            return 0;
        }
        catch (Exception exception) when (exception is COMException or MediaFoundationUnavailableException or InvalidOperationException)
        {
            Console.WriteLine($"NOT VERIFIED: MF A/V sink unavailable: {exception}");
            return 4;
        }
        finally
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    private static async Task<int> RunAudioSmokeAsync(bool microphone)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("NOT VERIFIED: WASAPI loopback requires Windows.");
            return 2;
        }

        await using IAudioSource source = microphone
            ? new WasapiMicrophoneSource()
            : new WasapiLoopbackSource();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var initializedFormat = await source.InitializeAsync(cancellation.Token);
            var chunks = 0;
            var totalBytes = 0L;
            await foreach (var chunk in source.CaptureAsync(cancellation.Token).ConfigureAwait(false))
            {
                chunks++;
                totalBytes += chunk.Data.Length;
                if (chunks >= 3)
                {
                    break;
                }
            }

            initializedFormat.Validate();
            source.Format.Validate();
            if (chunks < 3 || totalBytes == 0)
            {
                Console.WriteLine($"NOT VERIFIED: WASAPI returned chunks={chunks} bytes={totalBytes}.");
                return 2;
            }

            Console.WriteLine(
                $"PASS WASAPI source={source.SourceName} format={source.Format.SampleRate}Hz/{source.Format.Channels}ch/{source.Format.BitsPerSample}bit chunks={chunks} bytes={totalBytes}");
            return 0;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or PlatformNotSupportedException)
        {
            Console.WriteLine($"NOT VERIFIED: WASAPI {(microphone ? "microphone" : "loopback")} endpoint unavailable: {exception.Message}");
            return 2;
        }
    }

    private sealed class SyntheticSurface : IBgra32FrameSurface
    {
        private readonly byte[] _pixels;

        public SyntheticSurface(int width, int height, int frame)
        {
            Width = width;
            Height = height;
            Stride = width * 4;
            _pixels = new byte[Stride * height];
            for (var index = 0; index < _pixels.Length; index += 4)
            {
                _pixels[index] = (byte)(frame * 40);
                _pixels[index + 1] = 80;
                _pixels[index + 2] = 160;
                _pixels[index + 3] = 255;
            }
        }

        public int Width { get; }
        public int Height { get; }
        public int Stride { get; }
        public ReadOnlyMemory<byte> Bgra32 => _pixels;
        public void Dispose() { }
    }

    private static byte[] CreateTone(int frameCount, AudioFormat format, int frameOffset)
    {
        var bytes = new byte[checked(frameCount * format.BlockAlign)];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var value = MathF.Sin(2 * MathF.PI * 440 * (frameOffset + frame) / format.SampleRate) * 0.15f;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var offset = checked((frame * format.Channels + channel) * sizeof(float));
                BitConverter.TryWriteBytes(bytes.AsSpan(offset, sizeof(float)), value);
            }
        }

        return bytes;
    }
}
