using AreaRec.Platform.Windows;

namespace AreaRec.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        WindowsDpiAwareness.TryEnablePerMonitorAwareness();
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
