using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AreaRec.Core.Recording;

namespace AreaRec.Platform.Windows;

/// <summary>
/// Captures the default render endpoint through WASAPI loopback. The source is
/// intentionally independent from the video session until A/V muxing is ready.
/// </summary>
[SupportedOSPlatform("windows")]
public class WasapiLoopbackSource : IAudioSource
{
    private const uint ClsctxAll = 0x17;
    private const uint AudclntStreamflagsLoopback = 0x0002_0000;
    private const uint AudclntStreamflagsEventcallback = 0x0004_0000;
    private const uint AudclntBufferflagsSilent = 0x2;
    private const uint DeviceStateActive = 0x1;
    private const int CoInitMultithreaded = 0x0;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private const int ElementNotFound = unchecked((int)0x80070490);
    private static readonly Guid AudioClientIid = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid AudioCaptureClientIid = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");
    private static readonly Guid MmDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    private readonly DataFlow _dataFlow;
    private readonly uint _streamFlags;
    private readonly string _sourceName;
    private IAudioClient? _audioClient;
    private IAudioCaptureClient? _captureClient;
    private AudioFormat _format;
    private int _blockAlign;
    private bool _disposed;

    public WasapiLoopbackSource()
        : this(DataFlow.Render, AudclntStreamflagsLoopback | AudclntStreamflagsEventcallback, "wasapi-render-loopback")
    {
    }

    protected WasapiLoopbackSource(bool microphone)
        : this(
            microphone ? DataFlow.Capture : DataFlow.Render,
            microphone ? AudclntStreamflagsEventcallback : AudclntStreamflagsLoopback | AudclntStreamflagsEventcallback,
            microphone ? "wasapi-microphone" : "wasapi-render-loopback")
    {
    }

    private WasapiLoopbackSource(DataFlow dataFlow, uint streamFlags, string sourceName)
    {
        _dataFlow = dataFlow;
        _streamFlags = streamFlags;
        _sourceName = sourceName;
    }

    public string SourceName => _sourceName;

    public AudioFormat Format => _format;

    public ValueTask<AudioFormat> InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_format.SampleRate > 0)
        {
            return ValueTask.FromResult(_format);
        }

        var coInitialized = InitializeCom();
        try
        {
            InitializeAudioClient();
            return ValueTask.FromResult(_format);
        }
        finally
        {
            ReleaseCom(ref _captureClient);
            ReleaseCom(ref _audioClient);
            if (coInitialized)
            {
                CoUninitialize();
            }
        }
    }

    public async IAsyncEnumerable<AudioChunk> CaptureAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        await ValueTask.CompletedTask;

        var coInitialized = InitializeCom();
        AutoResetEvent? sampleEvent = null;
        CancellationTokenRegistration cancellationRegistration = default;
        try
        {
            InitializeAudioClient();
            sampleEvent = new AutoResetEvent(false);
            Check(_audioClient!.SetEventHandle(sampleEvent.SafeWaitHandle.DangerousGetHandle()), "IAudioClient.SetEventHandle");
            Check(_audioClient.Start(), "IAudioClient.Start");
            cancellationRegistration = cancellationToken.Register(static state => ((AutoResetEvent)state!).Set(), sampleEvent);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var packetFrames = GetNextPacketSize();
                while (packetFrames > 0)
                {
                    Check(
                        _captureClient!.GetBuffer(
                            out var data,
                            out var frames,
                            out var flags,
                            out _,
                            out var qpcPosition),
                        "IAudioCaptureClient.GetBuffer");
                    AudioChunk chunk;
                    try
                    {
                        var byteCount = checked((int)(frames * (uint)_blockAlign));
                        var bytes = new byte[byteCount];
                        if ((flags & AudclntBufferflagsSilent) == 0 && byteCount > 0)
                        {
                            if (data == 0)
                            {
                                throw new InvalidOperationException("WASAPI returned a null buffer for a non-silent packet.");
                            }

                            Marshal.Copy(data, bytes, 0, byteCount);
                        }

                        chunk = new AudioChunk(ToTimestamp(qpcPosition), bytes);
                    }
                    finally
                    {
                        Check(_captureClient.ReleaseBuffer(frames), "IAudioCaptureClient.ReleaseBuffer");
                    }

                    yield return chunk;

                    packetFrames = GetNextPacketSize();
                }

                var waitResult = WaitHandle.WaitAny([sampleEvent, cancellationToken.WaitHandle], 1000);
                if (waitResult == 1)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }
        finally
        {
            cancellationRegistration.Dispose();
            if (_audioClient is not null)
            {
                _ = _audioClient.Stop();
            }

            sampleEvent?.Dispose();
            ReleaseCom(ref _captureClient);
            ReleaseCom(ref _audioClient);
            if (coInitialized)
            {
                CoUninitialize();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        ReleaseCom(ref _captureClient);
        ReleaseCom(ref _audioClient);
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private void InitializeAudioClient()
    {
        var enumeratorType = Type.GetTypeFromCLSID(MmDeviceEnumeratorClsid, throwOnError: true)
            ?? throw new InvalidOperationException("MMDeviceEnumerator is unavailable.");
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
        try
        {
            var defaultEndpointHr = enumerator.GetDefaultAudioEndpoint(_dataFlow, Role.Console, out var device);
            if (defaultEndpointHr == ElementNotFound && _dataFlow == DataFlow.Capture)
            {
                // A capture endpoint can be active without being assigned as
                // the Windows default. This is common for virtual microphones
                // and avoids rejecting an explicitly requested microphone
                // source solely because the user's default is unset.
                device = GetFirstActiveCaptureEndpoint(enumerator);
            }
            else
            {
                Check(defaultEndpointHr, "IMMDeviceEnumerator.GetDefaultAudioEndpoint");
            }

            try
            {
                var audioClientIid = AudioClientIid;
                Check(
                    device.Activate(ref audioClientIid, ClsctxAll, IntPtr.Zero, out _audioClient),
                    "IMMDevice.Activate(IAudioClient)");
            }
            finally
            {
                ReleaseCom(ref device);
            }
        }
        finally
        {
            ReleaseCom(ref enumerator);
        }

        Check(_audioClient!.GetMixFormat(out var formatPointer), "IAudioClient.GetMixFormat");
        try
        {
            var waveFormat = Marshal.PtrToStructure<WaveFormat>(formatPointer);
            var encoding = waveFormat.FormatTag == 3
                ? AudioSampleEncoding.IeeeFloat
                : AudioSampleEncoding.PcmInteger;
            if (waveFormat.FormatTag == WaveFormatExtensibleTag && waveFormat.ExtraSize >= 22)
            {
                var extensible = Marshal.PtrToStructure<WaveFormatExtensible>(formatPointer);
                encoding = extensible.SubFormat == IeeeFloatSubFormat
                    ? AudioSampleEncoding.IeeeFloat
                    : AudioSampleEncoding.PcmInteger;
                waveFormat = new WaveFormat(
                    extensible.FormatTag,
                    extensible.Channels,
                    extensible.SamplesPerSecond,
                    extensible.AvgBytesPerSecond,
                    extensible.BlockAlign,
                    extensible.BitsPerSample,
                    extensible.ExtraSize);
            }

            if (waveFormat.Channels == 0 || waveFormat.SamplesPerSecond == 0 || waveFormat.BlockAlign == 0)
            {
                throw new InvalidOperationException("WASAPI returned an invalid mix format.");
            }

            _format = new AudioFormat(
                checked((int)waveFormat.SamplesPerSecond),
                waveFormat.Channels,
                waveFormat.BitsPerSample,
                encoding);
            _format.Validate();
            _blockAlign = waveFormat.BlockAlign;
            Check(
                _audioClient.Initialize(
                    ShareMode.Shared,
                    _streamFlags,
                    1_000_000,
                    0,
                    formatPointer,
                Guid.Empty),
                "IAudioClient.Initialize(loopback)");
            var audioCaptureClientIid = AudioCaptureClientIid;
            Check(
                _audioClient.GetService(ref audioCaptureClientIid, out _captureClient),
                "IAudioClient.GetService(IAudioCaptureClient)");
        }
        finally
        {
            Marshal.FreeCoTaskMem(formatPointer);
        }
    }

    private static IMMDevice GetFirstActiveCaptureEndpoint(IMMDeviceEnumerator enumerator)
    {
        Check(
            enumerator.EnumAudioEndpoints(DataFlow.Capture, DeviceStateActive, out var devices),
            "IMMDeviceEnumerator.EnumAudioEndpoints");
        try
        {
            Check(devices.GetCount(out var count), "IMMDeviceCollection.GetCount");
            if (count > 0)
            {
                Check(devices.Item(0, out var device), "IMMDeviceCollection.Item");
                return device;
            }
        }
        finally
        {
            ReleaseCom(ref devices);
        }

        throw new InvalidOperationException("No active WASAPI microphone endpoint is available.");
    }

    private uint GetNextPacketSize()
    {
        Check(_captureClient!.GetNextPacketSize(out var packetFrames), "IAudioCaptureClient.GetNextPacketSize");
        return packetFrames;
    }

    private static TimeSpan ToTimestamp(ulong qpcPosition) => qpcPosition == 0
        ? TimeSpan.FromSeconds(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency)
        : TimeSpan.FromSeconds(qpcPosition / (double)Stopwatch.Frequency);

    private static bool InitializeCom()
    {
        var hresult = CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
        if (hresult < 0 && hresult != RpcEChangedMode)
        {
            Marshal.ThrowExceptionForHR(hresult);
        }

        return hresult >= 0;
    }

    private static void Check(int hresult, string operation)
    {
        if (hresult < 0)
        {
            throw new InvalidOperationException(
                $"{operation} failed with HRESULT 0x{hresult:X8}.",
                Marshal.GetExceptionForHR(hresult));
        }
    }

    private static void ReleaseCom<T>(ref T? value) where T : class
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }

        value = null;
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, int coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    private enum DataFlow
    {
        Render,
        Capture,
        All,
    }

    private enum Role
    {
        Console,
        Multimedia,
        Communications,
    }

    private enum ShareMode
    {
        Shared,
        Exclusive,
    }

    private const ushort WaveFormatExtensibleTag = 0xFFFE;

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private readonly record struct WaveFormat(
        ushort FormatTag,
        ushort Channels,
        uint SamplesPerSecond,
        uint AvgBytesPerSecond,
        ushort BlockAlign,
        ushort BitsPerSample,
        ushort ExtraSize);

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private readonly record struct WaveFormatExtensible(
        ushort FormatTag,
        ushort Channels,
        uint SamplesPerSecond,
        uint AvgBytesPerSecond,
        ushort BlockAlign,
        ushort BitsPerSample,
        ushort ExtraSize,
        ushort ValidBitsPerSample,
        uint ChannelMask,
        Guid SubFormat);

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(DataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(DataFlow dataFlow, Role role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsContext, IntPtr activationParams, out IAudioClient audioClient);
        [PreserveSig] int OpenPropertyStore(uint access, out object properties);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(ShareMode shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, Guid sessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferSize);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(ShareMode shareMode, IntPtr format, out IntPtr closestFormat);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, out IAudioCaptureClient service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint framesRead);
        [PreserveSig] int GetNextPacketSize(out uint framesInNextPacket);
    }
}

/// <summary>
/// Captures the default microphone endpoint through shared-mode WASAPI.
/// Mixing and MP4 muxing remain owned by the future audio pipeline.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiMicrophoneSource : WasapiLoopbackSource
{
    public WasapiMicrophoneSource()
        : base(microphone: true)
    {
    }
}
