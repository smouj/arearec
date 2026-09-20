using System.Buffers;
using System.Runtime.InteropServices;
using AreaRec.Core.Recording;

namespace AreaRec.Media;

/// <summary>
/// Media Foundation Sink Writer bridge for contiguous CPU-readable BGRA frames.
/// WGC GPU surfaces require the D3D11 readback/convert stage before reaching this sink.
/// </summary>
public sealed class MediaFoundationMp4Sink : IRecordingSink, IAudioRecordingSink
{
    private readonly string _outputPath;
    private readonly bool _preferHardware;
    private AtomicMediaFile? _file;
    private IMFSinkWriter? _writer;
    private MediaFoundationRuntime? _runtime;
    private uint _streamIndex;
    private uint _audioStreamIndex;
    private AudioFormat _audioFormat;
    private bool _audioEnabled;
    private long _sampleDuration;
    private bool _started;
    private bool _completed;
    private readonly object _writerGate = new();

    public MediaFoundationTransformInfo? SelectedTransform { get; private set; }

    public MediaFoundationMp4Sink(string outputPath, bool preferHardware = true)
    {
        _outputPath = outputPath ?? throw new ArgumentNullException(nameof(outputPath));
        _preferHardware = preferHardware;
    }

    public async ValueTask OpenAsync(CaptureSettings settings, CancellationToken cancellationToken)
    {
        await OpenCoreAsync(settings, null, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask OpenWithAudioAsync(
        CaptureSettings settings,
        AudioFormat audioFormat,
        CancellationToken cancellationToken)
    {
        await OpenCoreAsync(settings, audioFormat, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask OpenCoreAsync(
        CaptureSettings settings,
        AudioFormat? audioFormat,
        CancellationToken cancellationToken)
    {
        settings.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        audioFormat?.Validate();
        if (_started || _completed)
        {
            throw new InvalidOperationException("The media sink is single-use and has already been opened or completed.");
        }

        _runtime = new MediaFoundationRuntime();
        _file = AtomicMediaFile.Create(_outputPath);
        try
        {
            var profile = MediaFoundationEncoderProfile.FromSettings(settings, _preferHardware);
            _sampleDuration = 10_000_000L / settings.FramesPerSecond;
            var temporaryPath = _file.TemporaryPath;
            _file.CloseForExternalWriter();
            nint writerAttributes = 0;
            IMFAttributes? writerAttributesObject = null;
            try
            {
                if (profile.PreferHardware)
                {
                    Check(HResults.MFCreateAttributes(out writerAttributes, 1), "MFCreateAttributes");
                    writerAttributesObject = (IMFAttributes)Marshal.GetObjectForIUnknown(writerAttributes);
                    Check(
                        writerAttributesObject.SetUINT32(MediaFoundationGuids.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1),
                        "IMFAttributes.SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS)");
                }

                Check(HResults.MFCreateSinkWriterFromURL(temporaryPath, IntPtr.Zero, writerAttributes, out var writer), "MFCreateSinkWriterFromURL");
                _writer = writer;
            }
            finally
            {
                Release(writerAttributesObject);
                if (writerAttributes != 0)
                {
                    Marshal.Release(writerAttributes);
                }
            }

            var outputType = CreateVideoType(
                MediaFoundationGuids.MFVideoFormatH264,
                settings.Region.Width,
                settings.Region.Height,
                settings.FramesPerSecond,
                profile.TargetBitrate);
            Check(_writer.AddStream(outputType, out _streamIndex), "IMFSinkWriter.AddStream");
            Release(outputType);

            var inputType = CreateVideoType(
                MediaFoundationGuids.MFVideoFormatNV12,
                settings.Region.Width,
                settings.Region.Height,
                settings.FramesPerSecond,
                0);
            Check(_writer.SetInputMediaType(_streamIndex, inputType, IntPtr.Zero), "IMFSinkWriter.SetInputMediaType");
            Release(inputType);

            if (audioFormat is { } format)
            {
                _audioFormat = format;
                var audioOutputType = CreateAudioType(
                    MediaFoundationGuids.MFAudioFormatAAC,
                    format.SampleRate,
                    format.Channels,
                    16,
                    1,
                    12_000,
                    isInput: false);
                Check(_writer.AddStream(audioOutputType, out _audioStreamIndex), "IMFSinkWriter.AddStream(audio)");
                Release(audioOutputType);

                var audioInputType = CreateAudioType(
                    MediaFoundationGuids.MFAudioFormatPCM,
                    format.SampleRate,
                    format.Channels,
                    16,
                    checked(format.SampleRate * 2 * format.Channels),
                    checked(format.SampleRate * 2 * format.Channels),
                    isInput: true);
                Check(_writer.SetInputMediaType(_audioStreamIndex, audioInputType, IntPtr.Zero), "IMFSinkWriter.SetInputMediaType(audio)");
                Release(audioInputType);
                _audioEnabled = true;
            }

            Check(_writer.BeginWriting(), "IMFSinkWriter.BeginWriting");
            SelectedTransform = MediaFoundationTransformProbe.TryGet(_writer, _streamIndex);
            _started = true;
        }
        catch
        {
            await AbortAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask WriteAsync(CapturedFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_started || _writer is null)
        {
            throw new InvalidOperationException("The media sink has not been opened.");
        }

        if (frame.Surface is not IBgra32FrameSurface surface)
        {
            throw new MediaFoundationUnavailableException(
                "The frame surface is GPU-native. A D3D11 readback/convert stage is required before MP4 encoding.");
        }

        var bytes = surface.Bgra32;
        var expectedStride = checked(surface.Width * 4);
        if (surface.Stride < expectedStride || bytes.Length < checked(surface.Stride * surface.Height))
        {
            throw new InvalidDataException("The BGRA surface stride or buffer length is invalid.");
        }

        if ((surface.Width & 1) != 0 || (surface.Height & 1) != 0)
        {
            throw new InvalidDataException("Media Foundation NV12 input requires even frame dimensions.");
        }

        var nv12Length = checked(surface.Width * surface.Height * 3 / 2);
        var nv12 = ArrayPool<byte>.Shared.Rent(nv12Length);
        IMFMediaBuffer? buffer = null;
        try
        {
            ConvertBgraToNv12(bytes.Span, surface.Stride, surface.Width, surface.Height, nv12.AsSpan(0, nv12Length));
            Check(HResults.MFCreateMemoryBuffer(nv12Length, out buffer), "MFCreateMemoryBuffer");
            Check(buffer!.Lock(out var destination, out _, out _), "IMFMediaBuffer.Lock");
            try
            {
                Marshal.Copy(nv12, 0, destination, nv12Length);
            }
            finally
            {
                Check(buffer.Unlock(), "IMFMediaBuffer.Unlock");
            }

            Check(buffer.SetCurrentLength(nv12Length), "IMFMediaBuffer.SetCurrentLength");
            Check(HResults.MFCreateSample(out var sample), "MFCreateSample");
            try
            {
                Check(sample.AddBuffer(buffer), "IMFSample.AddBuffer");
                Check(sample.SetSampleTime(To100Nanoseconds(frame.Timestamp)), "IMFSample.SetSampleTime");
                Check(sample.SetSampleDuration(_sampleDuration), "IMFSample.SetSampleDuration");
                lock (_writerGate)
                {
                    Check(_writer.WriteSample(_streamIndex, sample), "IMFSinkWriter.WriteSample");
                }
            }
            finally
            {
                Release(sample);
            }
        }
        finally
        {
            Release(buffer);
            ArrayPool<byte>.Shared.Return(nv12);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAudioAsync(AudioChunk chunk, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_started || _writer is null || !_audioEnabled)
        {
            throw new InvalidOperationException("The media sink has not been opened with an audio track.");
        }

        var sourceBlockAlign = _audioFormat.BlockAlign;
        if (sourceBlockAlign <= 0 || chunk.Data.Length % sourceBlockAlign != 0)
        {
            throw new InvalidDataException("The audio chunk is not aligned to its source format.");
        }

        var frameCount = chunk.Data.Length / sourceBlockAlign;
        if (frameCount == 0)
        {
            return ValueTask.CompletedTask;
        }

        var outputBytes = checked(frameCount * _audioFormat.Channels * sizeof(short));
        var pcm16 = ArrayPool<byte>.Shared.Rent(outputBytes);
        IMFMediaBuffer? buffer = null;
        try
        {
            ConvertAudioToPcm16(chunk.Data.Span, _audioFormat, pcm16.AsSpan(0, outputBytes));
            Check(HResults.MFCreateMemoryBuffer(outputBytes, out buffer), "MFCreateMemoryBuffer(audio)");
            Check(buffer!.Lock(out var destination, out _, out _), "IMFMediaBuffer.Lock(audio)");
            try
            {
                Marshal.Copy(pcm16, 0, destination, outputBytes);
            }
            finally
            {
                Check(buffer.Unlock(), "IMFMediaBuffer.Unlock(audio)");
            }

            Check(buffer.SetCurrentLength(outputBytes), "IMFMediaBuffer.SetCurrentLength(audio)");
            Check(HResults.MFCreateSample(out var sample), "MFCreateSample(audio)");
            try
            {
                Check(sample.AddBuffer(buffer), "IMFSample.AddBuffer(audio)");
                Check(sample.SetSampleTime(To100Nanoseconds(chunk.Timestamp)), "IMFSample.SetSampleTime(audio)");
                Check(
                    sample.SetSampleDuration(checked(frameCount * TimeSpan.TicksPerSecond / _audioFormat.SampleRate)),
                    "IMFSample.SetSampleDuration(audio)");
                lock (_writerGate)
                {
                    Check(_writer.WriteSample(_audioStreamIndex, sample), "IMFSinkWriter.WriteSample(audio)");
                }
            }
            finally
            {
                Release(sample);
            }
        }
        finally
        {
            Release(buffer);
            ArrayPool<byte>.Shared.Return(pcm16);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        if (!_started || _writer is null || _file is null || _completed)
        {
            return;
        }

        Check(_writer.FinalizeWriter(), "IMFSinkWriter.Finalize");
        Release(_writer);
        _writer = null;
        await _file.CommitAsync(cancellationToken).ConfigureAwait(false);
        _completed = true;
        await DisposeRuntimeAsync().ConfigureAwait(false);
    }

    public async ValueTask AbortAsync(CancellationToken cancellationToken)
    {
        if (_writer is not null)
        {
            Release(_writer);
            _writer = null;
        }

        if (_file is not null)
        {
            await _file.DisposeAsync().ConfigureAwait(false);
            _file = null;
        }

        await DisposeRuntimeAsync().ConfigureAwait(false);
        _started = false;
        _audioEnabled = false;
        _audioFormat = default;
        SelectedTransform = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            await AbortAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async ValueTask DisposeRuntimeAsync()
    {
        _runtime?.Dispose();
        _runtime = null;
        await ValueTask.CompletedTask;
    }

    private static IMFMediaType CreateVideoType(Guid subtype, int width, int height, int fps, uint bitrate)
    {
        Check(HResults.MFCreateMediaType(out var type), "MFCreateMediaType");
        try
        {
            Check(type.SetGUID(MediaFoundationGuids.MF_MT_MAJOR_TYPE, MediaFoundationGuids.MFMediaTypeVideo), "Set major type");
            Check(type.SetGUID(MediaFoundationGuids.MF_MT_SUBTYPE, subtype), "Set subtype");
            Check(type.SetUINT64(MediaFoundationGuids.MF_MT_FRAME_SIZE, Pack(width, height)), "Set frame size");
            Check(type.SetUINT64(MediaFoundationGuids.MF_MT_FRAME_RATE, Pack(fps, 1)), "Set frame rate");
            Check(type.SetUINT64(MediaFoundationGuids.MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1)), "Set pixel aspect ratio");
            Check(type.SetUINT32(MediaFoundationGuids.MF_MT_INTERLACE_MODE, 2), "Set interlace mode");
            if (subtype == MediaFoundationGuids.MFVideoFormatRGB32 || subtype == MediaFoundationGuids.MFVideoFormatNV12)
            {
                var sampleSize = subtype == MediaFoundationGuids.MFVideoFormatNV12
                    ? checked(width * height * 3 / 2)
                    : checked(width * height * 4);
                var stride = subtype == MediaFoundationGuids.MFVideoFormatNV12 ? width : checked(width * 4);
                Check(type.SetUINT32(MediaFoundationGuids.MF_MT_DEFAULT_STRIDE, checked((uint)stride)), "Set default stride");
                Check(type.SetUINT32(MediaFoundationGuids.MF_MT_SAMPLE_SIZE, checked((uint)sampleSize)), "Set sample size");
                Check(type.SetUINT32(MediaFoundationGuids.MF_MT_FIXED_SIZE_SAMPLES, 1), "Set fixed sample size");
                Check(type.SetUINT32(MediaFoundationGuids.MF_MT_ALL_SAMPLES_INDEPENDENT, 1), "Set independent samples");
            }
            if (bitrate != 0)
            {
                Check(type.SetUINT32(MediaFoundationGuids.MF_MT_AVG_BITRATE, bitrate), "Set bitrate");
            }

            return type;
        }
        catch
        {
            Release(type);
            throw;
        }
    }

    private static IMFMediaType CreateAudioType(
        Guid subtype,
        int sampleRate,
        int channels,
        int bitsPerSample,
        int blockAlign,
        int averageBytesPerSecond,
        bool isInput)
    {
        Check(HResults.MFCreateMediaType(out var type), "MFCreateMediaType(audio)");
        try
        {
            Check(type.SetGUID(MediaFoundationGuids.MF_MT_MAJOR_TYPE, MediaFoundationGuids.MFMediaTypeAudio), "Set audio major type");
            Check(type.SetGUID(MediaFoundationGuids.MF_MT_SUBTYPE, subtype), "Set audio subtype");
            Check(type.SetUINT32(MediaFoundationGuids.MF_MT_AUDIO_NUM_CHANNELS, checked((uint)channels)), "Set audio channels");
            Check(type.SetUINT32(MediaFoundationGuids.MF_MT_AUDIO_SAMPLES_PER_SECOND, checked((uint)sampleRate)), "Set audio sample rate");
            Check(type.SetUINT32(MediaFoundationGuids.MF_MT_AUDIO_BITS_PER_SAMPLE, checked((uint)bitsPerSample)), "Set audio bits per sample");
            Check(type.SetUINT32(MediaFoundationGuids.MF_MT_AUDIO_BLOCK_ALIGNMENT, checked((uint)blockAlign)), "Set audio block alignment");
            Check(type.SetUINT32(MediaFoundationGuids.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, checked((uint)averageBytesPerSecond)), "Set audio byte rate");
            if (isInput)
            {
                Check(type.SetUINT32(MediaFoundationGuids.MF_MT_ALL_SAMPLES_INDEPENDENT, 1), "Set audio independent samples");
                Check(type.SetUINT32(MediaFoundationGuids.MF_MT_FIXED_SIZE_SAMPLES, 1), "Set audio fixed samples");
            }
            else if (subtype == MediaFoundationGuids.MFAudioFormatAAC)
            {
                Check(type.SetUINT32(MediaFoundationGuids.MF_MT_AAC_PAYLOAD_TYPE, 0), "Set AAC payload type");
                Check(type.SetUINT32(MediaFoundationGuids.MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29), "Set AAC profile");
                Check(type.SetUINT32(MediaFoundationGuids.MF_MT_AVG_BITRATE, 96_000), "Set AAC bitrate");
            }

            return type;
        }
        catch
        {
            Release(type);
            throw;
        }
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    private static void ConvertBgraToNv12(
        ReadOnlySpan<byte> bgra,
        int sourceStride,
        int width,
        int height,
        Span<byte> nv12)
    {
        var yPlaneLength = checked(width * height);
        var yPlane = nv12[..yPlaneLength];
        var uvPlane = nv12[yPlaneLength..];

        for (var y = 0; y < height; y++)
        {
            var sourceRow = bgra.Slice(y * sourceStride, width * 4);
            var destinationRow = yPlane.Slice(y * width, width);
            for (var x = 0; x < width; x++)
            {
                var source = x * 4;
                destinationRow[x] = ToLuma(sourceRow[source + 2], sourceRow[source + 1], sourceRow[source]);
            }
        }

        for (var y = 0; y < height; y += 2)
        {
            var destinationRow = uvPlane.Slice((y / 2) * width, width);
            for (var x = 0; x < width; x += 2)
            {
                var red = 0;
                var green = 0;
                var blue = 0;
                for (var sampleY = 0; sampleY < 2; sampleY++)
                {
                    var sourceRow = bgra.Slice((y + sampleY) * sourceStride, width * 4);
                    for (var sampleX = 0; sampleX < 2; sampleX++)
                    {
                        var source = (x + sampleX) * 4;
                        blue += sourceRow[source];
                        green += sourceRow[source + 1];
                        red += sourceRow[source + 2];
                    }
                }

                destinationRow[x] = ToChromaU(red / 4, green / 4, blue / 4);
                destinationRow[x + 1] = ToChromaV(red / 4, green / 4, blue / 4);
            }
        }
    }

    private static byte ToLuma(byte red, byte green, byte blue) => ClampByte(
        ((66 * red) + (129 * green) + (25 * blue) + 128 >> 8) + 16);

    private static byte ToChromaU(int red, int green, int blue) => ClampByte(
        ((-38 * red) - (74 * green) + (112 * blue) + 128 >> 8) + 128);

    private static byte ToChromaV(int red, int green, int blue) => ClampByte(
        ((112 * red) - (94 * green) - (18 * blue) + 128 >> 8) + 128);

    private static byte ClampByte(int value) => (byte)Math.Clamp(value, 0, 255);

    private static void ConvertAudioToPcm16(
        ReadOnlySpan<byte> source,
        AudioFormat format,
        Span<byte> destination)
    {
        var sourceSampleBytes = format.BytesPerSample;
        var sampleCount = checked(source.Length / sourceSampleBytes);
        if (destination.Length < checked(sampleCount * sizeof(short)))
        {
            throw new ArgumentException("The destination audio buffer is too small.", nameof(destination));
        }

        for (var index = 0; index < sampleCount; index++)
        {
            var sourceOffset = checked(index * sourceSampleBytes);
            var value = format.Encoding switch
            {
                AudioSampleEncoding.IeeeFloat when format.BitsPerSample == 32 => BitConverter.Int32BitsToSingle(
                    BitConverter.ToInt32(source.Slice(sourceOffset, sizeof(float)))),
                AudioSampleEncoding.PcmInteger when format.BitsPerSample == 16 =>
                    BitConverter.ToInt16(source.Slice(sourceOffset, sizeof(short))) / 32768f,
                AudioSampleEncoding.PcmInteger when format.BitsPerSample == 32 =>
                    BitConverter.ToInt32(source.Slice(sourceOffset, sizeof(int))) / 2147483648f,
                AudioSampleEncoding.PcmInteger when format.BitsPerSample == 24 =>
                    ReadPcm24(source.Slice(sourceOffset, 3)) / 8_388_608f,
                _ => throw new NotSupportedException(
                    $"Unsupported audio conversion format {format.Encoding}/{format.BitsPerSample}.")
            };
            var pcm = (short)Math.Clamp(MathF.Round(value * short.MaxValue), short.MinValue, short.MaxValue);
            BitConverter.TryWriteBytes(destination.Slice(index * sizeof(short), sizeof(short)), pcm);
        }
    }

    private static int ReadPcm24(ReadOnlySpan<byte> bytes)
    {
        var value = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
        return (value & 0x0080_0000) != 0 ? value | unchecked((int)0xFF00_0000) : value;
    }

    private static long To100Nanoseconds(TimeSpan timestamp) => timestamp.Ticks;

    private static void Check(int hresult, string operation)
    {
        if (hresult < 0)
        {
            throw new MediaFoundationUnavailableException(
                $"{operation} failed with HRESULT 0x{hresult:X8}.",
                hresult: hresult);
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private static class HResults
    {
        [DllImport("mfreadwrite.dll", ExactSpelling = true)]
        public static extern int MFCreateSinkWriterFromURL(
            [MarshalAs(UnmanagedType.LPWStr)] string url,
            IntPtr byteStream,
            IntPtr attributes,
            out IMFSinkWriter writer);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateAttributes(out IntPtr attributes, uint initialSize);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateMediaType(out IMFMediaType mediaType);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateSample(out IMFSample sample);
    }

    private static class MediaFoundationGuids
    {
        public static readonly Guid MFMediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFMediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormatRGB32 = new("00000016-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormatNV12 = new("3231564E-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFAudioFormatAAC = new("00001610-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFAudioFormatPCM = new("00000001-0000-0010-8000-00AA00389B71");
        public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
        public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        public static readonly Guid MF_MT_SAMPLE_SIZE = new("dad3ab78-1990-408b-bce2-eba673dacc10");
        public static readonly Guid MF_MT_FIXED_SIZE_SAMPLES = new("b8ebefaf-b718-4e04-b0a9-116775e3321b");
        public static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");
        public static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        public static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        public static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        public static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");
        public static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        public static readonly Guid MF_MT_AAC_PAYLOAD_TYPE = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
        public static readonly Guid MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION = new("7632f0e6-9538-4d61-acda-ea29c8c14456");
        public static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("A634A91C-822B-41B9-A494-4DE4643612B0");
    }

    [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSinkWriter
    {
        int AddStream(IMFMediaType targetMediaType, out uint streamIndex);
        int SetInputMediaType(uint streamIndex, IMFMediaType inputMediaType, IntPtr encodingParameters);
        int BeginWriting();
        int WriteSample(uint streamIndex, IMFSample sample);
        int SendStreamTick(uint streamIndex, long timestamp);
        int PlaceMarker(uint streamIndex, IntPtr context);
        int NotifyEndOfSegment(uint streamIndex);
        int Flush(uint streamIndex);
        int FinalizeWriter();
        int GetServiceForStream(uint streamIndex, ref Guid guidService, ref Guid riid, out IntPtr service);
        int GetStatistics(uint streamIndex, IntPtr statistics);
    }

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        int GetItem(ref Guid key, IntPtr value); int GetItemType(ref Guid key, out uint type); int CompareItem(ref Guid key, IntPtr value, out bool result); int Compare(IntPtr attributes, uint matchType, out bool result); int GetUINT32(ref Guid key, out uint value); int GetUINT64(ref Guid key, out ulong value); int GetDouble(ref Guid key, out double value); int GetGUID(ref Guid key, out Guid value); int GetStringLength(ref Guid key, out uint length); int GetString(ref Guid key, IntPtr value, uint size, out uint length); int GetAllocatedString(ref Guid key, out IntPtr value, out uint length); int GetBlobSize(ref Guid key, out uint size); int GetBlob(ref Guid key, IntPtr buffer, uint size, out uint copied); int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size); int GetUnknown(ref Guid key, ref Guid riid, out IntPtr value); int SetItem(ref Guid key, IntPtr value); int DeleteItem(ref Guid key); int DeleteAllItems(); int SetUINT32(ref Guid key, uint value); int SetUINT64(ref Guid key, ulong value); int SetDouble(ref Guid key, double value); int SetGUID(ref Guid key, Guid value); int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value); int SetBlob(ref Guid key, IntPtr buffer, uint size); int SetUnknown(ref Guid key, IntPtr value); int LockStore(); int UnlockStore(); int GetCount(out uint count); int GetItemByIndex(uint index, IntPtr key, IntPtr value); int CopyAllItems(IntPtr attributes);
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType : IMFAttributes
    {
        int GetMajorType(out Guid majorType);
        int IsCompressedFormat(out bool compressed);
        int IsEqual(IMFMediaType other, out uint flags);
        int GetRepresentation(Guid representation, out IntPtr value);
        int FreeRepresentation(Guid representation, IntPtr value);
    }

    [ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        int Lock(out IntPtr buffer, out int maxLength, out int currentLength); int Unlock(); int GetCurrentLength(out int length); int SetCurrentLength(int length); int GetMaxLength(out int maxLength);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample
    {
        int GetItem(ref Guid key, IntPtr value); int GetItemType(ref Guid key, out uint type); int CompareItem(ref Guid key, IntPtr value, out bool result); int Compare(IntPtr attributes, uint matchType, out bool result); int GetUINT32(ref Guid key, out uint value); int GetUINT64(ref Guid key, out ulong value); int GetDouble(ref Guid key, out double value); int GetGUID(ref Guid key, out Guid value); int GetStringLength(ref Guid key, out uint length); int GetString(ref Guid key, IntPtr value, uint size, out uint length); int GetAllocatedString(ref Guid key, out IntPtr value, out uint length); int GetBlobSize(ref Guid key, out uint size); int GetBlob(ref Guid key, IntPtr buffer, uint size, out uint copied); int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size); int GetUnknown(ref Guid key, ref Guid riid, out IntPtr value); int SetItem(ref Guid key, IntPtr value); int DeleteItem(ref Guid key); int DeleteAllItems(); int SetUINT32(ref Guid key, uint value); int SetUINT64(ref Guid key, ulong value); int SetDouble(ref Guid key, double value); int SetGUID(ref Guid key, Guid value); int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value); int SetBlob(ref Guid key, IntPtr buffer, uint size); int SetUnknown(ref Guid key, IntPtr value); int LockStore(); int UnlockStore(); int GetCount(out uint count); int GetItemByIndex(uint index, IntPtr key, IntPtr value); int CopyAllItems(IntPtr attributes);
        int GetSampleFlags(out uint flags); int SetSampleFlags(uint flags); int GetSampleTime(out long time); int SetSampleTime(long time); int GetSampleDuration(out long duration); int SetSampleDuration(long duration); int GetBufferCount(out uint count); int GetBufferByIndex(uint index, out IMFMediaBuffer buffer); int ConvertToContiguousBuffer(out IMFMediaBuffer buffer); int AddBuffer(IMFMediaBuffer buffer); int RemoveBufferByIndex(uint index); int RemoveAllBuffers(); int GetTotalLength(out int length); int CopyToBuffer(IMFMediaBuffer buffer);
    }
}
