using System.Runtime.InteropServices;

namespace AreaRec.Media;

public sealed class MediaFoundationRuntime : IDisposable
{
    private const int MfVersion = 0x0002_0070;
    private const int MfStartupFull = 0;
    private static readonly object Gate = new();
    private static int _users;
    private bool _disposed;

    public MediaFoundationRuntime()
    {
        lock (Gate)
        {
            if (_users == 0)
            {
                var hr = MFStartup(MfVersion, MfStartupFull);
                if (hr < 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }
            }

            _users++;
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (--_users == 0)
            {
                var hr = MFShutdown();
                if (hr < 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }
            }
        }
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(int version, int dwFlags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();
}
