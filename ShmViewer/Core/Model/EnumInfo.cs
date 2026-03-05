namespace ShmViewer.Core.Model;

public class EnumInfo
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, long> Members { get; set; } = new();

    public string? FindName(long value)
    {
        foreach (var kv in Members)
            if (kv.Value == value)
                return kv.Key;
        return null;
    }
}
