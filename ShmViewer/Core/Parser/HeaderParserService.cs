using ClangSharp.Interop;
using ShmViewer.Core.Model;
using System.IO;
using System.Runtime.InteropServices;

namespace ShmViewer.Core.Parser;

public class ParseResult
{
    public TypeDatabase Database { get; } = new();
    public List<string> UnresolvedTypes { get; } = new();
    public bool Success => UnresolvedTypes.Count == 0;
}

public class HeaderParserService
{
    public ParseResult Parse(IEnumerable<string> headerFilePaths)
    {
        var paths = headerFilePaths.ToList();
        var result = new ParseResult();

        var combinedContent = CombinedHeaderBuilder.Build(paths);
        var tempFile = Path.Combine(Path.GetTempPath(), $"shm_combined_{Guid.NewGuid():N}.h");

        try
        {
            File.WriteAllText(tempFile, combinedContent);
            ParseFile(tempFile, result);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }

        // Post-processing: 전방 참조 등으로 누락된 ResolvedType/Size 보완
        PostProcessSizes(result.Database);

        return result;
    }

    private static unsafe void ParseFile(string filePath, ParseResult result)
    {
        var db = result.Database;

        var index = CXIndex.Create();
        try
        {
            var args = new[]
            {
                "-x", "c++",
                "-std=c++14",
                "-fms-extensions",
                "-fms-compatibility",
                "-D_WIN64",
                "-D_MSC_VER=1900"
            };

            var tu = CXTranslationUnit.Parse(
                index, filePath, args,
                ReadOnlySpan<CXUnsavedFile>.Empty,
                CXTranslationUnit_Flags.CXTranslationUnit_DetailedPreprocessingRecord |
                CXTranslationUnit_Flags.CXTranslationUnit_SkipFunctionBodies);

            // Pass 1: collect typedefs
            CollectTypedefs(tu.Cursor, db, filePath);

            // Pass 2: collect structs/enums
            CollectDeclarations(tu.Cursor, db, result.UnresolvedTypes, filePath);

            tu.Dispose();
        }
        finally
        {
            index.Dispose();
        }
    }

    private static unsafe void CollectTypedefs(CXCursor root, TypeDatabase db, string filePath)
    {
        var state = (db, filePath);
        var gcHandle = GCHandle.Alloc(state);
        try
        {
            root.VisitChildren(new CXCursorVisitor((cursor, parent, clientData) =>
            {
                var s = (ValueTuple<TypeDatabase, string>)GCHandle.FromIntPtr((IntPtr)clientData).Target!;
                if (IsFromFile(cursor, s.Item2) && cursor.Kind == CXCursorKind.CXCursor_TypedefDecl)
                    CollectTypedef(cursor, s.Item1);
                return CXChildVisitResult.CXChildVisit_Continue;
            }), new CXClientData(GCHandle.ToIntPtr(gcHandle)));
        }
        finally
        {
            gcHandle.Free();
        }
    }

    private static unsafe void CollectDeclarations(CXCursor root, TypeDatabase db,
        List<string> unresolved, string filePath)
    {
        var state = (db, unresolved, filePath);
        var gcHandle = GCHandle.Alloc(state);
        try
        {
            root.VisitChildren(new CXCursorVisitor((cursor, parent, clientData) =>
            {
                var s = (ValueTuple<TypeDatabase, List<string>, string>)GCHandle.FromIntPtr((IntPtr)clientData).Target!;
                if (!IsFromFile(cursor, s.Item3)) return CXChildVisitResult.CXChildVisit_Continue;

                switch (cursor.Kind)
                {
                    case CXCursorKind.CXCursor_StructDecl:
                    case CXCursorKind.CXCursor_UnionDecl:
                        CollectStruct(cursor, s.Item1, s.Item2);
                        break;
                    case CXCursorKind.CXCursor_EnumDecl:
                        CollectEnum(cursor, s.Item1);
                        break;
                    case CXCursorKind.CXCursor_TypedefDecl:
                        CollectTypedef(cursor, s.Item1);
                        break;
                }
                return CXChildVisitResult.CXChildVisit_Continue;
            }), new CXClientData(GCHandle.ToIntPtr(gcHandle)));
        }
        finally
        {
            gcHandle.Free();
        }
    }

    private static bool IsFromFile(CXCursor cursor, string filePath)
    {
        cursor.Location.GetFileLocation(out var file, out _, out _, out _);
        var name = file.Name.ToString();
        return string.Equals(name, filePath, StringComparison.OrdinalIgnoreCase);
    }

    private static void CollectTypedef(CXCursor cursor, TypeDatabase db)
    {
        var alias = cursor.Spelling.ToString();
        if (string.IsNullOrEmpty(alias)) return;

        var underlying = cursor.TypedefDeclUnderlyingType;
        var canonicalName = GetCleanTypeName(underlying.Spelling.ToString());

        if (!string.IsNullOrEmpty(canonicalName) && alias != canonicalName)
            db.Typedefs[alias] = canonicalName;
    }

    private static void CollectEnum(CXCursor cursor, TypeDatabase db)
    {
        var name = cursor.Spelling.ToString();
        if (string.IsNullOrEmpty(name)) return;
        if (db.Enums.ContainsKey(name)) return;

        var enumInfo = new EnumInfo { Name = name };

        unsafe
        {
            var gcHandle = GCHandle.Alloc(enumInfo);
            try
            {
                cursor.VisitChildren(new CXCursorVisitor((child, parent, clientData) =>
                {
                    if (child.Kind == CXCursorKind.CXCursor_EnumConstantDecl)
                    {
                        var ei = (EnumInfo)GCHandle.FromIntPtr((IntPtr)clientData).Target!;
                        var memberName = child.Spelling.ToString();
                        var value = clang.getEnumConstantDeclValue(child);
                        ei.Members[memberName] = value;
                    }
                    return CXChildVisitResult.CXChildVisit_Continue;
                }), new CXClientData(GCHandle.ToIntPtr(gcHandle)));
            }
            finally
            {
                gcHandle.Free();
            }
        }

        db.Enums[name] = enumInfo;
    }

    private static void CollectStruct(CXCursor cursor, TypeDatabase db, List<string> unresolved)
    {
        var name = cursor.Spelling.ToString();
        if (string.IsNullOrEmpty(name)) return;
        if (db.Structs.ContainsKey(name)) return;

        var sizeBytes = cursor.Type.SizeOf;
        if (sizeBytes < 0) return; // incomplete/forward declaration

        var typeInfo = new TypeInfo
        {
            Name = name,
            TotalSize = (int)sizeBytes,
            IsUnion = cursor.Kind == CXCursorKind.CXCursor_UnionDecl
        };

        unsafe
        {
            var state = (typeInfo, db, unresolved, name);
            var gcHandle = GCHandle.Alloc(state);
            try
            {
                cursor.VisitChildren(new CXCursorVisitor((child, parent, clientData) =>
                {
                    var s = (ValueTuple<TypeInfo, TypeDatabase, List<string>, string>)
                        GCHandle.FromIntPtr((IntPtr)clientData).Target!;

                    if (child.Kind == CXCursorKind.CXCursor_FieldDecl)
                    {
                        var member = BuildMember(child, s.Item2, s.Item3, s.Item4);
                        if (member != null)
                            s.Item1.Members.Add(member);
                    }
                    else if (child.Kind is CXCursorKind.CXCursor_StructDecl or CXCursorKind.CXCursor_UnionDecl)
                    {
                        CollectStruct(child, s.Item2, s.Item3);
                    }
                    return CXChildVisitResult.CXChildVisit_Continue;
                }), new CXClientData(GCHandle.ToIntPtr(gcHandle)));
            }
            finally
            {
                gcHandle.Free();
            }
        }

        db.Structs[name] = typeInfo;
    }

    private static unsafe MemberInfo? BuildMember(CXCursor cursor, TypeDatabase db,
        List<string> unresolved, string parentName)
    {
        var memberName = cursor.Spelling.ToString();
        var memberType = cursor.Type;
        var canonical = memberType.CanonicalType;

        var member = new MemberInfo { Name = memberName };

        // Bitfield
        var isBitField = clang.Cursor_isBitField(cursor) != 0;
        if (isBitField)
        {
            member.BitFieldWidth = clang.getFieldDeclBitWidth(cursor);
            var bitOffset = clang.Cursor_getOffsetOfField(cursor);
            member.Offset = (int)(bitOffset / 8);
            member.BitFieldOffset = (int)(bitOffset % 8);
        }
        else
        {
            var bitOffset = clang.Cursor_getOffsetOfField(cursor);
            if (bitOffset >= 0)
                member.Offset = (int)(bitOffset / 8);
        }

        // Size
        var sizeBytes = memberType.SizeOf;
        if (sizeBytes >= 0)
            member.Size = (int)sizeBytes;

        // Pointer
        if (canonical.kind == CXTypeKind.CXType_Pointer)
        {
            member.IsPointer = true;
            member.Primitive = PrimitiveKind.Pointer;
            member.TypeName = GetCleanTypeName(memberType.Spelling.ToString());
            member.Size = 8;
            return member;
        }

        // Array (multi-dimensional 지원: short arr[A][B] → ArrayDims={A,B}, ArrayCount=A*B)
        var dims = new List<int>();
        var elemType = memberType;
        var elemCanonical = canonical;

        while (true)
        {
            if (elemCanonical.kind == CXTypeKind.CXType_ConstantArray)
            {
                dims.Add((int)elemCanonical.ArraySize);
                elemType = elemType.ArrayElementType;      // 원본 타입 체인에서 추출 (spelling 보존)
                elemCanonical = elemType.CanonicalType;
            }
            else if (elemCanonical.kind == CXTypeKind.CXType_VariableArray
                  || elemCanonical.kind == CXTypeKind.CXType_DependentSizedArray)
            {
                // enum 상수 등 VLA: spelling에서 크기 해소 시도
                var dim = TryResolveVlaDim(elemCanonical, db);
                if (dim <= 0) break;
                dims.Add(dim);
                elemType = elemType.ArrayElementType;
                elemCanonical = elemType.CanonicalType;
            }
            else
            {
                break;
            }
        }

        member.ArrayDims = dims.ToArray();
        member.ArrayCount = dims.Count > 0 ? dims.Aggregate(1, (a, b) => a * b) : 1;

        ResolveElementType(member, elemType, db, unresolved, parentName);
        return member;
    }

    private static void ResolveElementType(MemberInfo member, CXType elemType,
        TypeDatabase db, List<string> unresolved, string parentName)
    {
        var canonElem = elemType.CanonicalType;
        var spelling = GetCleanTypeName(elemType.Spelling.ToString());
        var resolvedAlias = db.ResolveTypeAlias(spelling);

        // Enum?
        if (db.Enums.TryGetValue(resolvedAlias, out var enumInfo))
        {
            member.TypeName = spelling;
            member.ResolvedEnum = enumInfo;
            member.Primitive = PrimitiveKind.Int;
            if (member.Size == 0) member.Size = 4;
            return;
        }

        // Primitive via spelling
        var primitive = TypeDatabase.GetPrimitive(spelling);
        if (primitive == PrimitiveKind.None)
            primitive = TypeDatabase.GetPrimitive(resolvedAlias);

        if (primitive != PrimitiveKind.None)
        {
            member.TypeName = spelling;
            member.Primitive = primitive;
            if (member.Size == 0)
                member.Size = TypeDatabase.GetPrimitiveSize(primitive) * member.ArrayCount;
            return;
        }

        // Struct/Union?
        if (db.Structs.TryGetValue(resolvedAlias, out var typeInfo))
        {
            member.TypeName = spelling;
            member.ResolvedType = typeInfo;
            // 수정 3: member.Size가 0이면 typeInfo.TotalSize로 보완
            if (member.Size == 0) member.Size = typeInfo.TotalSize * member.ArrayCount;
            return;
        }

        // Fallback: canonical kind
        var prim = CanonicalKindToPrimitive(canonElem.kind);
        if (prim != PrimitiveKind.None)
        {
            member.TypeName = spelling;
            member.Primitive = prim;
            if (member.Size == 0)
                member.Size = TypeDatabase.GetPrimitiveSize(prim) * member.ArrayCount;
            return;
        }

        // Unresolved
        member.TypeName = spelling;
        if (!string.IsNullOrEmpty(spelling) && spelling != "void")
            unresolved.Add($"{parentName}.{member.Name} → {spelling} 미발견");
    }

    private static string GetCleanTypeName(string spelling)
    {
        var s = spelling.Trim();
        if (s.StartsWith("struct ")) s = s[7..].Trim();
        else if (s.StartsWith("union ")) s = s[6..].Trim();
        else if (s.StartsWith("enum ")) s = s[5..].Trim();
        return s;
    }

    private static PrimitiveKind CanonicalKindToPrimitive(CXTypeKind kind) => kind switch
    {
        CXTypeKind.CXType_Char_S or CXTypeKind.CXType_SChar => PrimitiveKind.Char,
        CXTypeKind.CXType_Char_U or CXTypeKind.CXType_UChar => PrimitiveKind.UChar,
        CXTypeKind.CXType_Short => PrimitiveKind.Short,
        CXTypeKind.CXType_UShort => PrimitiveKind.UShort,
        CXTypeKind.CXType_Int => PrimitiveKind.Int,
        CXTypeKind.CXType_UInt => PrimitiveKind.UInt,
        CXTypeKind.CXType_Long => PrimitiveKind.Long,
        CXTypeKind.CXType_ULong => PrimitiveKind.ULong,
        CXTypeKind.CXType_LongLong => PrimitiveKind.LongLong,
        CXTypeKind.CXType_ULongLong => PrimitiveKind.ULongLong,
        CXTypeKind.CXType_Float => PrimitiveKind.Float,
        CXTypeKind.CXType_Double => PrimitiveKind.Double,
        CXTypeKind.CXType_Bool => PrimitiveKind.Bool,
        CXTypeKind.CXType_WChar => PrimitiveKind.WChar,
        CXTypeKind.CXType_Pointer => PrimitiveKind.Pointer,
        _ => PrimitiveKind.None
    };

    // VLA/DependentSized 배열의 크기 표현식에서 enum 상수 또는 매크로 값 해소
    private static int TryResolveVlaDim(CXType vla, TypeDatabase db)
    {
        // Clang VLA의 크기 표현식 spelling 시도
        // 직접 접근이 제한되므로 spelling에서 추정
        var spelling = vla.Spelling.ToString().Trim();

        // 이미 숫자인 경우
        if (int.TryParse(spelling, out var n) && n > 0)
            return n;

        // enum 멤버에서 검색
        foreach (var enumInfo in db.Enums.Values)
        {
            if (enumInfo.Members.TryGetValue(spelling, out var val) && val > 0)
                return (int)val;
        }

        // Defines(매크로)에서 검색
        if (db.Defines.TryGetValue(spelling, out var def) && def > 0)
            return (int)def;

        return 0;
    }

    // Post-processing: 파싱 완료 후 전방 참조 해소 + bottom-up TotalSize 재계산
    private static void PostProcessSizes(TypeDatabase db)
    {
        // Step 1: 전방 참조 해소 (ResolvedType 설정만, Size 갱신은 Step 2에서)
        foreach (var typeInfo in db.Structs.Values)
        {
            foreach (var member in typeInfo.Members)
            {
                if (member.ResolvedType == null
                    && member.Primitive == PrimitiveKind.None
                    && !member.IsPointer
                    && !string.IsNullOrEmpty(member.TypeName))
                {
                    var resolved = db.ResolveType(member.TypeName);
                    if (resolved != null)
                        member.ResolvedType = resolved;
                }
            }
        }

        // Step 2: bottom-up TotalSize 재계산 (DFS post-order)
        var processed = new HashSet<string>();
        foreach (var name in db.Structs.Keys.ToList())
            RecomputeTypeSize(name, db, processed);
    }

    private static void RecomputeTypeSize(string name, TypeDatabase db, HashSet<string> processed)
    {
        if (!processed.Add(name)) return; // 이미 처리됨 또는 순환 방지
        if (!db.Structs.TryGetValue(name, out var typeInfo)) return;

        // 의존 struct 먼저 처리 (post-order DFS)
        foreach (var member in typeInfo.Members)
        {
            if (member.ResolvedType != null && !member.IsPointer)
            {
                RecomputeTypeSize(member.ResolvedType.Name, db, processed);
                var expected = member.ResolvedType.TotalSize * member.ArrayCount;
                if (expected > 0)
                    member.Size = expected;
            }
        }

        if (typeInfo.Members.Count == 0) return; // 빈 구조체는 Clang 값 유지

        int computed = typeInfo.IsUnion
            ? typeInfo.Members.Max(m => m.Size)
            : typeInfo.Members.Max(m => m.Offset + m.Size);

        if (computed > 0)
            typeInfo.TotalSize = computed;
    }
}
