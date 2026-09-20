using Windows.Graphics.Capture;

namespace AreaRec.Capture;

public static class WindowsGraphicsCaptureSupport
{
    public static bool IsSupported()
    {
        return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)
            && GraphicsCaptureSession.IsSupported();
    }
}
