namespace AreaRec.Media;

internal sealed class AtomicMediaFile : IAsyncDisposable
{
    private readonly string _temporaryPath;
    private readonly string _finalPath;
    private FileStream? _stream;
    private bool _committed;

    private AtomicMediaFile(string temporaryPath, string finalPath, FileStream stream)
    {
        _temporaryPath = temporaryPath;
        _finalPath = finalPath;
        _stream = stream;
    }

    public Stream Stream => _stream ?? throw new ObjectDisposedException(nameof(AtomicMediaFile));

    public string TemporaryPath => _temporaryPath;

    public void CloseForExternalWriter()
    {
        _stream?.Dispose();
        _stream = null;
    }

    public static AtomicMediaFile Create(string finalPath)
    {
        if (string.IsNullOrWhiteSpace(finalPath))
        {
            throw new ArgumentException("An output path is required.", nameof(finalPath));
        }

        var fullPath = Path.GetFullPath(finalPath);
        if (!string.Equals(Path.GetExtension(fullPath), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The output path must have an .mp4 extension.", nameof(finalPath));
        }

        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The output path must include a directory.", nameof(finalPath));
        Directory.CreateDirectory(directory);
        // Keep the .mp4 extension so Media Foundation can select its MP4 byte-stream handler.
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(fullPath)}.{Guid.NewGuid():N}.mp4");
        var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan | FileOptions.Asynchronous);
        return new AtomicMediaFile(temporaryPath, fullPath, stream);
    }

    public async ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        if (_stream is not null)
        {
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }

        if (!File.Exists(_temporaryPath) || new FileInfo(_temporaryPath).Length == 0)
        {
            throw new InvalidDataException("The media sink produced an empty output file.");
        }

        File.Move(_temporaryPath, _finalPath, overwrite: true);
        _committed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }

        if (!_committed)
        {
            TryDelete(_temporaryPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Cleanup is best effort during abort/dispose.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup is best effort during abort/dispose.
        }
    }
}
