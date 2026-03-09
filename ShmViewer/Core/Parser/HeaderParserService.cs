using ClangSharp.Interop;
using ShmViewer.Core.Model;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

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
            CollectDefinesFromText(combinedContent, result.Database);
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
        var sourceLines = File.ReadAllLines(filePath);

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

            // Pass 2: collect enum values before struct fields are inspected.
            CollectEnums(tu.Cursor, db, filePath);

            // Pass 3: collect structs/unions with the constant table already populated.
            CollectStructDeclarations(tu.Cursor, db, result.UnresolvedTypes, filePath, sourceLines);

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

    private static unsafe void CollectEnums(CXCursor root, TypeDatabase db, string filePath)
    {
        var state = (db, filePath);
        var gcHandle = GCHandle.Alloc(state);
        try
        {
            root.VisitChildren(new CXCursorVisitor((cursor, parent, clientData) =>
            {
                var s = (ValueTuple<TypeDatabase, string>)GCHandle.FromIntPtr((IntPtr)clientData).Target!;
                if (!IsFromFile(cursor, s.Item2)) return CXChildVisitResult.CXChildVisit_Continue;

                switch (cursor.Kind)
                {
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

    private static unsafe void CollectStructDeclarations(CXCursor root, TypeDatabase db,
        List<string> unresolved, string filePath, string[] sourceLines)
    {
        var state = (db, unresolved, filePath, sourceLines);
        var gcHandle = GCHandle.Alloc(state);
        try
        {
            root.VisitChildren(new CXCursorVisitor((cursor, parent, clientData) =>
            {
                var s = (ValueTuple<TypeDatabase, List<string>, string, string[]>)GCHandle.FromIntPtr((IntPtr)clientData).Target!;
                if (!IsFromFile(cursor, s.Item3)) return CXChildVisitResult.CXChildVisit_Continue;

                if (cursor.Kind is CXCursorKind.CXCursor_StructDecl or CXCursorKind.CXCursor_UnionDecl)
                    CollectStruct(cursor, s.Item1, s.Item2, s.Item4);

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
        if (!string.IsNullOrEmpty(name) && db.Enums.ContainsKey(name)) return;

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
                        db.EnumConstants[memberName] = value;
                    }
                    return CXChildVisitResult.CXChildVisit_Continue;
                }), new CXClientData(GCHandle.ToIntPtr(gcHandle)));
            }
            finally
            {
                gcHandle.Free();
            }
        }

        if (!string.IsNullOrEmpty(name))
            db.Enums[name] = enumInfo;
    }

    private static void CollectStruct(
        CXCursor cursor,
        TypeDatabase db,
        List<string> unresolved,
        string[] sourceLines,
        string? forcedName = null,
        bool isAnonymousRecord = false)
    {
        var name = forcedName ?? cursor.Spelling.ToString();
        if (string.IsNullOrEmpty(name)) return;
        if (db.Structs.ContainsKey(name)) return;

        var sizeBytes = cursor.Type.SizeOf;
        if (sizeBytes <= 0 && !HasFieldDeclarations(cursor))
            return; // incomplete/forward declaration or error-recovery stub (size==0)

        var typeInfo = new TypeInfo
        {
            Name = name,
            TotalSize = sizeBytes > 0 ? (int)sizeBytes : 0,
            IsUnion = cursor.Kind == CXCursorKind.CXCursor_UnionDecl,
            IsAnonymousRecord = isAnonymousRecord
        };

        var children = new List<CXCursor>();

        unsafe
        {
            var state = (children, db, unresolved, sourceLines);
            var gcHandle = GCHandle.Alloc(state);
            try
            {
                cursor.VisitChildren(new CXCursorVisitor((child, parent, clientData) =>
                {
                    var s = (ValueTuple<List<CXCursor>, TypeDatabase, List<string>, string[]>)
                        GCHandle.FromIntPtr((IntPtr)clientData).Target!;

                    s.Item1.Add(child);

                    if (IsNamedRecordDeclaration(child))
                    {
                        CollectStruct(child, s.Item2, s.Item3, s.Item4);
                    }

                    return CXChildVisitResult.CXChildVisit_Continue;
                }), new CXClientData(GCHandle.ToIntPtr(gcHandle)));
            }
            finally
            {
                gcHandle.Free();
            }
        }

        int anonymousOrdinal = 0;
        var processedAnonymousRecords = new HashSet<string>(StringComparer.Ordinal);

        for (int index = 0; index < children.Count; index++)
        {
            var child = children[index];

            if (child.Kind == CXCursorKind.CXCursor_FieldDecl)
            {
                if (IsAnonymousCarrierField(child))
                {
                    var anonymousMember = TryBuildAnonymousMemberFromCarrierField(
                        child,
                        db,
                        unresolved,
                        sourceLines,
                        name,
                        ref anonymousOrdinal);

                    if (anonymousMember is { } resolvedAnonymousMember)
                    {
                        typeInfo.Members.Add(resolvedAnonymousMember.Member);
                        processedAnonymousRecords.Add(resolvedAnonymousMember.RecordKey);
                        continue;
                    }
                }

                var member = BuildMember(child, db, unresolved, name, sourceLines);
                if (member != null)
                    typeInfo.Members.Add(member);
                continue;
            }

            if (child.Kind is not CXCursorKind.CXCursor_StructDecl and not CXCursorKind.CXCursor_UnionDecl)
                continue;

            if (!IsAnonymousRecordDeclaration(child))
                continue;

            var recordKey = GetCursorKey(child);
            if (processedAnonymousRecords.Contains(recordKey))
                continue;

            var syntheticTypeName = CreateAnonymousTypeName(name, child.Kind, anonymousOrdinal);
            CollectStruct(child, db, unresolved, sourceLines, syntheticTypeName, isAnonymousRecord: true);
            if (!db.Structs.TryGetValue(syntheticTypeName, out var anonymousType))
                continue;

            typeInfo.Members.Add(BuildAnonymousMember(name, child.Kind, anonymousOrdinal, anonymousType, child.Type.SizeOf));
            processedAnonymousRecords.Add(recordKey);
            anonymousOrdinal++;
        }

        db.Structs[name] = typeInfo;
    }

    private static bool HasFieldDeclarations(CXCursor cursor)
    {
        bool hasFields = false;
        unsafe
        {
            cursor.VisitChildren(new CXCursorVisitor((child, _, _) =>
            {
                if (child.Kind == CXCursorKind.CXCursor_FieldDecl
                    || IsAnonymousRecordDeclaration(child))
                {
                    hasFields = true;
                    return CXChildVisitResult.CXChildVisit_Break;
                }

                return CXChildVisitResult.CXChildVisit_Continue;
            }), new CXClientData(IntPtr.Zero));
        }

        return hasFields;
    }

    private static bool IsNamedRecordDeclaration(CXCursor cursor)
    {
        if (cursor.Kind is not CXCursorKind.CXCursor_StructDecl and not CXCursorKind.CXCursor_UnionDecl)
            return false;

        return !IsAnonymousRecordSpelling(cursor.Spelling.ToString());
    }

    private static bool IsAnonymousRecordDeclaration(CXCursor cursor)
    {
        if (cursor.Kind is not CXCursorKind.CXCursor_StructDecl and not CXCursorKind.CXCursor_UnionDecl)
            return false;

        return IsAnonymousRecordSpelling(cursor.Spelling.ToString()) && HasFieldDeclarations(cursor);
    }

    private static bool IsAnonymousCarrierField(CXCursor cursor)
    {
        if (cursor.Kind != CXCursorKind.CXCursor_FieldDecl)
            return false;

        if (!string.IsNullOrEmpty(cursor.Spelling.ToString()))
            return false;

        return cursor.Type.CanonicalType.kind == CXTypeKind.CXType_Record;
    }

    private static (MemberInfo Member, string RecordKey)? TryBuildAnonymousMemberFromCarrierField(
        CXCursor fieldCursor,
        TypeDatabase db,
        List<string> unresolved,
        string[] sourceLines,
        string parentName,
        ref int anonymousOrdinal)
    {
        var recordCursor = FindAnonymousRecordCursor(fieldCursor);
        if (recordCursor == null)
            return null;

        var syntheticTypeName = CreateAnonymousTypeName(parentName, recordCursor.Value.Kind, anonymousOrdinal);
        CollectStruct(recordCursor.Value, db, unresolved, sourceLines, syntheticTypeName, isAnonymousRecord: true);
        if (!db.Structs.TryGetValue(syntheticTypeName, out var anonymousType))
            return null;

        var member = BuildAnonymousMember(
            parentName,
            recordCursor.Value.Kind,
            anonymousOrdinal,
            anonymousType,
            fieldCursor.Type.SizeOf);

        var bitOffset = clang.Cursor_getOffsetOfField(fieldCursor);
        if (bitOffset >= 0)
            member.Offset = (int)(bitOffset / 8);

        anonymousOrdinal++;
        return (member, GetCursorKey(recordCursor.Value));
    }

    private static CXCursor? FindAnonymousRecordCursor(CXCursor fieldCursor)
    {
        CXCursor? recordCursor = null;

        unsafe
        {
            fieldCursor.VisitChildren(new CXCursorVisitor((child, _, _) =>
            {
                if (child.Kind is CXCursorKind.CXCursor_StructDecl or CXCursorKind.CXCursor_UnionDecl)
                {
                    recordCursor = child;
                    return CXChildVisitResult.CXChildVisit_Break;
                }

                return CXChildVisitResult.CXChildVisit_Continue;
            }), new CXClientData(IntPtr.Zero));
        }

        return recordCursor;
    }

    private static MemberInfo BuildAnonymousMember(
        string parentName,
        CXCursorKind recordKind,
        int ordinal,
        TypeInfo anonymousType,
        long sizeBytes)
    {
        var displayName = GetAnonymousDisplayName(recordKind);

        return new MemberInfo
        {
            Name = CreateAnonymousMemberName(parentName, recordKind, ordinal),
            DisplayName = displayName,
            TypeName = displayName,
            ResolvedType = anonymousType,
            Size = sizeBytes > 0 ? (int)sizeBytes : anonymousType.TotalSize,
            IsAnonymousRecord = true
        };
    }

    private static string CreateAnonymousTypeName(string parentName, CXCursorKind recordKind, int ordinal)
        => $"__anon_{GetAnonymousKindName(recordKind)}_{parentName}_{ordinal}";

    private static string CreateAnonymousMemberName(string parentName, CXCursorKind recordKind, int ordinal)
        => $"__anon_member_{GetAnonymousKindName(recordKind)}_{parentName}_{ordinal}";

    private static string GetAnonymousDisplayName(CXCursorKind recordKind)
        => recordKind == CXCursorKind.CXCursor_UnionDecl ? "(anonymous union)" : "(anonymous struct)";

    private static string GetAnonymousKindName(CXCursorKind recordKind)
        => recordKind == CXCursorKind.CXCursor_UnionDecl ? "union" : "struct";

    private static string GetCursorKey(CXCursor cursor)
    {
        var extent = cursor.Extent;
        extent.Start.GetFileLocation(out _, out uint startLine, out uint startColumn, out _);
        extent.End.GetFileLocation(out _, out uint endLine, out uint endColumn, out _);
        return $"{(int)cursor.Kind}:{startLine}:{startColumn}:{endLine}:{endColumn}";
    }

    private static bool IsAnonymousRecordSpelling(string spelling)
    {
        if (string.IsNullOrWhiteSpace(spelling))
            return true;

        return spelling.StartsWith("(anonymous ", StringComparison.Ordinal);
    }

    private static string ExtractFieldSourceText(CXCursor cursor, string[] sourceLines)
    {
        var extent = cursor.Extent;
        extent.Start.GetFileLocation(out _, out uint startLine, out _, out _);
        extent.End.GetFileLocation(out _, out uint endLine, out _, out _);

        if (startLine == 0 || endLine == 0 || startLine > sourceLines.Length || endLine > sourceLines.Length)
            return string.Empty;

        var parts = new List<string>();
        for (int lineIndex = (int)startLine - 1; lineIndex <= (int)endLine - 1; lineIndex++)
            parts.Add(sourceLines[lineIndex]);

        return NormalizeDeclarationText(string.Join("\n", parts));
    }

    private static unsafe MemberInfo? BuildMember(CXCursor cursor, TypeDatabase db,
        List<string> unresolved, string parentName, string[] sourceLines)
    {
        var memberName = cursor.Spelling.ToString();
        var memberType = cursor.Type;
        var canonical = memberType.CanonicalType;
        var typeSpelling = memberType.Spelling.ToString();
        var displayName = cursor.DisplayName.ToString();

        var member = new MemberInfo { Name = memberName };
        member.ArrayDimExpressions = ExtractArrayDimensionExpressions(typeSpelling);
        if (member.ArrayDimExpressions.Length == 0)
            member.ArrayDimExpressions = ExtractArrayDimensionExpressions(displayName);
        if (member.ArrayDimExpressions.Length == 0)
            member.ArrayDimExpressions = ExtractArrayDimensionExpressions(ExtractFieldSourceText(cursor, sourceLines));

        // Bitfield
        var isBitField = clang.Cursor_isBitField(cursor) != 0;
        member.IsBitField = isBitField;
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
            member.TypeName = GetCleanTypeName(typeSpelling);
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
                // [FIX] typedef-of-array인 경우 elemType.ArrayElementType이 Invalid를 반환.
                // elemType이 실제 배열 타입일 때만 원본 체인에서 추출하고, 아닌 경우 canonical에서 fallback.
                var canonElem = elemCanonical.ArrayElementType;
                elemType = elemType.kind == CXTypeKind.CXType_ConstantArray
                    ? elemType.ArrayElementType
                    : canonElem;
                elemCanonical = canonElem;
            }
            else if (elemCanonical.kind == CXTypeKind.CXType_VariableArray
                  || elemCanonical.kind == CXTypeKind.CXType_DependentSizedArray)
            {
                var canonElem = elemCanonical.ArrayElementType;
                elemType = (elemType.kind == CXTypeKind.CXType_VariableArray
                         || elemType.kind == CXTypeKind.CXType_DependentSizedArray)
                    ? elemType.ArrayElementType
                    : canonElem;
                elemCanonical = canonElem;
            }
            else
            {
                break;
            }
        }

        if (member.ArrayDimExpressions.Length > dims.Count)
        {
            if (TryResolveArrayDimensions(member.ArrayDimExpressions.Skip(dims.Count), db, out var resolvedDims, out var unresolvedExpression))
                dims.AddRange(resolvedDims);
            else if (!string.IsNullOrEmpty(unresolvedExpression))
            {
                member.UnresolvedArrayBoundExpression = unresolvedExpression;
                unresolved.Add(FormatArrayBoundFailure(parentName, memberName, unresolvedExpression));
            }
        }

        member.ArrayDims = dims.ToArray();
        member.ArrayCount = dims.Count > 0 ? dims.Aggregate(1, (a, b) => a * b) : 1;

        ResolveElementType(member, elemType, db, unresolved, parentName);
        UpdateResolvedArraySize(member);
        return member;
    }

    private static void ResolveElementType(MemberInfo member, CXType elemType,
        TypeDatabase db, List<string> unresolved, string parentName)
    {
        var canonElem = elemType.CanonicalType;
        var spelling = GetCleanTypeName(elemType.Spelling.ToString());
        var resolvedAlias = db.ResolveTypeAlias(spelling);

        // Enum?
        if (db.ResolveEnum(spelling) is { } enumInfo)
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
        // 단, spelling이 사용자 정의 타입 이름(비-primitive)인 경우에는 신뢰하지 않음.
        // Clang error recovery가 미선언 struct를 int로 대체했을 수 있으므로,
        // spelling이 primitive가 아니면 Unresolved로 남겨 PostProcessSizes에서 해소.
        bool spellingIsPrimitive = string.IsNullOrEmpty(spelling)
            || TypeDatabase.GetPrimitive(spelling) != PrimitiveKind.None
            || TypeDatabase.GetPrimitive(resolvedAlias) != PrimitiveKind.None;

        var prim = CanonicalKindToPrimitive(canonElem.kind);
        if (prim != PrimitiveKind.None && spellingIsPrimitive)
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

    private static string[] ExtractArrayDimensionExpressions(string typeSpelling)
    {
        var declaration = NormalizeDeclarationText(typeSpelling);
        return Regex.Matches(declaration, @"\[(?<expr>[^\[\]]+)\]")
            .Cast<Match>()
            .Select(m => m.Groups["expr"].Value.Trim())
            .Where(expr => expr.Length > 0)
            .ToArray();
    }

    private static bool TryResolveArrayDimensions(IEnumerable<string> expressions, TypeDatabase db, out List<int> dims, out string unresolvedExpression)
    {
        dims = new List<int>();
        unresolvedExpression = string.Empty;

        foreach (var expression in expressions)
        {
            if (!db.TryResolveConstant(expression, out var value) || value <= 0 || value > int.MaxValue)
            {
                unresolvedExpression = expression;
                return false;
            }

            dims.Add((int)value);
        }

        return true;
    }

    private static void UpdateResolvedArraySize(MemberInfo member)
    {
        if (member.ArrayDims.Length == 0)
            return;

        int elementSize = GetMemberElementSize(member);
        if (elementSize > 0)
            member.Size = elementSize * member.ArrayCount;
    }

    private static int GetMemberElementSize(MemberInfo member)
    {
        if (member.IsPointer)
            return 8;

        if (member.ResolvedType != null)
            return member.ResolvedType.TotalSize;

        if (member.ResolvedEnum != null)
            return 4;

        if (member.Primitive != PrimitiveKind.None)
            return TypeDatabase.GetPrimitiveSize(member.Primitive);

        return member.ArrayCount > 0 && member.Size > 0 ? member.Size / member.ArrayCount : member.Size;
    }

    private static string FormatArrayBoundFailure(string parentName, string memberName, string expression)
    {
        var memberPath = string.IsNullOrEmpty(parentName)
            ? memberName
            : $"{parentName}.{memberName}";
        return $"{memberPath} -> unresolved array bound '{expression}'";
    }

    private static void CollectDefinesFromText(string content, TypeDatabase db)
    {
        foreach (var define in ExtractDefines(content))
        {
            db.DefineExpressions[define.Key] = define.Value;
            if (db.TryResolveConstant(define.Value, out var value))
                db.Defines[define.Key] = value;
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> ExtractDefines(string content)
    {
        var stripped = StripCommentsPreservingLiterals(content);
        var lines = stripped.Replace("\r", string.Empty).Split('\n');
        var current = new List<string>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0)
                continue;

            if (line.EndsWith("\\", StringComparison.Ordinal))
            {
                current.Add(line[..^1].TrimEnd());
                continue;
            }

            current.Add(line);
            var merged = string.Join(" ", current).Trim();
            current.Clear();

            var match = Regex.Match(merged, @"^\s*#define\s+([A-Za-z_]\w*)(?!\s*\()(?<expr>\s+.+)$");
            if (!match.Success)
                continue;

            var name = match.Groups[1].Value;
            var expr = match.Groups["expr"].Value.Trim();
            if (expr.Length > 0)
                yield return new KeyValuePair<string, string>(name, expr);
        }
    }

    private static string NormalizeDeclarationText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var stripped = StripCommentsPreservingLiterals(text);
        var semicolonIndex = stripped.IndexOf(';');
        if (semicolonIndex >= 0)
            stripped = stripped[..semicolonIndex];

        return stripped.Trim();
    }

    private static string StripCommentsPreservingLiterals(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        var state = CommentStripState.Code;
        var escapeNext = false;

        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            var next = index + 1 < text.Length ? text[index + 1] : '\0';

            switch (state)
            {
                case CommentStripState.LineComment:
                    if (current is '\r' or '\n')
                    {
                        state = CommentStripState.Code;
                        builder.Append(current);
                    }
                    continue;

                case CommentStripState.BlockComment:
                    if (current == '*' && next == '/')
                    {
                        state = CommentStripState.Code;
                        index++;
                        continue;
                    }

                    if (current is '\r' or '\n')
                        builder.Append(current);
                    continue;

                case CommentStripState.StringLiteral:
                    builder.Append(current);
                    if (escapeNext)
                    {
                        escapeNext = false;
                    }
                    else if (current == '\\')
                    {
                        escapeNext = true;
                    }
                    else if (current == '"')
                    {
                        state = CommentStripState.Code;
                    }
                    continue;

                case CommentStripState.CharLiteral:
                    builder.Append(current);
                    if (escapeNext)
                    {
                        escapeNext = false;
                    }
                    else if (current == '\\')
                    {
                        escapeNext = true;
                    }
                    else if (current == '\'')
                    {
                        state = CommentStripState.Code;
                    }
                    continue;
            }

            if (current == '/' && next == '/')
            {
                AppendSpaceIfNeeded(builder);
                state = CommentStripState.LineComment;
                index++;
                continue;
            }

            if (current == '/' && next == '*')
            {
                AppendSpaceIfNeeded(builder);
                state = CommentStripState.BlockComment;
                index++;
                continue;
            }

            builder.Append(current);
            if (current == '"')
            {
                state = CommentStripState.StringLiteral;
            }
            else if (current == '\'')
            {
                state = CommentStripState.CharLiteral;
            }
        }

        return builder.ToString();
    }

    private static void AppendSpaceIfNeeded(StringBuilder builder)
    {
        if (builder.Length == 0)
            return;

        var last = builder[^1];
        if (!char.IsWhiteSpace(last))
            builder.Append(' ');
    }

    private enum CommentStripState
    {
        Code,
        LineComment,
        BlockComment,
        StringLiteral,
        CharLiteral
    }

    // Post-processing: resolve references, then rebuild layout with the app's x64 rules.
    private static void PostProcessSizes(TypeDatabase db)
    {
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

        var layoutCalculator = new RecordLayoutCalculator(db);
        layoutCalculator.Apply();
    }
}
