using System.Diagnostics;
using System.Runtime.InteropServices;
using AreaRec.Capture;
using AreaRec.Core.Recording;
using AreaRec.Graphics;
using AreaRec.Media;
using AreaRec.Platform.Windows;

namespace AreaRec.EndToEnd.Smoke;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("cursor-pixels", StringComparer.OrdinalIgnoreCase))
        {
            return await RunCursorPixelProbeAsync();
        }

        if (args.Contains("audio", StringComparer.OrdinalIgnoreCase))
        {
            return await RunAudioEndToEndAsync();
        }

        var useDxgi = args.Contains("dxgi", StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows() || (!useDxgi && !WindowsGraphicsCaptureSupport.IsSupported()))
        {
            Console.WriteLine("NOT VERIFIED: requested Windows capture backend is unavailable.");
            return 2;
        }

        var fps = args.Contains("60", StringComparer.Ordinal) ? 60 : 30;
        var includeCursor = args.Contains("cursor", StringComparer.Ordinal);
        var preferHardware = args.Contains("hardware", StringComparer.OrdinalIgnoreCase);
        var validatePlayback = args.Contains("playback", StringComparer.OrdinalIgnoreCase);
        var settings = new CaptureSettings(
            new PhysicalRegion(0, 0, 640, 360),
            FramesPerSecond: fps,
            Quality: VideoQuality.Balanced,
            IncludeCursor: includeCursor);
        var output = Path.Combine(Path.GetTempPath(), $"arearec-e2e-{Guid.NewGuid():N}.mp4");
        await using ICaptureSource source = useDxgi
            ? new DesktopDuplicationCaptureSource()
            : CaptureSourceFactory.Create(settings.Region);
        var sink = new MediaFoundationMp4Sink(output, preferHardware);
        await using var session = new RecordingSession(
            source,
            sink,
            processorFactory: capture =>
            {
                var context = (INativeGraphicsContext)capture;
                return new D3D11FrameProcessor(context.NativeDevice, context.NativeContext);
            });

        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var wallClock = Stopwatch.StartNew();
            await session.StartAsync(settings, cancellation.Token);
            var backend = source.BackendName;
            await Task.Delay(TimeSpan.FromMilliseconds(700), CancellationToken.None);
            await session.StopAsync(CancellationToken.None);
            wallClock.Stop();
            process.Refresh();
            var cpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            var cpuPercent = wallClock.Elapsed.TotalMilliseconds > 0
                ? cpuMilliseconds / wallClock.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100
                : 0;
            var statistics = session.Statistics;
            var selectedTransform = sink.SelectedTransform;
            var frames = (int)statistics.EncodedFrames;
            var duration = statistics.CaptureDuration;
            var outputWidth = settings.Region.Width;
            var outputHeight = settings.Region.Height;
            var bytes = File.Exists(output) ? new FileInfo(output).Length : 0;
            var inspection = bytes > 0 ? Mp4Inspector.Inspect(output) : null;
            var playback = validatePlayback && bytes > 0 && inspection is not null
                ? MediaFoundationPlaybackValidator.Validate(output, settings.Region.Width, settings.Region.Height)
                : null;
            if (frames < 3 || outputWidth != settings.Region.Width || outputHeight != settings.Region.Height || bytes == 0 || inspection is null || (validatePlayback && (playback is null || playback.DecodedFrames < 1)) || !inspection.HasFileTypeBox || !inspection.HasVideoTrack || inspection.Width != settings.Region.Width || inspection.Height != settings.Region.Height || inspection.Duration <= TimeSpan.Zero)
            {
                Console.WriteLine($"NOT VERIFIED: E2E output frames={frames} size={outputWidth}x{outputHeight} duration={duration.TotalMilliseconds:0}ms bytes={bytes}");
                return 1;
            }

            var playbackText = validatePlayback ? $" decoded={playback!.DecodedFrames}" : " playback=NOT VERIFIED";
            var transformText = selectedTransform is null
                ? " transform=NOT VERIFIED hardware=NOT VERIFIED"
                : $" transform={selectedTransform.FriendlyName ?? "unknown"} hardware={selectedTransform.IsHardware}";
            Console.WriteLine($"PASS E2E backend={backend} hardwareRequested={preferHardware} fps={fps} frames={frames}{playbackText}{transformText} duplicated={statistics.DuplicatedFrames} dropped={statistics.DroppedFrames} size={inspection.Width}x{inspection.Height} capture={duration.TotalMilliseconds:0}ms mp4={inspection.Duration.TotalMilliseconds:0}ms bytes={bytes} wall={wallClock.Elapsed.TotalMilliseconds:0}ms cpu={cpuPercent:0.0}% workingSet={process.WorkingSet64 / (1024 * 1024)}MB");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"NOT VERIFIED: E2E capture-to-MP4 failed (0x{exception.HResult:X8}): {exception}");
            return 3;
        }
        finally
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    private static async Task<int> RunAudioEndToEndAsync()
    {
        if (!OperatingSystem.IsWindows() || !WindowsGraphicsCaptureSupport.IsSupported())
        {
            Console.WriteLine("NOT VERIFIED: audio E2E requires Windows Graphics Capture.");
            return 2;
        }

        var settings = new CaptureSettings(
            new PhysicalRegion(0, 0, 640, 360),
            FramesPerSecond: 30,
            Quality: VideoQuality.Balanced,
            IncludeCursor: true);
        var output = Path.Combine(Path.GetTempPath(), $"arearec-audio-e2e-{Guid.NewGuid():N}.mp4");
        var source = CaptureSourceFactory.Create(settings.Region);
        var audio = new WasapiLoopbackSource();
        var sink = new MediaFoundationMp4Sink(output, preferHardware: true);
        await using var session = new RecordingSession(
            source,
            sink,
            processorFactory: capture =>
            {
                var context = (INativeGraphicsContext)capture;
                return new D3D11FrameProcessor(context.NativeDevice, context.NativeContext);
            },
            audioSource: audio);

        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await session.StartAsync(settings, cancellation.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(700), CancellationToken.None);
            await session.StopAsync(CancellationToken.None);

            var inspection = Mp4Inspector.Inspect(output);
            var playback = MediaFoundationPlaybackValidator.Validate(
                output,
                settings.Region.Width,
                settings.Region.Height);
            if (!File.Exists(output) || !inspection.HasVideoTrack || !inspection.HasAudioTrack || playback.DecodedFrames < 1)
            {
                Console.WriteLine(
                    $"NOT VERIFIED: audio E2E video={inspection.HasVideoTrack} audio={inspection.HasAudioTrack} decoded={playback.DecodedFrames}");
                return 1;
            }

            Console.WriteLine(
                $"PASS audio E2E backend={source.BackendName} frames={session.Statistics.EncodedFrames} decoded={playback.DecodedFrames} audio={inspection.HasAudioTrack} duration={inspection.Duration.TotalMilliseconds:0}ms bytes={new FileInfo(output).Length}");
            return 0;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or MediaFoundationUnavailableException)
        {
            Console.WriteLine($"NOT VERIFIED: audio E2E unavailable: {exception.Message}");
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

    private static async Task<int> RunCursorPixelProbeAsync()
    {
        if (!OperatingSystem.IsWindows() || !WindowsGraphicsCaptureSupport.IsSupported())
        {
            Console.WriteLine("NOT VERIFIED: saved cursor pixel probe requires Windows Graphics Capture.");
            return 2;
        }

        var outputs = new List<string>();
        GetCursorPos(out var originalCursor);
        try
        {
            var region = new PhysicalRegion(0, 0, 640, 360);
            SetCursorPos(320, 180);
            outputs.Add(await RecordCursorVariantAsync(region, includeCursor: false));
            SetCursorPos(320, 180);
            outputs.Add(await RecordCursorVariantAsync(region, includeCursor: true));

            var withoutCursor = MediaFoundationPlaybackValidator.ReadFirstDecodedNv12Frame(
                outputs[0], region.Width, region.Height);
            var withCursor = MediaFoundationPlaybackValidator.ReadFirstDecodedNv12Frame(
                outputs[1], region.Width, region.Height);
            var differingBytes = CountDifferences(withoutCursor, withCursor);
            if (differingBytes == 0)
            {
                Console.WriteLine("NOT VERIFIED: decoded cursor-off and cursor-on MP4 frames were identical.");
                return 2;
            }

            Console.WriteLine($"PASS saved cursor pixel probe differingNv12Bytes={differingBytes}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"NOT VERIFIED: saved cursor pixel probe failed (0x{exception.HResult:X8}): {exception}");
            return 2;
        }
        finally
        {
            SetCursorPos(originalCursor.X, originalCursor.Y);
            foreach (var output in outputs)
            {
                if (File.Exists(output))
                {
                    File.Delete(output);
                }
            }
        }
    }

    private static async Task<string> RecordCursorVariantAsync(PhysicalRegion region, bool includeCursor)
    {
        var settings = new CaptureSettings(
            region,
            FramesPerSecond: 30,
            Quality: VideoQuality.Balanced,
            IncludeCursor: includeCursor);
        var output = Path.Combine(Path.GetTempPath(), $"arearec-cursor-pixels-{Guid.NewGuid():N}.mp4");
        await using ICaptureSource source = CaptureSourceFactory.Create(region);
        var sink = new MediaFoundationMp4Sink(output, preferHardware: true);
        await using var session = new RecordingSession(
            source,
            sink,
            processorFactory: capture =>
            {
                var context = (INativeGraphicsContext)capture;
                return new D3D11FrameProcessor(context.NativeDevice, context.NativeContext);
            });

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await session.StartAsync(settings, cancellation.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(700), CancellationToken.None);
        await session.StopAsync(CancellationToken.None);
        if (!File.Exists(output) || new FileInfo(output).Length == 0)
        {
            throw new InvalidOperationException($"Cursor variant did not produce an MP4: {includeCursor}.");
        }

        return output;
    }

    private static long CountDifferences(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var differing = Math.Abs(first.Length - second.Length);
        var sharedLength = Math.Min(first.Length, second.Length);
        for (var index = 0; index < sharedLength; index++)
        {
            if (first[index] != second[index])
            {
                differing++;
            }
        }

        return differing;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CursorPoint(int X, int Y);
}
