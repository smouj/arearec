namespace AreaRec.Core.Recording;

public enum AudioSampleEncoding
{
    PcmInteger,
    IeeeFloat,
}

public readonly record struct AudioFormat(
    int SampleRate,
    int Channels,
    int BitsPerSample,
    AudioSampleEncoding Encoding = AudioSampleEncoding.PcmInteger)
{
    public void Validate()
    {
        if (SampleRate <= 0 || Channels <= 0 || BitsPerSample <= 0 || BitsPerSample % 8 != 0 || !Enum.IsDefined(Encoding))
        {
            throw new ArgumentOutOfRangeException(nameof(AudioFormat), "Audio format dimensions must be positive.");
        }
    }

    public int BytesPerSample => checked(BitsPerSample / 8);

    public int BlockAlign => checked(Channels * BytesPerSample);
}

public readonly record struct AudioChunk(TimeSpan Timestamp, ReadOnlyMemory<byte> Data);

public interface IAudioClock
{
    TimeSpan Now { get; }
}

public interface IAudioMixer
{
    AudioFormat OutputFormat { get; }

    ValueTask<AudioChunk> MixAsync(
        IReadOnlyList<AudioChunk> inputs,
        CancellationToken cancellationToken);
}
