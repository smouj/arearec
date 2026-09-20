using System.Diagnostics;
using System.Runtime.InteropServices;
using AreaRec.Media;

namespace AreaRec.App.Smoke;

internal static class Program
{
    private const byte VirtualKeyControl = 0x11;
    private const byte VirtualKeyShift = 0x10;
    private const byte VirtualKeyR = 0x52;
    private const byte VirtualKeyEscape = 0x1B;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint WmSetText = 0x000C;
    private const uint BmClick = 0x00F5;

    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("NOT VERIFIED: native UI smoke requires Windows.");
            return 2;
        }

        var appPath = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AreaRec.App", "bin", "Release", "net8.0-windows10.0.19041.0", "AreaRec.exe"));
        if (!File.Exists(appPath))
        {
            Console.WriteLine($"NOT VERIFIED: AreaRec executable not found at {appPath}");
            return 2;
        }

        var startup = Stopwatch.StartNew();
        using var app = Process.Start(new ProcessStartInfo
        {
            FileName = appPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(appPath),
        });
        if (app is null)
        {
            Console.WriteLine("NOT VERIFIED: AreaRec could not be started.");
            return 2;
        }

        string? recordingPath = null;
        try
        {
            if (!WaitUntil(() =>
            {
                app.Refresh();
                return !app.HasExited && app.MainWindowHandle != IntPtr.Zero;
            }, TimeSpan.FromSeconds(5)))
            {
                Console.WriteLine("NOT VERIFIED: AreaRec did not expose a main window.");
                return 2;
            }

            startup.Stop();
            app.Refresh();
            var idleCpuBefore = app.TotalProcessorTime;
            var idleWindow = Stopwatch.StartNew();
            Thread.Sleep(1_000);
            app.Refresh();
            idleWindow.Stop();
            var idleCpuMilliseconds = (app.TotalProcessorTime - idleCpuBefore).TotalMilliseconds;
            var idleCpuPercent = idleWindow.Elapsed.TotalMilliseconds > 0
                ? idleCpuMilliseconds / idleWindow.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100
                : 0;
            var idleWorkingSet = app.WorkingSet64 / (1024 * 1024);

            var mainWindow = app.MainWindowHandle;
            SetForegroundWindow(mainWindow);
            SendHotkey();
            var selectorOpened = WaitUntil(
                () => !IsWindowVisible(mainWindow) && EnumerateVisibleWindows(app.Id).Any(window => window != mainWindow),
                TimeSpan.FromSeconds(3));
            if (!selectorOpened)
            {
                Console.WriteLine("NOT VERIFIED: Ctrl+Shift+R did not open the region selector overlay.");
                return 3;
            }

            SendKey(VirtualKeyEscape);
            var selectorCancelled = WaitUntil(
                () => IsWindowVisible(mainWindow) && EnumerateVisibleWindows(app.Id).All(window => window == mainWindow),
                TimeSpan.FromSeconds(3));
            if (!selectorCancelled)
            {
                Console.WriteLine("NOT VERIFIED: Escape did not close the region selector overlay.");
                return 4;
            }

            SetForegroundWindow(mainWindow);
            SendHotkey();
            var selectorForDrag = WaitUntil(
                () => !IsWindowVisible(mainWindow) && EnumerateVisibleWindows(app.Id).Any(window => window != mainWindow),
                TimeSpan.FromSeconds(3));
            if (!selectorForDrag)
            {
                Console.WriteLine("NOT VERIFIED: second Ctrl+Shift+R did not open the selector for drag testing.");
                return 6;
            }

            var left = GetSystemMetrics(76);
            var top = GetSystemMetrics(77);
            var right = GetSystemMetrics(78);
            var bottom = GetSystemMetrics(79);
            SendMouseDrag(
                Math.Clamp(left + 40, left, right - 160),
                Math.Clamp(top + 40, top, bottom - 120),
                Math.Clamp(left + 160, left + 2, right - 2),
                Math.Clamp(top + 120, top + 2, bottom - 2));
            var selectionAccepted = WaitUntil(
                () => IsWindowVisible(mainWindow) && IsEnabledChildWithText(mainWindow, "Record"),
                TimeSpan.FromSeconds(3));
            if (!selectionAccepted)
            {
                Console.WriteLine("NOT VERIFIED: drag selection did not enable the Record button.");
                return 7;
            }

            recordingPath = Path.Combine(Path.GetTempPath(), $"arearec-ui-smoke-{Guid.NewGuid():N}.mp4");
            var recordButton = FindChildWithText(mainWindow, "Record");
            if (recordButton == IntPtr.Zero)
            {
                Console.WriteLine("NOT VERIFIED: Record button was not found.");
                return 8;
            }

            SetForegroundWindow(mainWindow);
            PostMessage(recordButton, BmClick, IntPtr.Zero, IntPtr.Zero);
            var saveDialog = WaitForProcessWindow(app.Id, window =>
                GetWindowClass(window) == "#32770" &&
                GetWindowTextValue(window).Contains("Save recording", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(3));
            if (saveDialog == IntPtr.Zero)
            {
                Console.WriteLine("NOT VERIFIED: Save recording dialog did not open.");
                return 9;
            }

            var fileNameEdit = FindChildByClass(saveDialog, "Edit");
            if (fileNameEdit == IntPtr.Zero)
            {
                Console.WriteLine("NOT VERIFIED: Save recording filename field was not found.");
                return 10;
            }

            SetForegroundWindow(saveDialog);
            SendMessage(fileNameEdit, WmSetText, IntPtr.Zero, recordingPath);
            var saveButton = FindChildWithAnyText(saveDialog, "Save", "Guardar");
            if (saveButton == IntPtr.Zero)
            {
                Console.WriteLine($"NOT VERIFIED: Save recording button was not found. dialog={GetWindowTextValue(saveDialog)}");
                DumpChildButtons(saveDialog);
                return 11;
            }

            PostMessage(saveButton, BmClick, IntPtr.Zero, IntPtr.Zero);
            var recordingStarted = WaitUntil(
                () => IsWindowVisible(mainWindow) &&
                    IsEnabledChildWithText(mainWindow, "Stop") &&
                    FindChildTextStartingWith(mainWindow, "Recording"),
                TimeSpan.FromSeconds(5));
            if (!recordingStarted)
            {
                Console.WriteLine("NOT VERIFIED: recording did not enter the Recording state.");
                return 12;
            }

            Thread.Sleep(900);
            SetForegroundWindow(mainWindow);
            SendHotkey();
            var recordingStopped = WaitUntil(
                () => File.Exists(recordingPath) &&
                    new FileInfo(recordingPath).Length > 0 &&
                    IsWindowVisible(mainWindow) &&
                    FindChildTextStartingWith(mainWindow, "Saved"),
                TimeSpan.FromSeconds(30));
            if (!recordingStopped)
            {
                Console.WriteLine($"NOT VERIFIED: hotkey stop did not produce a saved MP4. expected={recordingPath} status={FindChildTextStartingWith(mainWindow, "Saved")} exists={File.Exists(recordingPath)}");
                DumpChildTexts(mainWindow);
                return 13;
            }

            var inspection = Mp4Inspector.Inspect(recordingPath);
            if (!inspection.HasFileTypeBox || !inspection.HasVideoTrack || inspection.Width <= 0 || inspection.Height <= 0 || inspection.Duration <= TimeSpan.Zero)
            {
                Console.WriteLine($"NOT VERIFIED: UI recording metadata ftyp={inspection.HasFileTypeBox} video={inspection.HasVideoTrack} size={inspection.Width}x{inspection.Height} duration={inspection.Duration}");
                return 14;
            }

            var playback = MediaFoundationPlaybackValidator.Validate(recordingPath, inspection.Width, inspection.Height);
            Console.WriteLine(
                $"PASS UI hotkey=Ctrl+Shift+R selector=opened escape=cancelled drag=accepted record=enabled saved=mp4 decoded={playback.DecodedFrames} size={inspection.Width}x{inspection.Height} startupToWindow={startup.Elapsed.TotalMilliseconds:0}ms idleCpu={idleCpuPercent:0.0}% idleWorkingSet={idleWorkingSet}MB");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"NOT VERIFIED: UI smoke failed: {exception.Message}");
            return 5;
        }
        finally
        {
            if (!app.HasExited)
            {
                app.CloseMainWindow();
                if (!app.WaitForExit(1500))
                {
                    app.Kill(entireProcessTree: true);
                }
            }

            if (recordingPath is not null && File.Exists(recordingPath))
            {
                File.Delete(recordingPath);
            }
        }
    }

    private static bool WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (predicate())
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return predicate();
    }

    private static void SendHotkey()
    {
        SendKey(VirtualKeyControl, keyUp: false);
        SendKey(VirtualKeyShift, keyUp: false);
        SendKey(VirtualKeyR, keyUp: false);
        SendKey(VirtualKeyR, keyUp: true);
        SendKey(VirtualKeyShift, keyUp: true);
        SendKey(VirtualKeyControl, keyUp: true);
    }

    private static void SendKey(byte virtualKey, bool keyUp = false)
    {
        keybd_event(virtualKey, 0, keyUp ? KeyEventKeyUp : 0, UIntPtr.Zero);
    }

    private static void SendMouseDrag(int startX, int startY, int endX, int endY)
    {
        SetCursorPos(startX, startY);
        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        SetCursorPos(endX, endY);
        mouse_event(MouseEventMove, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
    }

    private static bool IsEnabledChildWithText(IntPtr parent, string expectedText)
    {
        var found = false;
        EnumChildWindows(parent, (window, _) =>
        {
            var text = new char[128];
            var length = GetWindowText(window, text, text.Length);
            if (IsWindowEnabled(window) && string.Equals(new string(text, 0, length), expectedText, StringComparison.Ordinal))
            {
                found = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool FindChildTextStartingWith(IntPtr parent, string expectedPrefix)
    {
        var found = false;
        EnumChildWindows(parent, (window, _) =>
        {
            if (GetWindowTextValue(window).StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static IntPtr FindChildWithText(IntPtr parent, string expectedText)
    {
        var found = IntPtr.Zero;
        EnumChildWindows(parent, (window, _) =>
        {
            if (string.Equals(GetWindowTextValue(window), expectedText, StringComparison.OrdinalIgnoreCase))
            {
                found = window;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static IntPtr FindChildWithAnyText(IntPtr parent, params string[] expectedTexts)
    {
        var found = IntPtr.Zero;
        EnumChildWindows(parent, (window, _) =>
        {
            var text = NormalizeUiText(GetWindowTextValue(window));
            if (expectedTexts.Any(expected => string.Equals(text, NormalizeUiText(expected), StringComparison.OrdinalIgnoreCase)))
            {
                found = window;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static void DumpChildButtons(IntPtr parent)
    {
        EnumChildWindows(parent, (window, _) =>
        {
            if (string.Equals(GetWindowClass(window), "Button", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"UI child button text={GetWindowTextValue(window)} enabled={IsWindowEnabled(window)}");
            }

            return true;
        }, IntPtr.Zero);
    }

    private static void DumpChildTexts(IntPtr parent)
    {
        EnumChildWindows(parent, (window, _) =>
        {
            var text = GetWindowTextValue(window);
            if (!string.IsNullOrWhiteSpace(text))
            {
                Console.WriteLine($"UI child class={GetWindowClass(window)} text={text} enabled={IsWindowEnabled(window)}");
            }

            return true;
        }, IntPtr.Zero);
    }

    private static string NormalizeUiText(string text) => text.Trim().TrimStart('&').Trim();

    private static IntPtr FindChildByClass(IntPtr parent, string expectedClass)
    {
        var found = IntPtr.Zero;
        EnumChildWindows(parent, (window, _) =>
        {
            if (string.Equals(GetWindowClass(window), expectedClass, StringComparison.OrdinalIgnoreCase))
            {
                found = window;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static IntPtr WaitForProcessWindow(int processId, Func<IntPtr, bool> predicate, TimeSpan timeout)
    {
        IntPtr result = IntPtr.Zero;
        WaitUntil(() =>
        {
            result = EnumerateVisibleWindows(processId).FirstOrDefault(predicate);
            return result != IntPtr.Zero;
        }, timeout);
        return result;
    }

    private static string GetWindowTextValue(IntPtr window)
    {
        var text = new char[256];
        var length = GetWindowText(window, text, text.Length);
        return new string(text, 0, Math.Max(0, length));
    }

    private static string GetWindowClass(IntPtr window)
    {
        var className = new char[128];
        var length = GetClassName(window, className, className.Length);
        return new string(className, 0, Math.Max(0, length));
    }

    private static List<IntPtr> EnumerateVisibleWindows(int processId)
    {
        var windows = new List<IntPtr>();
        EnumWindows((window, _) =>
        {
            var threadId = GetWindowThreadProcessId(window, out var ownerProcessId);
            if (threadId != 0 && ownerProcessId == processId && IsWindowVisible(window))
            {
                windows.Add(window);
            }

            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsCallback callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, [Out] char[] text, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, [Out] char[] className, int capacity);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, string lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInformation);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInformation);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
}
