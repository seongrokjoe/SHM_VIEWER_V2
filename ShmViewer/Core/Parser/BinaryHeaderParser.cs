using ClangSharp.Interop;
using ShmViewer.Core.Dat;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace ShmViewer.Core.Parser;

internal sealed class BinaryHeaderParser
{
    private readonly Dictionary<string, BinaryType> _types = new(StringComparer.Ordinal);
    private readonly HashSet<string> _building = new(StringComparer.Ordinal);
    private readonly List<(CXCursor Cursor, BinaryType Type)> _records = new();

    public unsafe BinarySchema Parse(IEnumerable<string> headers, string structName, HeaderParseOptions options)
    {
        if (options.Pack is not (1 or 2 or 4 or 8 or 16)) throw new InvalidDataException("Pack은 1, 2, 4, 8, 16 중 하나여야 합니다.");
        var paths = headers.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0 || string.IsNullOrWhiteSpace(structName)) throw new InvalidDataException("헤더와 Struct를 지정하세요.");
        foreach (var path in paths) { using var stream = File.OpenRead(path); }
        var tempFile = Path.Combine(Path.GetTempPath(), "shm_binary_" + Guid.NewGuid().ToString("N") + ".cpp");
        try
        {
            File.WriteAllText(tempFile, $"#pragma pack({options.Pack})\n" + string.Join("\n", paths.Select(p => "#include \"" + p.Replace('\\', '/') + "\"")), new UTF8Encoding(false));
            var args = new List<string> { "-x", "c++", "-std=c++14", "--target=x86_64-pc-windows-msvc", "-fms-extensions", "-fms-compatibility", $"-fpack-struct={options.Pack}" };
            args.AddRange(paths.Select(Path.GetDirectoryName).OfType<string>().Concat(options.IncludeDirectories).Distinct(StringComparer.OrdinalIgnoreCase).Select(p => "-I" + p));
            args.AddRange(options.Defines.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => "-D" + d));
            using var index = CXIndex.Create();
            using var tu = CXTranslationUnit.Parse(index, tempFile, args.ToArray(), ReadOnlySpan<CXUnsavedFile>.Empty,
                CXTranslationUnit_Flags.CXTranslationUnit_SkipFunctionBodies);
            var errors = new List<string>();
            for (uint i = 0; i < tu.NumDiagnostics; i++)
            {
                using var diagnostic = tu.GetDiagnostic(i);
                if (diagnostic.Severity >= CXDiagnosticSeverity.CXDiagnostic_Error)
                    errors.Add(diagnostic.Format(CXDiagnosticDisplayOptions.CXDiagnostic_DisplaySourceLocation).ToString());
            }
            if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
            CXType? rootType = null;
            var requested = structName.Trim();
            foreach (var prefix in new[] { "struct ", "union ", "class " })
                if (requested.StartsWith(prefix, StringComparison.Ordinal)) requested = requested[prefix.Length..];
            void Find(CXCursor cursor, string scope)
            {
                foreach (var child in Children(cursor))
                {
                    var name = child.Spelling.ToString();
                    var fullName = string.IsNullOrEmpty(scope) ? name : scope + "::" + name;
                    if ((fullName == requested || name == requested && !requested.Contains("::")) &&
                        child.Kind is CXCursorKind.CXCursor_StructDecl or CXCursorKind.CXCursor_UnionDecl or CXCursorKind.CXCursor_ClassDecl or CXCursorKind.CXCursor_TypedefDecl or CXCursorKind.CXCursor_TypeAliasDecl)
                    {
                        if (child.Type.SizeOf > 0)
                        {
                            if (rootType != null && rootType.Value.CanonicalType != child.Type.CanonicalType)
                                throw new InvalidDataException("동일한 Struct 이름이 여러 범위에 있습니다. 정규화된 이름을 입력하세요.");
                            rootType = child.Type;
                        }
                    }
                    if (child.Kind == CXCursorKind.CXCursor_Namespace) Find(child, fullName);
                    else if (child.Kind == CXCursorKind.CXCursor_LinkageSpec) Find(child, scope);
                }
            }
            Find(tu.Cursor, string.Empty);
            if (rootType == null) throw new InvalidDataException($"Struct '{requested}'을 찾을 수 없습니다.");
            var type = BuildType(rootType.Value);
            ReadRecordPacking(index, tempFile, args.ToArray());
            if (type.Kind is not (BinaryKind.Record or BinaryKind.Union)) throw new InvalidDataException("Struct에는 구조체 또는 union을 지정하세요.");
            return new(requested, type);
        }
        finally { if (File.Exists(tempFile)) File.Delete(tempFile); }
    }

    private BinaryType BuildType(CXType original)
    {
        var type = original.CanonicalType;
        var size = checked((int)type.SizeOf);
        if (size <= 0) throw new InvalidDataException($"크기를 확정할 수 없는 타입: {original.Spelling}");
        var name = original.Spelling.ToString();
        if (type.kind == CXTypeKind.CXType_ConstantArray)
            return new BinaryType { Name = name, Kind = BinaryKind.Array, Size = size, Alignment = checked((int)type.AlignOf), Element = BuildType(type.ArrayElementType), Count = checked((int)type.ArraySize) };
        if (type.kind == CXTypeKind.CXType_Record)
        {
            var declaration = type.Declaration;
            var key = declaration.Usr.ToString();
            if (_building.Contains(key)) throw new InvalidDataException("순환 구조체 레이아웃: " + name);
            if (_types.TryGetValue(key, out var cached)) return cached;
            if (!type.IsPODType) throw new InvalidDataException("비 POD C++ 객체는 DAT 변환을 지원하지 않습니다: " + name);
            var record = new BinaryType { Name = name, Kind = declaration.Kind == CXCursorKind.CXCursor_UnionDecl ? BinaryKind.Union : BinaryKind.Record,
                Size = size, Alignment = checked((int)type.AlignOf), ExplicitAlignment = checked((int)declaration.MaxAlignment / 8) };
            _building.Add(key);
            var children = Children(declaration);
            var fields = children.Where(c => c.Kind == CXCursorKind.CXCursor_FieldDecl).ToList();
            var anonymousOrdinal = 0;
            foreach (var field in fields)
            {
                var fieldName = field.Spelling.ToString();
                var bits = field.IsBitField ? field.FieldDeclBitWidth : 0;
                var padding = field.IsBitField && string.IsNullOrEmpty(fieldName);
                var bitOffset = field.OffsetOfField;
                if (bitOffset < 0) throw new InvalidDataException("멤버 오프셋을 확정할 수 없습니다: " + fieldName);
                var anonymous = string.IsNullOrEmpty(fieldName);
                if (anonymous) fieldName = $"(anonymous{anonymousOrdinal++})";
                record.Members.Add(new(fieldName, BuildType(field.Type), checked((int)(bitOffset / 8)), field.IsBitField ? (int)(bitOffset % 8) : 0, bits, anonymous, padding, checked((int)field.MaxAlignment / 8)));
            }
            // Clang can expose promoted anonymous records without a carrier FieldDecl.
            foreach (var child in children.Where(c => c.Kind is CXCursorKind.CXCursor_StructDecl or CXCursorKind.CXCursor_UnionDecl && c.IsAnonymousRecordDecl))
            {
                if (fields.Any(f => f.Type.CanonicalType == child.Type.CanonicalType)) continue;
                var nested = BuildType(child.Type);
                var offset = FindAnonymousOffset(type, child.Type, 0);
                record.Members.Add(new($"(anonymous{anonymousOrdinal++})", nested, offset, IsAnonymous: true));
            }
            foreach (var member in record.Members)
            {
                var length = member.IsPadding && member.BitWidth == 0 ? 0 : member.BitWidth > 0 ? (member.BitOffset + member.BitWidth + 7) / 8 : member.Type.Size;
                if (member.Offset < 0 || (long)member.Offset + length > size) throw new InvalidDataException("멤버 범위가 구조체를 벗어납니다: " + member.Name);
            }
            // Stable ordering retains declaration order for shared bitfield/union offsets.
            var ordered = record.Members.OrderBy(m => m.Offset).ToArray();
            record.Members.Clear(); record.Members.AddRange(ordered);
            _records.Add((declaration, record));
            _building.Remove(key); _types[key] = record;
            return record;
        }
        var kind = Primitive(type.kind);
        var unsigned = type.kind is CXTypeKind.CXType_UChar or CXTypeKind.CXType_Char_U or CXTypeKind.CXType_UShort or CXTypeKind.CXType_UInt or CXTypeKind.CXType_ULong or CXTypeKind.CXType_ULongLong;
        if (type.kind == CXTypeKind.CXType_Enum)
        {
            var declaration = type.Declaration;
            var underlying = declaration.EnumDecl_IntegerType.CanonicalType;
            unsigned = Primitive(underlying.kind) == BinaryKind.Unsigned;
            var result = new BinaryType { Name = name, Kind = BinaryKind.Enum, Size = size, Alignment = checked((int)type.AlignOf), IsUnsigned = unsigned };
            foreach (var item in Children(declaration).Where(c => c.Kind == CXCursorKind.CXCursor_EnumConstantDecl))
                result.EnumValues[item.Spelling.ToString()] = unsigned ? new BigInteger(item.EnumConstantDeclUnsignedValue) : new BigInteger(item.EnumConstantDeclValue);
            return result;
        }
        if (size > 8) throw new InvalidDataException("지원하지 않는 기본 타입 크기: " + name);
        return new BinaryType { Name = name, Kind = kind, Size = size, Alignment = checked((int)type.AlignOf), IsUnsigned = unsigned };
    }

    private unsafe void ReadRecordPacking(CXIndex index, string mainFile, string[] args)
    {
        // Ask the preprocessor at each declaration, respecting includes, macros and inactive branches.
        // Unsaved overlays leave the user's headers untouched.
        var insertions = new Dictionary<string, List<(uint Offset, int Index)>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _records.Count; i++)
        {
            _records[i].Cursor.Extent.Start.GetExpansionLocation(out var file, out _, out _, out var offset);
            var path = file.Name.ToString();
            if (!insertions.TryGetValue(path, out var list)) insertions[path] = list = new();
            list.Add((offset, i));
        }
        var overlays = new List<CXUnsavedFile>();
        try
        {
            foreach (var (path, positions) in insertions)
            {
                var bytes = File.ReadAllBytes(path);
                using var content = new MemoryStream();
                var previous = 0;
                foreach (var (offset, id) in positions.OrderBy(p => p.Offset))
                {
                    content.Write(bytes, previous, checked((int)offset - previous));
                    var marker = Encoding.UTF8.GetBytes($" _Pragma(\"message(\\\"SHM_VIEWER_PACK_{id}\\\")\") _Pragma(\"pack(show)\") ");
                    content.Write(marker); previous = (int)offset;
                }
                content.Write(bytes, previous, bytes.Length - previous);
                overlays.Add(CXUnsavedFile.Create(path, Encoding.UTF8.GetString(content.ToArray())));
            }
            using var probe = CXTranslationUnit.Parse(index, mainFile, args, overlays.ToArray(), CXTranslationUnit_Flags.CXTranslationUnit_SkipFunctionBodies);
            var current = -1;
            var found = new HashSet<int>();
            for (uint i = 0; i < probe.NumDiagnostics; i++)
            {
                using var diagnostic = probe.GetDiagnostic(i);
                var text = diagnostic.Spelling.ToString();
                var marker = Regex.Match(text, @"^SHM_VIEWER_PACK_(\d+)$");
                if (marker.Success) { current = int.Parse(marker.Groups[1].Value); continue; }
                var pack = Regex.Match(text, @"#pragma pack\(show\) == (\d+)");
                if (current >= 0 && pack.Success)
                {
                    _records[current].Type.Pack = int.Parse(pack.Groups[1].Value);
                    found.Add(current); current = -1;
                }
            }
            if (found.Count != _records.Count)
                throw new InvalidDataException("일부 구조체의 pack을 확인할 수 없습니다. 헤더의 구조체 선언 및 매크로를 확인하세요.");
        }
        finally { foreach (var overlay in overlays) overlay.Dispose(); }
    }
    private static int FindAnonymousOffset(CXType outer, CXType inner, int innerOffset)
    {
        foreach (var child in Children(inner.Declaration))
        {
            if (child.Kind == CXCursorKind.CXCursor_FieldDecl && !string.IsNullOrEmpty(child.Spelling.ToString()))
            {
                var offset = outer.GetOffsetOf(child.Spelling.ToString());
                if (offset >= 0) return checked((int)(offset / 8 - child.OffsetOfField / 8 - innerOffset));
            }
            if (child.Kind is CXCursorKind.CXCursor_StructDecl or CXCursorKind.CXCursor_UnionDecl && child.IsAnonymousRecordDecl)
            {
                try { return FindAnonymousOffset(outer, child.Type, innerOffset + FindAnonymousOffset(inner, child.Type, 0)); }
                catch (InvalidDataException) { }
            }
        }
        throw new InvalidDataException("익명 구조체의 오프셋을 확정할 수 없습니다.");
    }
    private static BinaryKind Primitive(CXTypeKind kind) => kind switch
    {
        CXTypeKind.CXType_Char_S or CXTypeKind.CXType_Char_U => BinaryKind.Char,
        CXTypeKind.CXType_SChar or CXTypeKind.CXType_Short or CXTypeKind.CXType_Int or CXTypeKind.CXType_Long or CXTypeKind.CXType_LongLong => BinaryKind.Signed,
        CXTypeKind.CXType_UChar or CXTypeKind.CXType_UShort or CXTypeKind.CXType_UInt or CXTypeKind.CXType_ULong or CXTypeKind.CXType_ULongLong => BinaryKind.Unsigned,
        CXTypeKind.CXType_Float => BinaryKind.Float, CXTypeKind.CXType_Double => BinaryKind.Double,
        CXTypeKind.CXType_Bool => BinaryKind.Bool, CXTypeKind.CXType_WChar => BinaryKind.WChar,
        CXTypeKind.CXType_Pointer => BinaryKind.Pointer, CXTypeKind.CXType_Enum => BinaryKind.Enum,
        _ => throw new InvalidDataException("지원하지 않는 바이너리 타입: " + kind)
    };
    private static unsafe List<CXCursor> Children(CXCursor parent)
    {
        var children = new List<CXCursor>();
        parent.VisitChildren(new CXCursorVisitor((child, _, _) => { children.Add(child); return CXChildVisitResult.CXChildVisit_Continue; }), new CXClientData(IntPtr.Zero));
        return children;
    }
}
