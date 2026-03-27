using System.IO;
using System.Text.Json;

namespace Voip.Client.Wpf;

internal sealed class ClientSettings
{
    public string ServerUrl { get; set; } = "ws://localhost:8181";
    public string UserName { get; set; } = Environment.UserName;
    public string Room { get; set; } = "general";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static ClientSettings Load()
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path))
            {
                return new ClientSettings();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ClientSettings>(json) ?? new ClientSettings();
        }
        catch
        {
            return new ClientSettings();
        }
    }

    public void Save()
    {
        var path = GetSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    private static string GetSettingsPath()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoipClient");

        return Path.Combine(folder, "settings.json");
    }
}
