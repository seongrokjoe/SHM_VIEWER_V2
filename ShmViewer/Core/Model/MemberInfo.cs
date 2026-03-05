namespace ShmViewer.Core.Model;

public class MemberInfo
{
    public string Name { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public TypeInfo? ResolvedType { get; set; }
    public EnumInfo? ResolvedEnum { get; set; }
    public PrimitiveKind Primitive { get; set; } = PrimitiveKind.None;
    public int Offset { get; set; }
    public int Size { get; set; }
    public int ArrayCount { get; set; } = 1;
    public bool IsPointer { get; set; }
    public int BitFieldWidth { get; set; }  // 0 = not a bitfield
    public int BitFieldOffset { get; set; } // bit offset within storage unit
    public bool IsSpare => IsSpareByName(Name);

    private static bool IsSpareByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var lower = name.ToLower();
        return lower.Contains("spare") || lower.Contains("reserved") || lower.Contains("dummy");
    }
}
