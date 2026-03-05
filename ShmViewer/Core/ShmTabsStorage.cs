using System.IO;
using System.Text.Json;

namespace ShmViewer.Core;

public record ShmTabEntry(string ShmName, string StructName, string RefreshMode);

public static class ShmTabsStorage
{
    private static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "shm_tabs.json");

    public static List<ShmTabEntry> Load()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<ShmTabEntry>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
            return new();
        }
    }

    public static void Save(IEnumerable<ShmTabEntry> entries)
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(entries.ToList()));
        }
        catch { }
    }
}
