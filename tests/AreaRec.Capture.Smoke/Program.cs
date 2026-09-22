using AreaRec.Capture;
using AreaRec.Core.Recording;
using AreaRec.Graphics;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace AreaRec.Capture.Smoke;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("NOT VERIFIED: native Windows capture requires Windows.");
            return 2;
        }

        var useDxgi = args.Any(argument => string.Equals(argument, "dxgi", StringComparison.OrdinalIgnoreCase));
        var useCursorProbe = args.Any(argument => string.Equals(argument, "cursor", StringComparison.OrdinalIgnoreCase));
        if (useCursorProbe)
        {
            return await RunCursorProbeAsync();
        }

        if (!useDxgi && !WindowsGraphicsCaptureSupport.IsSupported())
        {
            Console.WriteLine("NOT VERIFIED: Windows Graphics Capture is unavailable on this build.");
            return 2;
        }

        await using ICaptureSource source = useDxgi
            ? new DesktopDuplicationCaptureSource()
            : new WindowsGraphicsCaptureSource();
        using var desktopPulse = useDxgi ? new DesktopPulseForm() : null;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var settings = new CaptureSettings(new PhysicalRegion(0, 0, 640, 360), 30, IncludeCursor: false);

        try
        {
            if (desktopPulse is not null)
            {
                desktopPulse.Show();
                Application.DoEvents();
            }

            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var captureTask = Task.Run(async () =>
            {
                await source.StartAsync(settings, cancellation.Token).ConfigureAwait(false);
                started.SetResult(true);
                return await CollectFramesAsync(source, cancellation.Token).ConfigureAwait(false);
            });
            PumpUntilStarted(started.Task, captureTask, desktopPulse);
            if (captureTask.IsCompleted)
            {
                await captureTask.ConfigureAwait(false);
            }

            if (desktopPulse is not null)
            {
                desktopPulse.Pulse();
            }

            PumpUntilCompleted(captureTask, desktopPulse);
            var (frames, width, height) = await captureTask.ConfigureAwait(false);

            if (frames < 3 || width < 2 || height < 2)
            {
                if (useDxgi)
                {
                    Console.WriteLine(
                        $"NOT VERIFIED: DXGI delivered {frames} frame(s) after a controlled desktop pulse; no initial desktop update was observed.");
                    return 2;
                }

                Console.Error.WriteLine($"Capture smoke failed: frames={frames}, size={width}x{height}");
                return 1;
            }

            Console.WriteLine($"PASS {(useDxgi ? "DXGI" : "WGC")} frames={frames} size={width}x{height}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"NOT VERIFIED: {(useDxgi ? "DXGI" : "WGC")} runtime smoke failed (0x{exception.HResult:X8}): {exception}");
            return 2;
        }
    }

    private static async Task<int> RunCursorProbeAsync()
    {
        if (!OperatingSystem.IsWindows() || !WindowsGraphicsCaptureSupport.IsSupported())
        {
            Console.WriteLine("NOT VERIFIED: cursor pixel probe requires Windows Graphics Capture.");
            return 2;
        }

        GetCursorPos(out var originalCursor);
        try
        {
            SetCursorPos(320, 180);
            Thread.Sleep(100);
            var region = new PhysicalRegion(0, 0, 640, 360);
            var withoutCursor = await CaptureBgraAsync(new CaptureSettings(region, IncludeCursor: false));
            var withCursor = await CaptureBgraAsync(new CaptureSettings(region, IncludeCursor: true));
            var differingBytes = withoutCursor.Zip(withCursor).LongCount(pair => pair.First != pair.Second);
            if (differingBytes == 0)
            {
                Console.WriteLine("NOT VERIFIED: WGC cursor-enabled and cursor-disabled readbacks were identical.");
                return 2;
            }

            Console.WriteLine($"PASS cursor pixel probe differingBytes={differingBytes}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"NOT VERIFIED: cursor pixel probe failed (0x{exception.HResult:X8}): {exception}");
            return 2;
        }
        finally
        {
            SetCursorPos(originalCursor.X, originalCursor.Y);
        }
    }

    private static async Task<byte[]> CaptureBgraAsync(CaptureSettings settings)
    {
        await using var source = new WindowsGraphicsCaptureSource();
        await source.StartAsync(settings, CancellationToken.None);
        WindowsGraphicsCaptureSource nativeContext = source;
        await using var processor = new D3D11FrameProcessor(nativeContext.NativeDevice, nativeContext.NativeContext);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await foreach (var frame in source.CaptureAsync(cancellation.Token).ConfigureAwait(false))
        {
            using (frame.Surface)
            using (var processed = await processor.ProcessAsync(frame.Surface, settings.Region, cancellation.Token).ConfigureAwait(false))
            {
                if (processed is not IBgra32FrameSurface bgra)
                {
                    throw new InvalidOperationException("Cursor probe did not produce a BGRA readback.");
                }

                return bgra.Bgra32.ToArray();
            }
        }

        throw new InvalidOperationException("Cursor probe captured no frame.");
    }

    private static void PumpUntilCompleted(Task task, DesktopPulseForm? desktopPulse)
    {
        var nextPulse = Stopwatch.GetTimestamp();
        while (!task.IsCompleted)
        {
            Application.DoEvents();
            if (desktopPulse is not null && Stopwatch.GetTimestamp() >= nextPulse)
            {
                desktopPulse.Pulse();
                nextPulse = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
            }

            Thread.Sleep(10);
        }
    }

    private static void PumpUntilStarted(
        Task started,
        Task captureTask,
        DesktopPulseForm? desktopPulse)
    {
        var nextPulse = Stopwatch.GetTimestamp();
        while (!started.IsCompleted && !captureTask.IsCompleted)
        {
            Application.DoEvents();
            if (desktopPulse is not null && Stopwatch.GetTimestamp() >= nextPulse)
            {
                desktopPulse.Pulse();
                nextPulse = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
            }

            Thread.Sleep(10);
        }
    }

    private static async Task<(int Frames, int Width, int Height)> CollectFramesAsync(
        ICaptureSource source,
        CancellationToken cancellationToken)
    {
        var frames = 0;
        var width = 0;
        var height = 0;
        await foreach (var frame in source.CaptureAsync(cancellationToken).ConfigureAwait(false))
        {
            frames++;
            width = frame.Surface.Width;
            height = frame.Surface.Height;
            frame.Surface.Dispose();
            if (frames >= 3)
            {
                break;
            }
        }

        return (frames, width, height);
    }

    private sealed class DesktopPulseForm : Form
    {
        public DesktopPulseForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(24, 24);
            Size = new Size(120, 80);
            BackColor = Color.Magenta;
            TopMost = true;
        }

        public void Pulse()
        {
            BackColor = BackColor == Color.Magenta ? Color.Lime : Color.Magenta;
            Refresh();
            Application.DoEvents();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private readonly record struct CursorPoint(int X, int Y);
}
