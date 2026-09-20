using System.Text.Json;
using AreaRec.Core.Recording;

namespace AreaRec.App;

internal sealed record AppSettings(
    int Version = 1,
    int FramesPerSecond = 30,
    VideoQuality Quality = VideoQuality.High,
    bool IncludeCursor = true,
    string? SaveFolder = null);

internal static class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
    };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AreaRec",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                return settings is null || settings.Version <= 0
                    ? new AppSettings()
                    : settings;
            }
        }
        catch (IOException)
        {
            return new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException("The local settings directory could not be resolved.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"settings.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup; the next save can remove the orphaned temp file.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup; the next save can remove the orphaned temp file.
            }
        }
    }
}
