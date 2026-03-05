namespace ShmViewer.Core.Model;

public class TypeDatabase
{
    public Dictionary<string, TypeInfo> Structs { get; } = new();
    public Dictionary<string, EnumInfo> Enums { get; } = new();
    public Dictionary<string, string> Typedefs { get; } = new();
    public Dictionary<string, long> Defines { get; } = new();

    public TypeInfo? ResolveType(string typeName)
    {
        var resolved = ResolveTypeAlias(typeName);
        if (Structs.TryGetValue(resolved, out var t)) return t;
        return null;
    }

    public EnumInfo? ResolveEnum(string typeName)
    {
        var resolved = ResolveTypeAlias(typeName);
        if (Enums.TryGetValue(resolved, out var e)) return e;
        return null;
    }

    public string ResolveTypeAlias(string typeName)
    {
        var visited = new HashSet<string>();
        var current = typeName.Trim();
        while (Typedefs.TryGetValue(current, out var next) && visited.Add(current))
            current = next.Trim();
        return current;
    }

    public static PrimitiveKind GetPrimitive(string typeName)
    {
        return typeName.Trim() switch
        {
            "char" or "signed char" => PrimitiveKind.Char,
            "unsigned char" or "BYTE" => PrimitiveKind.UChar,
            "short" or "signed short" or "short int" => PrimitiveKind.Short,
            "unsigned short" or "WORD" or "unsigned short int" => PrimitiveKind.UShort,
            "int" or "signed int" or "signed" => PrimitiveKind.Int,
            "unsigned int" or "UINT" or "unsigned" => PrimitiveKind.UInt,
            "long" or "signed long" or "long int" => PrimitiveKind.Long,
            "unsigned long" or "DWORD" or "unsigned long int" => PrimitiveKind.ULong,
            "long long" or "signed long long" or "__int64" => PrimitiveKind.LongLong,
            "unsigned long long" or "unsigned __int64" => PrimitiveKind.ULongLong,
            "float" => PrimitiveKind.Float,
            "double" => PrimitiveKind.Double,
            "bool" or "_Bool" => PrimitiveKind.Bool,
            "wchar_t" => PrimitiveKind.WChar,
            _ => PrimitiveKind.None
        };
    }

    public static int GetPrimitiveSize(PrimitiveKind kind) => kind switch
    {
        PrimitiveKind.Char or PrimitiveKind.UChar or PrimitiveKind.Bool => 1,
        PrimitiveKind.Short or PrimitiveKind.UShort or PrimitiveKind.WChar => 2,
        PrimitiveKind.Int or PrimitiveKind.UInt => 4,
        PrimitiveKind.Long or PrimitiveKind.ULong => 4,     // Windows x64
        PrimitiveKind.LongLong or PrimitiveKind.ULongLong => 8,
        PrimitiveKind.Float => 4,
        PrimitiveKind.Double => 8,
        PrimitiveKind.Pointer => 8,                          // x64
        _ => 0
    };
}
