using System.Buffers.Binary;
using System.Text;

namespace AreaRec.Media;

public sealed record Mp4Inspection(
    bool HasFileTypeBox,
    bool HasVideoTrack,
    int Width,
    int Height,
    TimeSpan Duration,
    bool HasAudioTrack = false);

public static class Mp4Inspector
{
    public static Mp4Inspection Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 8)
        {
            throw new InvalidDataException("The MP4 file is shorter than a box header.");
        }

        var root = ReadBoxes(bytes, 0, bytes.Length);
        var hasFileType = root.Any(box => box.Type is "ftyp");
        var moov = root.FirstOrDefault(box => box.Type is "moov");
        if (moov.End == 0)
        {
            return new Mp4Inspection(hasFileType, false, 0, 0, TimeSpan.Zero, false);
        }

        var tracks = Children(bytes, moov, "trak").ToArray();
        var hasAudio = tracks.Any(track =>
        {
            var media = Child(bytes, track, "mdia");
            var handler = media is { } value ? Child(bytes, value, "hdlr") : null;
            return handler is { } audioHandler && IsHandler(bytes, audioHandler, "soun");
        });

        foreach (var track in tracks)
        {
            var media = Child(bytes, track, "mdia");
            if (media is null)
            {
                continue;
            }

            var handler = Child(bytes, media.Value, "hdlr");
            if (handler is null || !IsHandler(bytes, handler.Value, "vide"))
            {
                continue;
            }

            var mediaHeader = Child(bytes, media.Value, "mdhd");
            var sampleDescription = Child(bytes, media.Value, "minf") is { } minf
                ? Child(bytes, minf, "stbl") is { } sampleTable
                    ? Child(bytes, sampleTable, "stsd")
                    : null
                : null;
            var dimensions = sampleDescription is { } description
                ? ReadVideoDimensions(bytes, description)
                : (Width: 0, Height: 0);
            var duration = mediaHeader is { } header
                ? ReadDuration(bytes, header)
                : TimeSpan.Zero;
            return new Mp4Inspection(true, true, dimensions.Width, dimensions.Height, duration, hasAudio);
        }

        return new Mp4Inspection(hasFileType, false, 0, 0, TimeSpan.Zero, hasAudio);
    }

    private static bool IsHandler(byte[] bytes, Box handler, string expectedType)
    {
        var offset = handler.PayloadStart + 8;
        return offset + 4 <= handler.End && ReadType(bytes, offset) == expectedType;
    }

    private static TimeSpan ReadDuration(byte[] bytes, Box header)
    {
        if (header.PayloadStart + 20 > header.End)
        {
            return TimeSpan.Zero;
        }

        var version = bytes[header.PayloadStart];
        ulong timescale;
        ulong duration;
        if (version == 1 && header.PayloadStart + 32 <= header.End)
        {
            timescale = ReadUInt32(bytes, header.PayloadStart + 20);
            duration = ReadUInt64(bytes, header.PayloadStart + 24);
        }
        else
        {
            timescale = ReadUInt32(bytes, header.PayloadStart + 12);
            duration = ReadUInt32(bytes, header.PayloadStart + 16);
        }

        return timescale == 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(duration / (double)timescale);
    }

    private static (int Width, int Height) ReadVideoDimensions(byte[] bytes, Box sampleDescription)
    {
        if (sampleDescription.PayloadStart + 16 > sampleDescription.End)
        {
            return (0, 0);
        }

        var entryStart = sampleDescription.PayloadStart + 8;
        if (entryStart + 36 > sampleDescription.End || ReadType(bytes, entryStart + 4) is not ("avc1" or "avc3"))
        {
            return (0, 0);
        }

        return (
            BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(entryStart + 32, 2)),
            BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(entryStart + 34, 2)));
    }

    private static Box? Child(byte[] bytes, Box parent, string type)
    {
        return Children(bytes, parent, type).FirstOrDefault() is { End: not 0 } box ? box : null;
    }

    private static IEnumerable<Box> Children(byte[] bytes, Box parent, string type)
    {
        foreach (var box in ReadBoxes(bytes, parent.PayloadStart, parent.End))
        {
            if (box.Type == type)
            {
                yield return box;
            }
        }
    }

    private static List<Box> ReadBoxes(byte[] bytes, int start, int end)
    {
        var boxes = new List<Box>();
        var offset = start;
        while (offset + 8 <= end)
        {
            var size32 = ReadUInt32(bytes, offset);
            var type = ReadType(bytes, offset + 4);
            var headerSize = 8;
            ulong size = size32;
            if (size32 == 1 && offset + 16 <= end)
            {
                size = ReadUInt64(bytes, offset + 8);
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                size = (ulong)(end - offset);
            }

            if (size < (uint)headerSize || size > (ulong)(end - offset) || size > int.MaxValue)
            {
                break;
            }

            var boxEnd = checked(offset + (int)size);
            boxes.Add(new Box(type, offset, offset + headerSize, boxEnd));
            offset = boxEnd;
        }

        return boxes;
    }

    private static string ReadType(byte[] bytes, int offset) => Encoding.ASCII.GetString(bytes, offset, 4);

    private static uint ReadUInt32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));

    private static ulong ReadUInt64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset, 8));

    private readonly record struct Box(string Type, int Start, int PayloadStart, int End);
}
