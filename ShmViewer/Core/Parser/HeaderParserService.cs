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

        // Array
        int arrayCount = 1;
        var elemType = memberType;
        if (canonical.kind == CXTypeKind.CXType_ConstantArray)
        {
            arrayCount = (int)canonical.ArraySize;
            elemType = canonical.ArrayElementType;
            var elemSize = elemType.SizeOf;
            member.Size = elemSize > 0 ? (int)(elemSize * arrayCount) : (int)sizeBytes;
        }
        member.ArrayCount = arrayCount;

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
}
