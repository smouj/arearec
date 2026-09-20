using AreaRec.Core.Recording;

namespace AreaRec.Media;

public sealed record MediaFoundationEncoderProfile(
    int FramesPerSecond,
    VideoQuality Quality,
    uint TargetBitrate,
    bool PreferHardware)
{
    public static MediaFoundationEncoderProfile FromSettings(CaptureSettings settings, bool preferHardware = true)
    {
        settings.Validate();
        var bitsPerPixel = settings.Quality switch
        {
            VideoQuality.Balanced => 0.08,
            VideoQuality.High => 0.12,
            VideoQuality.VeryHigh => 0.18,
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.Quality, "Unsupported video quality."),
        };
        var bitrate = (uint)Math.Clamp(
            settings.Region.Width * settings.Region.Height * settings.FramesPerSecond * bitsPerPixel,
            2_000_000,
            50_000_000);
        return new MediaFoundationEncoderProfile(
            settings.FramesPerSecond,
            settings.Quality,
            bitrate,
            preferHardware);
    }
}

public sealed class MediaFoundationUnavailableException : Exception
{
    public MediaFoundationUnavailableException(string message, Exception? innerException = null, int? hresult = null)
        : base(message, innerException)
    {
        if (hresult.HasValue)
        {
            HResult = hresult.Value;
        }
    }
}
