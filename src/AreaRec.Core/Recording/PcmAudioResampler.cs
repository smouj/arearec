using System.Buffers.Binary;

namespace AreaRec.Core.Recording;

/// <summary>
/// Resamples PCM chunks with linear interpolation. It deliberately preserves
/// timestamp origin; an audio/video clock owns timeline alignment.
/// </summary>
public sealed class PcmAudioResampler
{
    public PcmAudioResampler(AudioFormat inputFormat, AudioFormat outputFormat)
    {
        inputFormat.Validate();
        outputFormat.Validate();
        if (inputFormat.Channels != outputFormat.Channels ||
            inputFormat.BitsPerSample != outputFormat.BitsPerSample ||
            inputFormat.Encoding != outputFormat.Encoding)
        {
            throw new ArgumentException("PCM resampling requires matching channels, bit depth and encoding.");
        }

        if (inputFormat.BitsPerSample is not (16 or 32))
        {
            throw new ArgumentOutOfRangeException(
                nameof(inputFormat),
                "The initial PCM resampler supports 16-bit and 32-bit samples.");
        }

        InputFormat = inputFormat;
        OutputFormat = outputFormat;
    }

    public AudioFormat InputFormat { get; }

    public AudioFormat OutputFormat { get; }

    public AudioChunk Resample(AudioChunk input)
    {
        var inputBlockAlign = InputFormat.BlockAlign;
        if (input.Data.Length % inputBlockAlign != 0)
        {
            throw new InvalidDataException("Audio chunk length is not aligned to the input format.");
        }

        var inputFrames = input.Data.Length / inputBlockAlign;
        if (inputFrames == 0)
        {
            return new AudioChunk(input.Timestamp, ReadOnlyMemory<byte>.Empty);
        }

        var outputFrames = Math.Max(
            1,
            checked((int)Math.Round(
                inputFrames * (double)OutputFormat.SampleRate / InputFormat.SampleRate,
                MidpointRounding.AwayFromZero)));
        var output = new byte[checked(outputFrames * OutputFormat.BlockAlign)];
        for (var outputFrame = 0; outputFrame < outputFrames; outputFrame++)
        {
            var sourcePosition = outputFrame * (double)InputFormat.SampleRate / OutputFormat.SampleRate;
            var firstFrame = Math.Min((int)sourcePosition, inputFrames - 1);
            var secondFrame = Math.Min(firstFrame + 1, inputFrames - 1);
            var fraction = (float)(sourcePosition - firstFrame);
            for (var channel = 0; channel < OutputFormat.Channels; channel++)
            {
                var firstOffset = checked(firstFrame * inputBlockAlign + channel * InputFormat.BytesPerSample);
                var secondOffset = checked(secondFrame * inputBlockAlign + channel * InputFormat.BytesPerSample);
                var first = ReadSample(input.Data.Span.Slice(firstOffset, InputFormat.BytesPerSample));
                var second = ReadSample(input.Data.Span.Slice(secondOffset, InputFormat.BytesPerSample));
                var outputOffset = checked(outputFrame * OutputFormat.BlockAlign + channel * OutputFormat.BytesPerSample);
                WriteSample(output.AsSpan(outputOffset, OutputFormat.BytesPerSample), first + (second - first) * fraction);
            }
        }

        return new AudioChunk(input.Timestamp, output);
    }

    private float ReadSample(ReadOnlySpan<byte> bytes) => InputFormat.Encoding switch
    {
        AudioSampleEncoding.IeeeFloat when InputFormat.BitsPerSample == 32
            => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes)),
        AudioSampleEncoding.PcmInteger when InputFormat.BitsPerSample == 16
            => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
        AudioSampleEncoding.PcmInteger when InputFormat.BitsPerSample == 32
            => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648f,
        _ => throw new InvalidOperationException("The input PCM encoding is not supported by the resampler."),
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
                throw new InvalidOperationException("The output PCM encoding is not supported by the resampler.");
        }
    }
}
