using System.IO;
using System.Text.Json;

namespace ShmViewer.Core;

public static class HeaderPathsStorage
{
    private static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "header_paths.json");

    public static List<string> Load()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? new();
            return paths.Where(File.Exists).ToList();
        }
        catch
        {
            return new();
        }
    }

    public static void Save(IEnumerable<string> paths)
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(paths.ToList()));
        }
        catch { /* 저장 실패 시 무시 */ }
    }
}
