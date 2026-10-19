using System.IO;
using System.Text.RegularExpressions;
using ShmViewer.Core.Parser;

namespace ShmViewer.Core.Dat;

public sealed record DatTypeChoice(string Name, string Source, BinaryType? Type)
{
    public override string ToString() => $"{Name} · {Source}" + (Type == null ? "" : $" · {Type.Size:N0} B");
}

public sealed class DatTypeCatalog
{
    public static IReadOnlyList<DatTypeChoice> Primitives { get; } = new[]
    {
        Primitive("char", BinaryKind.Char, 1), Primitive("signed char", BinaryKind.Signed, 1), Primitive("unsigned char", BinaryKind.Unsigned, 1),
        Primitive("short", BinaryKind.Signed, 2), Primitive("unsigned short", BinaryKind.Unsigned, 2),
        Primitive("int", BinaryKind.Signed, 4), Primitive("unsigned int", BinaryKind.Unsigned, 4),
        Primitive("long", BinaryKind.Signed, 4), Primitive("unsigned long", BinaryKind.Unsigned, 4),
        Primitive("long long", BinaryKind.Signed, 8), Primitive("unsigned long long", BinaryKind.Unsigned, 8),
        Primitive("float", BinaryKind.Float, 4), Primitive("double", BinaryKind.Double, 8),
        Primitive("bool", BinaryKind.Bool, 1), Primitive("wchar_t", BinaryKind.WChar, 2)
    };
    private static DatTypeChoice Primitive(string name, BinaryKind kind, int size) => new(name, "기본 타입", new()
    { Name = name, Kind = kind, Size = size, Alignment = size, IsUnsigned = kind is BinaryKind.Unsigned or BinaryKind.WChar });
    private string[] _headers = Array.Empty<string>();
    private HeaderParseOptions _options = new();
    public string[] Headers { get => _headers; set { _headers = value; _resolved.Clear(); } }
    public HeaderParseOptions Options { get => _options; set { _options = value; _resolved.Clear(); } }
    public IReadOnlyList<DatTypeChoice> Choices { get; private set; } = Primitives;
    private string[] _headerTypes = Array.Empty<string>();
    private readonly Dictionary<string, BinaryType> _resolved = new(StringComparer.Ordinal);
    public void SetDocument(DatDocument document)
    {
        var choices = new List<DatTypeChoice>(Primitives); var visited = new HashSet<BinaryType>(ReferenceEqualityComparer.Instance);
        void Add(BinaryType type, string path)
        {
            if (!visited.Add(type)) return;
            if (type.Kind is BinaryKind.Record or BinaryKind.Union or BinaryKind.Enum)
                choices.Add(new(type.Name.Replace("struct ", "").Replace("enum ", "").Replace("union ", ""), "현재 구조: " + path, type));
            if (type.Element != null) Add(type.Element, path + "[*]");
            foreach (var member in type.Members) Add(member.Type, path + "." + member.Name);
        }
        Add(document.Schema.Root, document.Schema.StructName);
        choices.AddRange(_headerTypes.Select(n => new DatTypeChoice(n, "참조 헤더", null)));
        Choices = choices;
    }
    private BinaryType ParseReference(string baseName, ICollection<string>? names = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "shm_type_" + Guid.NewGuid().ToString("N") + ".h");
        try
        {
            File.WriteAllText(path, $"struct __SingleLoadTypeProbe {{ {baseName} value; }};");
            return new BinaryHeaderParser().Parse(Headers.Append(path), "__SingleLoadTypeProbe", Options, names).Root.Members.Single(m => m.Name == "value").Type;
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    public void RefreshHeaders(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); var names = new HashSet<string>(StringComparer.Ordinal);
        ParseReference("int", names); token.ThrowIfCancellationRequested();
        _headerTypes = names.Order(StringComparer.Ordinal).ToArray(); _resolved.Clear();
        Choices = Choices.Where(c => c.Type != null).Concat(_headerTypes.Select(n => new DatTypeChoice(n, "참조 헤더", null))).ToArray();
    }
    public BinaryType Resolve(string expression, DatTypeChoice? selected = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var match = Regex.Match(expression.Trim(), @"^(?<base>(?:struct\s+|enum\s+|union\s+)?[A-Za-z_]\w*(?:(?:::|\s+)[A-Za-z_]\w*)*)(?<arrays>(?:\s*\[\s*[1-9]\d*\s*\])*)$");
        if (!match.Success) throw new InvalidDataException("타입명 또는 고정 배열을 입력하세요. 예: unsigned long, MyStruct, long[10], Item[2][3]");
        var name = Regex.Replace(match.Groups["base"].Value, @"\s+", " ");
        name = Regex.Replace(name, @"^(struct|enum|union) ", "");
        BinaryType type;
        if (selected != null && selected.Name == name) type = selected.Type ?? ResolveHeader(name);
        else
        {
            var choices = Choices.Where(c => c.Name == name).ToArray();
            var distinct = choices.Where(c => c.Type != null).Select(c => c.Type!).ToArray();
            if (choices.Any(c => c.Type == null) && distinct.Length > 0 || distinct.Length > 1 && distinct.Skip(1).Any(t => !DatSchemaFile.SameLayout(t, distinct[0])))
                throw new InvalidDataException("같은 이름의 타입이 여러 출처에 있습니다. 목록에서 사용할 정의를 선택하세요.");
            type = distinct.FirstOrDefault() ?? ResolveHeader(name);
        }
        var dimensions = Regex.Matches(match.Groups["arrays"].Value, @"\d+").Select(m => int.Parse(m.Value)).ToArray();
        for (var i = dimensions.Length - 1; i >= 0; i--) type = BinaryLayoutBuilder.Array(type, dimensions[i]);
        token.ThrowIfCancellationRequested(); return type;
    }
    private BinaryType ResolveHeader(string name)
    {
        if (_resolved.TryGetValue(name, out var type)) return type;
        if (Headers.Length == 0) throw new InvalidDataException($"'{name}' 타입을 찾을 수 없습니다. 참조 헤더를 추가하거나 현재 구조의 타입을 선택하세요.");
        return _resolved[name] = ParseReference(name);
    }
}
