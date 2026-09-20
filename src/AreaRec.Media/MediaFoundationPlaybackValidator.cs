using System.Runtime.InteropServices;

namespace AreaRec.Media;

public sealed record MediaFoundationPlaybackResult(
    int DecodedFrames,
    int Width,
    int Height,
    TimeSpan LastTimestamp);

/// <summary>
/// Validates that Media Foundation can open and decode an MP4 produced by the sink.
/// This deliberately uses the Source Reader rather than parsing the container again.
/// </summary>
public static class MediaFoundationPlaybackValidator
{
    private const uint FirstVideoStream = 0xFFFFFFFC;
    private const uint EndOfStream = 0x00000002;
    private const uint Error = 0x00000001;

    public static MediaFoundationPlaybackResult Validate(
        string path,
        int expectedWidth,
        int expectedHeight,
        int minimumDecodedFrames = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (expectedWidth <= 0 || expectedHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedWidth));
        }

        using var runtime = new MediaFoundationRuntime();
        var readerAttributes = CreateReaderAttributes();
        Check(HResults.MFCreateSourceReaderFromURL(path, readerAttributes, out var reader), "MFCreateSourceReaderFromURL");
        Com.Release(readerAttributes);
        try
        {
            var outputType = CreateOutputType(expectedWidth, expectedHeight);
            try
            {
                Check(Com.SetCurrentMediaType(reader, FirstVideoStream, outputType), "IMFSourceReader.SetCurrentMediaType(NV12)");
            }
            finally
            {
                Com.Release(outputType);
            }

            var decodedFrames = 0;
            var sourceSamples = 0;
            uint lastBufferCount = 0;
            uint lastSampleLength = 0;
            uint lastFlags = 0;
            long lastTimestamp = 0;
            while (true)
            {
                Check(
                    Com.ReadSample(reader, FirstVideoStream, out var streamFlags, out lastTimestamp, out var sample),
                    "IMFSourceReader.ReadSample");
                lastFlags = streamFlags;
                try
                {
                    if ((streamFlags & Error) != 0)
                    {
                        throw new MediaFoundationUnavailableException("Media Foundation reported a Source Reader decode error.");
                    }

                    if (sample != 0)
                    {
                        sourceSamples++;
                        Check(Com.GetBufferCount(sample, out var bufferCount), "IMFSample.GetBufferCount");
                        Check(Com.GetTotalLength(sample, out var sampleLength), "IMFSample.GetTotalLength");
                        lastBufferCount = bufferCount;
                        lastSampleLength = sampleLength;
                        if (bufferCount == 0)
                        {
                            continue;
                        }

                        Check(Com.GetBufferByIndex(sample, 0, out var buffer), "IMFSample.GetBufferByIndex");
                        try
                        {
                            Check(Com.GetCurrentLength(buffer, out var length), "IMFMediaBuffer.GetCurrentLength");
                            if (length <= 0)
                            {
                                throw new MediaFoundationUnavailableException("Media Foundation returned an empty decoded video sample.");
                            }

                            decodedFrames++;
                        }
                        finally
                        {
                            Com.Release(buffer);
                        }
                    }

                    if ((streamFlags & EndOfStream) != 0)
                    {
                        break;
                    }
                }
                finally
                {
                    Com.Release(sample);
                }
            }

            if (decodedFrames < minimumDecodedFrames)
            {
                throw new MediaFoundationUnavailableException(
                    $"Media Foundation decoded {decodedFrames} video frame(s); at least {minimumDecodedFrames} required (sourceSamples={sourceSamples}, lastBuffers={lastBufferCount}, lastLength={lastSampleLength}, lastFlags=0x{lastFlags:X}).");
            }

            var inspection = Mp4Inspector.Inspect(path);
            if (inspection.Width != expectedWidth || inspection.Height != expectedHeight)
            {
                throw new MediaFoundationUnavailableException(
                    $"Decoded MP4 dimensions {inspection.Width}x{inspection.Height} do not match {expectedWidth}x{expectedHeight}.");
            }

            return new MediaFoundationPlaybackResult(
                decodedFrames,
                inspection.Width,
                inspection.Height,
                TimeSpan.FromTicks(lastTimestamp));
        }
        finally
        {
            Com.Release(reader);
        }
    }

    public static byte[] ReadFirstDecodedNv12Frame(
        string path,
        int expectedWidth,
        int expectedHeight)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (expectedWidth <= 0 || expectedHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedWidth));
        }

        using var runtime = new MediaFoundationRuntime();
        var readerAttributes = CreateReaderAttributes();
        Check(HResults.MFCreateSourceReaderFromURL(path, readerAttributes, out var reader), "MFCreateSourceReaderFromURL");
        Com.Release(readerAttributes);
        try
        {
            var outputType = CreateOutputType(expectedWidth, expectedHeight);
            try
            {
                Check(Com.SetCurrentMediaType(reader, FirstVideoStream, outputType), "IMFSourceReader.SetCurrentMediaType(NV12)");
            }
            finally
            {
                Com.Release(outputType);
            }

            while (true)
            {
                Check(
                    Com.ReadSample(reader, FirstVideoStream, out var streamFlags, out _, out var sample),
                    "IMFSourceReader.ReadSample");
                try
                {
                    if ((streamFlags & Error) != 0)
                    {
                        throw new MediaFoundationUnavailableException("Media Foundation reported a Source Reader decode error.");
                    }

                    if (sample != 0)
                    {
                        Check(Com.GetBufferCount(sample, out var bufferCount), "IMFSample.GetBufferCount");
                        if (bufferCount == 0)
                        {
                            continue;
                        }

                        Check(Com.GetBufferByIndex(sample, 0, out var buffer), "IMFSample.GetBufferByIndex");
                        try
                        {
                            Check(Com.GetCurrentLength(buffer, out var length), "IMFMediaBuffer.GetCurrentLength");
                            if (length <= 0)
                            {
                                throw new MediaFoundationUnavailableException("Media Foundation returned an empty decoded video sample.");
                            }

                            Check(Com.LockBuffer(buffer, out var data, out _, out _), "IMFMediaBuffer.Lock");
                            try
                            {
                                var bytes = new byte[length];
                                Marshal.Copy(data, bytes, 0, length);
                                return bytes;
                            }
                            finally
                            {
                                Check(Com.UnlockBuffer(buffer), "IMFMediaBuffer.Unlock");
                            }
                        }
                        finally
                        {
                            Com.Release(buffer);
                        }
                    }

                    if ((streamFlags & EndOfStream) != 0)
                    {
                        break;
                    }
                }
                finally
                {
                    Com.Release(sample);
                }
            }
        }
        finally
        {
            Com.Release(reader);
        }

        throw new MediaFoundationUnavailableException("Media Foundation decoded no video frame.");
    }

    private static IntPtr CreateReaderAttributes()
    {
        Check(HResults.MFCreateAttributes(out var attributes, 1), "MFCreateAttributes");
        try
        {
            Check(Com.SetUInt32(attributes, MediaFoundationGuids.EnableVideoProcessing, 1), "Enable Source Reader video processing");
            return attributes;
        }
        catch
        {
            Com.Release(attributes);
            throw;
        }
    }

    private static IntPtr CreateOutputType(int width, int height)
    {
        Check(HResults.MFCreateMediaType(out var type), "MFCreateMediaType");
        try
        {
            Check(Com.SetGuid(type, MediaFoundationGuids.MajorType, MediaFoundationGuids.Video), "Set source reader major type");
            Check(Com.SetGuid(type, MediaFoundationGuids.Subtype, MediaFoundationGuids.Nv12), "Set source reader subtype");
            Check(Com.SetUInt64(type, MediaFoundationGuids.FrameSize, Pack(width, height)), "Set source reader frame size");
            return type;
        }
        catch
        {
            Com.Release(type);
            throw;
        }
    }

    private static void Check(int hresult, string operation)
    {
        if (hresult < 0)
        {
            throw new MediaFoundationUnavailableException(
                $"{operation} failed with HRESULT 0x{hresult:X8}.",
                hresult: hresult);
        }
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    private static class HResults
    {
        [DllImport("mfreadwrite.dll", ExactSpelling = true)]
        public static extern int MFCreateSourceReaderFromURL(
            [MarshalAs(UnmanagedType.LPWStr)] string url,
            IntPtr attributes,
            out IntPtr reader);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateAttributes(out IntPtr attributes, uint initialSize);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateMediaType(out IntPtr mediaType);
    }

    private static class MediaFoundationGuids
    {
        public static readonly Guid Video = new("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid Nv12 = new("3231564E-0000-0010-8000-00AA00389B71");
        public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
        public static readonly Guid EnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    }

    private static class Com
    {
        private const int SetUInt32Index = 21;
        private const int SetUInt64Index = 22;
        private const int SetGuidIndex = 24;
        // IMFSourceReader's order is GetStreamSelection, SetStreamSelection,
        // GetNativeMediaType, GetCurrentMediaType, SetCurrentMediaType,
        // SetCurrentPosition, ReadSample after IUnknown.
        private const int ReadSampleIndex = 9;
        // IMFAttributes contributes 30 methods, so IMFSample's first own
        // method starts at vtable slot 33 after IUnknown.
        private const int GetBufferCountIndex = 39;
        private const int GetBufferByIndexIndex = 40;
        private const int GetTotalLengthIndex = 45;
        private const int LockBufferIndex = 3;
        private const int UnlockBufferIndex = 4;
        private const int GetCurrentLengthIndex = 5;
        public static int SetUInt32(IntPtr self, Guid key, uint value)
        {
            var call = GetDelegate<SetUInt32Delegate>(self, SetUInt32Index);
            return call(self, ref key, value);
        }

        public static int SetGuid(IntPtr self, Guid key, Guid value)
        {
            var call = GetDelegate<SetGuidDelegate>(self, SetGuidIndex);
            return call(self, ref key, ref value);
        }

        public static int SetUInt64(IntPtr self, Guid key, ulong value)
        {
            var call = GetDelegate<SetUInt64Delegate>(self, SetUInt64Index);
            return call(self, ref key, value);
        }

        public static int SetCurrentMediaType(IntPtr self, uint streamIndex, IntPtr mediaType)
        {
            var call = GetDelegate<SetCurrentMediaTypeDelegate>(self, 7);
            return call(self, streamIndex, IntPtr.Zero, mediaType);
        }

        public static int ReadSample(IntPtr self, uint streamIndex, out uint streamFlags, out long timestamp, out IntPtr sample)
        {
            var call = GetDelegate<ReadSampleDelegate>(self, ReadSampleIndex);
            return call(self, streamIndex, 0, out _, out streamFlags, out timestamp, out sample);
        }

        public static int GetBufferCount(IntPtr self, out uint count)
        {
            var call = GetDelegate<GetBufferCountDelegate>(self, GetBufferCountIndex);
            return call(self, out count);
        }

        public static int GetBufferByIndex(IntPtr self, uint index, out IntPtr buffer)
        {
            var call = GetDelegate<GetBufferByIndexDelegate>(self, GetBufferByIndexIndex);
            return call(self, index, out buffer);
        }

        public static int GetTotalLength(IntPtr self, out uint length)
        {
            var call = GetDelegate<GetTotalLengthDelegate>(self, GetTotalLengthIndex);
            return call(self, out length);
        }

        public static int GetCurrentLength(IntPtr self, out int length)
        {
            var call = GetDelegate<GetCurrentLengthDelegate>(self, GetCurrentLengthIndex);
            return call(self, out length);
        }

        public static int LockBuffer(IntPtr self, out IntPtr data, out int maxLength, out int currentLength)
        {
            var call = GetDelegate<LockBufferDelegate>(self, LockBufferIndex);
            return call(self, out data, out maxLength, out currentLength);
        }

        public static int UnlockBuffer(IntPtr self)
        {
            var call = GetDelegate<UnlockBufferDelegate>(self, UnlockBufferIndex);
            return call(self);
        }

        public static void Release(IntPtr self)
        {
            if (self == 0)
            {
                return;
            }

            _ = Marshal.Release(self);
        }

        private static T GetDelegate<T>(IntPtr self, int index)
            where T : Delegate
        {
            var vtable = Marshal.ReadIntPtr(self);
            var method = Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<T>(method);
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetUInt32Delegate(IntPtr self, ref Guid key, uint value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetUInt64Delegate(IntPtr self, ref Guid key, ulong value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetGuidDelegate(IntPtr self, ref Guid key, ref Guid value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetCurrentMediaTypeDelegate(IntPtr self, uint streamIndex, IntPtr reserved, IntPtr mediaType);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReadSampleDelegate(IntPtr self, uint streamIndex, uint controlFlags, out uint actualStreamIndex, out uint streamFlags, out long timestamp, out IntPtr sample);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetBufferCountDelegate(IntPtr self, out uint count);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetBufferByIndexDelegate(IntPtr self, uint index, out IntPtr buffer);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetTotalLengthDelegate(IntPtr self, out uint length);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetCurrentLengthDelegate(IntPtr self, out int length);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int LockBufferDelegate(IntPtr self, out IntPtr data, out int maxLength, out int currentLength);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int UnlockBufferDelegate(IntPtr self);

    }
}
