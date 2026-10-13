using System.IO;
using System.Text.Json;

namespace ShmViewer.Core;

public sealed record RealTimeSettings(bool UseDefaultSctcClean, List<string> Headers, List<ShmTabEntry> Tabs);

public interface IRealTimeSettingsStore
{
    RealTimeSettings Load();
    void Save(RealTimeSettings settings);
}

public sealed class RealTimeSettingsStorage : IRealTimeSettingsStore
{
    private readonly string _directory;
    public RealTimeSettingsStorage(string? directory = null) => _directory = directory ?? AppContext.BaseDirectory;
    public RealTimeSettings Load()
    {
        var path = Path.Combine(_directory, "realtime_settings.json");
        if (File.Exists(path))
            return JsonSerializer.Deserialize<RealTimeSettings>(File.ReadAllText(path))
                ?? throw new InvalidDataException("RealTime 설정 파일이 비어 있습니다.");
        List<T> ReadLegacy<T>(string name)
        {
            var file = Path.Combine(_directory, name);
            return File.Exists(file) ? JsonSerializer.Deserialize<List<T>>(File.ReadAllText(file)) ?? new() : new();
        }
        return new(false, ReadLegacy<string>("header_paths.json"), ReadLegacy<ShmTabEntry>("shm_tabs.json"));
    }
    public void Save(RealTimeSettings settings)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "realtime_settings.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
