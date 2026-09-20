using System.Buffers;
using System.Runtime.InteropServices;
using AreaRec.Core.Recording;

namespace AreaRec.Graphics;

/// <summary>
/// Copies the requested region from a WGC D3D11 texture into a CPU-readable
/// BGRA surface for the current Media Foundation sink. The copy is bounded to
/// the selected rectangle; no full-monitor bitmap is created.
/// </summary>
public sealed class D3D11FrameProcessor : IFrameProcessor
{
    private readonly nint _device;
    private readonly nint _context;
    private readonly Dictionary<(nint Device, nint Context), D3D11FrameProcessor> _componentProcessors = [];
    private nint _stagingTexture;
    private uint _stagingWidth;
    private uint _stagingHeight;
    private uint _stagingFormat;
    private bool _disposed;

    public D3D11FrameProcessor(nint device, nint context)
    {
        if (device == 0 || context == 0)
        {
            throw new ArgumentException("A live D3D11 device and context are required.");
        }

        _device = device;
        _context = context;
    }

    public ValueTask<IFrameSurface> ProcessAsync(
        IFrameSurface source,
        PhysicalRegion region,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (source is ICompositeNativeFrameSurface compositeSurface)
        {
            return ProcessCompositeAsync(compositeSurface, region, cancellationToken);
        }

        if (source is not INativeTextureFrameSurface nativeSurface)
        {
            throw new GraphicsDeviceUnavailableException(
                "The capture surface does not expose a D3D11 texture.");
        }

        var outputRegion = region.NormalizeForH264();
        var sourceRegion = nativeSurface.SourceRegion;
        var offsetX = outputRegion.X - sourceRegion.X;
        var offsetY = outputRegion.Y - sourceRegion.Y;
        if (offsetX < 0 || offsetY < 0 ||
            offsetX + outputRegion.Width > sourceRegion.Width ||
            offsetY + outputRegion.Height > sourceRegion.Height)
        {
            throw new GraphicsDeviceUnavailableException(
                "The selected region crosses the captured monitor. A multi-monitor compositor is required for this selection.");
        }

        var texture = nativeSurface.NativeTexture;
        if (texture == 0)
        {
            throw new GraphicsDeviceUnavailableException("The captured D3D11 texture is unavailable.");
        }

        var sourceDescription = ReadTextureDescription(texture);
        if (sourceDescription.Format != DxgiFormatBgra8)
        {
            throw new GraphicsDeviceUnavailableException(
                $"Unsupported WGC texture format {sourceDescription.Format}; expected BGRA8.");
        }

        var stagingDescription = sourceDescription with
        {
            Width = checked((uint)outputRegion.Width),
            Height = checked((uint)outputRegion.Height),
            MipLevels = 1,
            ArraySize = 1,
            Usage = D3D11UsageStaging,
            BindFlags = 0,
            CPUAccessFlags = D3D11CpuAccessRead,
            MiscFlags = 0,
        };

        var stagingTexture = EnsureStagingTexture(stagingDescription);
        var sourceBox = new D3D11Box(
            checked((uint)offsetX),
            checked((uint)offsetY),
            0,
            checked((uint)(offsetX + outputRegion.Width)),
            checked((uint)(offsetY + outputRegion.Height)),
            1);
        CopySubresourceRegion(
            stagingTexture,
            0,
            0,
            0,
            0,
            texture,
            0,
            ref sourceBox);

        var mapHr = Map(stagingTexture, 0, D3D11MapRead, 0, out var mapped);
        Marshal.ThrowExceptionForHR(mapHr);
        try
        {
            var outputStride = checked(outputRegion.Width * 4);
            if (mapped.RowPitch < outputStride)
            {
                throw new GraphicsDeviceUnavailableException("The mapped D3D11 row pitch is smaller than the requested BGRA row.");
            }

            var pixelLength = checked(outputStride * outputRegion.Height);
            var pixels = ArrayPool<byte>.Shared.Rent(pixelLength);
            try
            {
                for (var row = 0; row < outputRegion.Height; row++)
                {
                    Marshal.Copy(
                        IntPtr.Add(mapped.Data, checked((int)(row * mapped.RowPitch))),
                        pixels,
                        checked(row * outputStride),
                        outputStride);
                }

                IFrameSurface result = new Bgra32FrameSurface(
                    outputRegion.Width,
                    outputRegion.Height,
                    outputStride,
                    pixelLength,
                    pixels);
                return ValueTask.FromResult(result);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(pixels);
                throw;
            }
        }
        finally
        {
            Unmap(stagingTexture, 0);
        }
    }

    private async ValueTask<IFrameSurface> ProcessCompositeAsync(
        ICompositeNativeFrameSurface source,
        PhysicalRegion region,
        CancellationToken cancellationToken)
    {
        var outputRegion = region.NormalizeForH264();
        var outputStride = checked(outputRegion.Width * 4);
        var pixelLength = checked(outputStride * outputRegion.Height);
        var pixels = ArrayPool<byte>.Shared.Rent(pixelLength);
        Array.Clear(pixels, 0, pixelLength);

        try
        {
            foreach (var component in source.Components)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var intersection = outputRegion.Intersection(component.SourceRegion);
                if (!intersection.HasValue)
                {
                    continue;
                }

                var componentRegion = intersection.Value;
                if (componentRegion.Width < 2 || componentRegion.Height < 2)
                {
                    continue;
                }

                componentRegion = componentRegion.NormalizeForH264();

                var componentProcessor = GetComponentProcessor(component.NativeDevice, component.NativeContext);
                using var processed = await componentProcessor.ProcessAsync(
                    component,
                    componentRegion,
                    cancellationToken).ConfigureAwait(false);
                if (processed is not IBgra32FrameSurface bgra)
                {
                    throw new GraphicsDeviceUnavailableException(
                        "The monitor compositor did not produce a BGRA surface.");
                }

                var copyWidth = checked(componentRegion.Width * 4);
                var destinationX = checked(componentRegion.X - outputRegion.X) * 4;
                var destinationY = componentRegion.Y - outputRegion.Y;
                for (var row = 0; row < componentRegion.Height; row++)
                {
                    bgra.Bgra32
                        .Slice(checked(row * bgra.Stride), copyWidth)
                        .CopyTo(pixels.AsMemory(
                            checked((destinationY + row) * outputStride + destinationX),
                            copyWidth));
                }
            }

            return new Bgra32FrameSurface(
                outputRegion.Width,
                outputRegion.Height,
                outputStride,
                pixelLength,
                pixels);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(pixels);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var processor in _componentProcessors.Values)
        {
            await processor.DisposeAsync().ConfigureAwait(false);
        }

        _componentProcessors.Clear();
        if (_stagingTexture != 0)
        {
            Marshal.Release(_stagingTexture);
            _stagingTexture = 0;
        }

    }

    private D3D11FrameProcessor GetComponentProcessor(nint device, nint context)
    {
        var key = (device, context);
        if (!_componentProcessors.TryGetValue(key, out var processor))
        {
            processor = new D3D11FrameProcessor(device, context);
            _componentProcessors.Add(key, processor);
        }

        return processor;
    }

    private nint EnsureStagingTexture(Texture2DDescription description)
    {
        if (_stagingTexture != 0 &&
            _stagingWidth == description.Width &&
            _stagingHeight == description.Height &&
            _stagingFormat == description.Format)
        {
            return _stagingTexture;
        }

        var hr = CreateTexture2D(ref description, out var replacement);
        Marshal.ThrowExceptionForHR(hr);
        if (_stagingTexture != 0)
        {
            Marshal.Release(_stagingTexture);
        }

        _stagingTexture = replacement;
        _stagingWidth = description.Width;
        _stagingHeight = description.Height;
        _stagingFormat = description.Format;
        return replacement;
    }

    private static Texture2DDescription ReadTextureDescription(nint texture)
    {
        var vtable = Marshal.ReadIntPtr(texture);
        var method = Marshal.GetDelegateForFunctionPointer<GetDescriptionDelegate>(
            Marshal.ReadIntPtr(vtable, TextureGetDescriptionVtableIndex * IntPtr.Size));
        method(texture, out var description);
        return description;
    }

    private int CreateTexture2D(ref Texture2DDescription description, out nint texture)
    {
        var vtable = Marshal.ReadIntPtr(_device);
        var method = Marshal.GetDelegateForFunctionPointer<CreateTexture2DDelegate>(
            Marshal.ReadIntPtr(vtable, DeviceCreateTexture2DVtableIndex * IntPtr.Size));
        return method(_device, ref description, 0, out texture);
    }

    private void CopySubresourceRegion(
        nint destination,
        uint destinationSubresource,
        uint destinationX,
        uint destinationY,
        uint destinationZ,
        nint source,
        uint sourceSubresource,
        ref D3D11Box sourceBox)
    {
        var vtable = Marshal.ReadIntPtr(_context);
        var method = Marshal.GetDelegateForFunctionPointer<CopySubresourceRegionDelegate>(
            Marshal.ReadIntPtr(vtable, ContextCopySubresourceRegionVtableIndex * IntPtr.Size));
        method(
            _context,
            destination,
            destinationSubresource,
            destinationX,
            destinationY,
            destinationZ,
            source,
            sourceSubresource,
            ref sourceBox);
    }

    private int Map(nint resource, uint subresource, uint mapType, uint flags, out MappedSubresource mapped)
    {
        var vtable = Marshal.ReadIntPtr(_context);
        var method = Marshal.GetDelegateForFunctionPointer<MapDelegate>(
            Marshal.ReadIntPtr(vtable, ContextMapVtableIndex * IntPtr.Size));
        return method(_context, resource, subresource, mapType, flags, out mapped);
    }

    private void Unmap(nint resource, uint subresource)
    {
        var vtable = Marshal.ReadIntPtr(_context);
        var method = Marshal.GetDelegateForFunctionPointer<UnmapDelegate>(
            Marshal.ReadIntPtr(vtable, ContextUnmapVtableIndex * IntPtr.Size));
        method(_context, resource, subresource);
    }

    private sealed class Bgra32FrameSurface : IBgra32FrameSurface, ICloneableFrameSurface
    {
        private byte[]? _pixels;
        private readonly int _length;

        public Bgra32FrameSurface(int width, int height, int stride, int length, byte[] pixels)
        {
            Width = width;
            Height = height;
            Stride = stride;
            _length = length;
            _pixels = pixels;
        }

        public int Width { get; }
        public int Height { get; }
        public int Stride { get; }
        public ReadOnlyMemory<byte> Bgra32 => (_pixels ?? throw new ObjectDisposedException(nameof(Bgra32FrameSurface))).AsMemory(0, _length);

        public IFrameSurface Clone()
        {
            var source = _pixels ?? throw new ObjectDisposedException(nameof(Bgra32FrameSurface));
            var clone = ArrayPool<byte>.Shared.Rent(_length);
            try
            {
                source.AsMemory(0, _length).CopyTo(clone.AsMemory(0, _length));
                return new Bgra32FrameSurface(Width, Height, Stride, _length, clone);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(clone);
                throw;
            }
        }

        public void Dispose()
        {
            var pixels = Interlocked.Exchange(ref _pixels, null);
            if (pixels is not null)
            {
                ArrayPool<byte>.Shared.Return(pixels);
            }
        }
    }

    private const uint DxgiFormatBgra8 = 87;
    private const uint D3D11UsageStaging = 3;
    private const uint D3D11CpuAccessRead = 0x20000;
    private const uint D3D11MapRead = 1;
    private const int DeviceCreateTexture2DVtableIndex = 5;
    private const int TextureGetDescriptionVtableIndex = 10;
    private const int ContextMapVtableIndex = 14;
    private const int ContextUnmapVtableIndex = 15;
    private const int ContextCopySubresourceRegionVtableIndex = 46;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Texture2DDescription(
        uint Width,
        uint Height,
        uint MipLevels,
        uint ArraySize,
        uint Format,
        uint SampleCount,
        uint SampleQuality,
        uint Usage,
        uint BindFlags,
        uint CPUAccessFlags,
        uint MiscFlags);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct D3D11Box(
        uint Left,
        uint Top,
        uint Front,
        uint Right,
        uint Bottom,
        uint Back);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct MappedSubresource(nint Data, uint RowPitch, uint DepthPitch);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GetDescriptionDelegate(nint self, out Texture2DDescription description);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTexture2DDelegate(
        nint self,
        ref Texture2DDescription description,
        nint initialData,
        out nint texture);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CopySubresourceRegionDelegate(
        nint self,
        nint destination,
        uint destinationSubresource,
        uint destinationX,
        uint destinationY,
        uint destinationZ,
        nint source,
        uint sourceSubresource,
        ref D3D11Box sourceBox);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int MapDelegate(
        nint self,
        nint resource,
        uint subresource,
        uint mapType,
        uint flags,
        out MappedSubresource mapped);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void UnmapDelegate(nint self, nint resource, uint subresource);
}
