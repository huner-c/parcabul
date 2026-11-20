using System.IO;
using System.Text.Json;

namespace ParcaBul.Services;

internal sealed record AppSettings(bool InstantCapture)
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ParcaBul", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings(false);
        }
    }

    public void Save()
    {
        string temporaryPath = $"{SettingsPath}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this));
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
