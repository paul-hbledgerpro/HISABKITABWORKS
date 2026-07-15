using System.Text.Json;
using ManagerPaperworkSystem.Core.Services;

namespace ManagerPaperworkSystem.UI.Services;

public sealed class UiPreferences
{
    public string ThemeAccent { get; set; } = "NeonGreen"; // NeonGreen, Cyan, Purple, Orange, Pink
    public string LayoutPreset { get; set; } = "Default";  // Default, Compact, Spacious
}

public sealed class UiPreferencesService
{
    private readonly IAppPaths _paths;
    private readonly string _filePath;

    public UiPreferencesService(IAppPaths paths)
    {
        _paths = paths;
        _filePath = Path.Combine(_paths.AppDataDirectory, "ui_prefs.json");
    }

    public UiPreferences Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new UiPreferences();
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<UiPreferences>(json) ?? new UiPreferences();
        }
        catch
        {
            return new UiPreferences();
        }
    }

    public void Save(UiPreferences prefs)
    {
        try
        {
            var json = JsonSerializer.Serialize(prefs, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // ignore
        }
    }
}
