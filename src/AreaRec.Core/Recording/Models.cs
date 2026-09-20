namespace AreaRec.Core.Recording;

public readonly record struct PhysicalRegion(int X, int Y, int Width, int Height)
{
    public bool IsValid => Width >= 2 && Height >= 2;

    public PhysicalRegion NormalizeForH264()
    {
        var width = Math.Max(2, Width & ~1);
        var height = Math.Max(2, Height & ~1);
        return new PhysicalRegion(X, Y, width, height);
    }

    public PhysicalRegion? Intersection(PhysicalRegion other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(X + Width, other.X + other.Width);
        var bottom = Math.Min(Y + Height, other.Y + other.Height);
        return right > left && bottom > top
            ? new PhysicalRegion(left, top, right - left, bottom - top)
            : null;
    }

    public static PhysicalRegion FromPoints(int x1, int y1, int x2, int y2)
    {
        var left = Math.Min(x1, x2);
        var top = Math.Min(y1, y2);
        return new PhysicalRegion(left, top, Math.Abs(x2 - x1), Math.Abs(y2 - y1))
            .NormalizeForH264();
    }

    public override string ToString() => $"{Width}×{Height} · ({X}, {Y})";
}

public enum VideoQuality
{
    Balanced,
    High,
    VeryHigh,
}

public sealed record CaptureSettings(
    PhysicalRegion Region,
    int FramesPerSecond = 30,
    VideoQuality Quality = VideoQuality.Balanced,
    bool IncludeCursor = true)
{
    public void Validate()
    {
        if (!Region.IsValid)
        {
            throw new ArgumentException("The capture region must be at least 2×2 physical pixels.", nameof(Region));
        }

        if (FramesPerSecond is not (30 or 60))
        {
            throw new ArgumentOutOfRangeException(nameof(FramesPerSecond), "Only 30 and 60 FPS are supported by the initial profile set.");
        }
    }
}

public enum RecordingState
{
    Idle,
    Selecting,
    Starting,
    Recording,
    Stopping,
    Saving,
    Completed,
    Error,
}

public readonly record struct RecordingStatistics(
    long CapturedFrames,
    long EncodedFrames,
    long DroppedFrames,
    long DuplicatedFrames,
    TimeSpan CaptureDuration,
    TimeSpan EncodeDuration)
{
    public double EffectiveFramesPerSecond =>
        CaptureDuration.TotalSeconds > 0 ? EncodedFrames / CaptureDuration.TotalSeconds : 0;
}
