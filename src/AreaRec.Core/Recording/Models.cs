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
        // Keep the edge arithmetic in 64 bits. Virtual-screen coordinates are
        // physical pixels and can be negative; using int addition here can
        // wrap at the edge of the coordinate range and report a false
        // disjoint/overlapping result.
        var left = Math.Max((long)X, other.X);
        var top = Math.Max((long)Y, other.Y);
        var right = Math.Min((long)X + Width, (long)other.X + other.Width);
        var bottom = Math.Min((long)Y + Height, (long)other.Y + other.Height);
        return right > left && bottom > top
            ? new PhysicalRegion(
                checked((int)left),
                checked((int)top),
                checked((int)(right - left)),
                checked((int)(bottom - top)))
            : null;
    }

    public bool Contains(PhysicalRegion other) =>
        (long)other.X >= X &&
        (long)other.Y >= Y &&
        (long)other.X + other.Width <= (long)X + Width &&
        (long)other.Y + other.Height <= (long)Y + Height;

    public static PhysicalRegion FromPoints(int x1, int y1, int x2, int y2)
    {
        var left = Math.Min((long)x1, x2);
        var top = Math.Min((long)y1, y2);
        var width = checked((int)(Math.Max((long)x1, x2) - left));
        var height = checked((int)(Math.Max((long)y1, y2) - top));
        return new PhysicalRegion(checked((int)left), checked((int)top), width, height)
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

        if (!Enum.IsDefined(Quality))
        {
            throw new ArgumentOutOfRangeException(nameof(Quality), Quality, "The selected video quality is not supported.");
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
