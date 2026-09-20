using System.Buffers.Binary;

namespace AreaRec.Core.Recording;

/// <summary>
/// Mixes already-aligned PCM chunks without resampling. A future audio clock
/// is responsible for aligning sources before they reach this boundary.
/// </summary>
public sealed class PcmAudioMixer : IAudioMixer
{
    public PcmAudioMixer(AudioFormat outputFormat)
    {
        outputFormat.Validate();
        if (outputFormat.BitsPerSample is not (16 or 32))
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputFormat),
                "The initial PCM mixer supports 16-bit and 32-bit samples.");
        }

        OutputFormat = outputFormat;
    }

    public AudioFormat OutputFormat { get; }

    public ValueTask<AudioChunk> MixAsync(
        IReadOnlyList<AudioChunk> inputs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        cancellationToken.ThrowIfCancellationRequested();
        if (inputs.Count == 0)
        {
            return ValueTask.FromResult(new AudioChunk(TimeSpan.Zero, ReadOnlyMemory<byte>.Empty));
        }

        var blockAlign = OutputFormat.BlockAlign;
        var maxFrames = 0;
        var firstTimestamp = inputs[0].Timestamp;
        foreach (var input in inputs)
        {
            if (input.Data.Length % blockAlign != 0)
            {
                throw new InvalidDataException("Audio chunk length is not aligned to the output format.");
            }

            maxFrames = Math.Max(maxFrames, input.Data.Length / blockAlign);
            firstTimestamp = input.Timestamp < firstTimestamp ? input.Timestamp : firstTimestamp;
        }

        var output = new byte[checked(maxFrames * blockAlign)];
        for (var frame = 0; frame < maxFrames; frame++)
        {
            for (var channel = 0; channel < OutputFormat.Channels; channel++)
            {
                var mixed = 0f;
                foreach (var input in inputs)
                {
                    var inputFrames = input.Data.Length / blockAlign;
                    if (frame >= inputFrames)
                    {
                        continue;
                    }

                    var offset = checked(frame * blockAlign + channel * OutputFormat.BytesPerSample);
                    mixed += ReadSample(input.Data.Span.Slice(offset, OutputFormat.BytesPerSample));
                }

                var outputOffset = checked(frame * blockAlign + channel * OutputFormat.BytesPerSample);
                WriteSample(output.AsSpan(outputOffset, OutputFormat.BytesPerSample), mixed);
            }
        }

        return ValueTask.FromResult(new AudioChunk(firstTimestamp, output));
    }

    private float ReadSample(ReadOnlySpan<byte> bytes) => OutputFormat.Encoding switch
    {
        AudioSampleEncoding.IeeeFloat when OutputFormat.BitsPerSample == 32
            => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes)),
        AudioSampleEncoding.PcmInteger when OutputFormat.BitsPerSample == 16
            => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
        AudioSampleEncoding.PcmInteger when OutputFormat.BitsPerSample == 32
            => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648f,
        _ => throw new InvalidOperationException("The audio sample encoding is not supported by the PCM mixer."),
    };

    private void WriteSample(Span<byte> bytes, float value)
    {
        value = Math.Clamp(value, -1f, 1f);
        switch (OutputFormat.Encoding, OutputFormat.BitsPerSample)
        {
            case (AudioSampleEncoding.IeeeFloat, 32):
                BinaryPrimitives.WriteInt32LittleEndian(bytes, BitConverter.SingleToInt32Bits(value));
                break;
            case (AudioSampleEncoding.PcmInteger, 16):
                var sample16 = value <= -1f
                    ? short.MinValue
                    : (short)MathF.Round(value * short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(bytes, sample16);
                break;
            case (AudioSampleEncoding.PcmInteger, 32):
                var sample32 = value <= -1f
                    ? int.MinValue
                    : (int)MathF.Round(value * int.MaxValue);
                BinaryPrimitives.WriteInt32LittleEndian(bytes, sample32);
                break;
            default:
                throw new InvalidOperationException("The audio sample encoding is not supported by the PCM mixer.");
        }
    }
}
