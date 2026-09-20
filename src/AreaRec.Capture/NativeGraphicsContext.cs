namespace AreaRec.Capture;

public interface INativeGraphicsContext
{
    nint NativeDevice { get; }
    nint NativeContext { get; }
}
