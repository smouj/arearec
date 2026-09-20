namespace AreaRec.Graphics;

public interface ID3D11GraphicsDevice : IDisposable
{
    string AdapterName { get; }
    bool SupportsHardwareVideoEncoding { get; }
}

public sealed class GraphicsDeviceUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
