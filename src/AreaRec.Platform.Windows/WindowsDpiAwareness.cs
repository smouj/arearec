using System.Runtime.InteropServices;

namespace AreaRec.Platform.Windows;

public static class WindowsDpiAwareness
{
    private const int ProcessPerMonitorDpiAware = 2;

    public static void TryEnablePerMonitorAwareness()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = SetProcessDpiAwareness(ProcessPerMonitorDpiAware);
        if (result is 0 or -5)
        {
            return;
        }

        _ = SetProcessDpiAware();
    }

    [DllImport("shcore.dll")]
    private static extern int SetProcessDpiAwareness(int value);

    [return: MarshalAs(UnmanagedType.Bool)]
    [System.Runtime.InteropServices.DllImport("user32.dll", ExactSpelling = true, EntryPoint = "SetProcessDPIAware")]
    private static extern bool SetProcessDpiAware();
}
