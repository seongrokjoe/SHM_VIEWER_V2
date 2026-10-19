using System.IO;
using ShmViewer.Core.Dat;
using Xunit;

namespace ShmViewer.Tests;

public sealed class DatSingleStructureTests
{
    internal const string Header = "struct Item { short a; int b; }; struct Root { int first; Item items[3]; long tail; };";
    internal static DatEditSession Session() => new(DatTestData.Document(Header, ("first", "10"), ("items[0].a", "11"),
        ("items[1].a", "21"), ("items[2].a", "31"), ("items[1].b", "987"), ("tail", "99")));
    private static BinaryType Type(string name) => new DatTypeCatalog().Resolve(name);
    private static string Value(DatEditSession session, string path) => session.Value(session.Find(path)!.Id, "ASCII");

    [Fact]
    public void InsertRenameDeleteAndRestoreRecalculatesOffsetsAndKeepsValues()
    {
        var session = Session(); var first = session.Find("first")!;
        var plan = session.PrepareInsert(first.Id, false, "middle", Type("short")); session.Commit(plan);
        var added = session.Find("middle")!;
        Assert.Equal(4, added.Field.Offset); Assert.Equal("0", Value(session, "middle")); Assert.Equal("99", Value(session, "tail"));
        session.Commit(session.PrepareRename(added.Id, "renamed")); Assert.Equal(added.Id, session.Find("renamed")!.Id);
        session.Commit(session.PrepareDelete(added.Id)); Assert.True(session.Entries[added.Id].IsDeleted); Assert.Null(session.Find("renamed"));
        session.Commit(session.PrepareRestore(added.Id)); Assert.Equal(4, session.Find("renamed")!.Field.Offset);
        session.Undo(); Assert.True(session.Entries[added.Id].IsDeleted);
        session.Redo(); Assert.False(session.Entries[added.Id].IsDeleted);
    }

    [Fact]
    public void ArrayMemberEditPropagatesWithoutTouchingOtherValuesAndRestoresNewElementsAsZero()
    {
        var session = Session(); var initialSize = session.Bytes.Length;
        var plan = session.PrepareInsert(session.Find("items[0].a")!.Id, false, "extra", Type("long long"));
        Assert.Equal(3, plan.AffectedCount); Assert.True(plan.RequiresConfirmation); session.Commit(plan);
        for (var i = 0; i < 3; i++) Assert.Equal("0", Value(session, $"items[{i}].extra"));
        Assert.True(session.Bytes.Length > initialSize); Assert.Equal("987", Value(session, "items[1].b")); Assert.Equal("99", Value(session, "tail"));
        var id = session.Find("items[1].extra")!.Id;
        session.SetValue(session.Find("items[1].extra")!.Field, "123", "ASCII");
        session.Commit(session.PrepareDelete(id));
        session.Commit(session.PrepareInsert(session.Find("items[0]")!.Id, false, "", Type("int")));
        session.Commit(session.PrepareRestore(id));
        Assert.Equal("123", Value(session, "items[2].extra")); Assert.Equal("0", Value(session, "items[1].extra"));
        Assert.Equal("987", Value(session, "items[2].b"));
    }

    [Fact]
    public void DeletedArrayElementKeepsIdentityAcrossInsertionAndRename()
    {
        var session = Session(); var element = session.Find("items[1]")!;
        session.Commit(session.PrepareDelete(element.Id)); Assert.Equal("31", Value(session, "items[1].a"));
        session.Commit(session.PrepareRename(session.Find("items")!.Id, "records"));
        session.Commit(session.PrepareInsert(session.Find("records[0]")!.Id, false, "", Type("int")));
        session.Commit(session.PrepareRestore(element.Id));
        Assert.Equal(element.Id, session.Find("records[2]")!.Id);
        Assert.Equal("987", Value(session, "records[2].b")); Assert.Equal("31", Value(session, "records[3].a"));
    }

    [Fact]
    public void TypeChangesPreserveSafeConversionsAndPreviewLosses()
    {
        var session = Session(); var id = session.Find("items[0].b")!.Id;
        var plan = session.PrepareType(id, Type("unsigned char"), "ASCII");
        Assert.Equal(3, plan.AffectedCount); Assert.Equal(1, plan.LossCount);
        Assert.Equal("987", Value(session, "items[1].b")); session.Commit(plan);
        Assert.Equal("0", Value(session, "items[1].b")); Assert.Equal("99", Value(session, "tail"));
        session.Undo(); Assert.Equal("987", Value(session, "items[1].b"));
        session.Commit(session.PrepareType(id, Type("long long"), "ASCII")); Assert.Equal("987", Value(session, "items[1].b"));
    }

    [Fact]
    public void NestedArrayElementChangesKeepRectangularShape()
    {
        var session = new DatEditSession(DatTestData.Document("struct Root { int matrix[2][3]; int last; };", ("matrix[1][2]", "72"), ("last", "9")));
        var id = session.Find("matrix[0][1]")!.Id;
        var plan = session.PrepareDelete(id); Assert.Equal(2, plan.AffectedCount); session.Commit(plan);
        Assert.Equal("72", Value(session, "matrix[1][1]"));
        session.Commit(session.PrepareRestore(id)); Assert.Equal("72", Value(session, "matrix[1][2]"));
        Assert.Equal("9", Value(session, "last"));
    }

    [Fact]
    public void InvalidNamesConflictingRestoreAndStalePlansDoNotMutateSession()
    {
        var session = Session(); var id = session.Find("first")!.Id;
        Assert.Throws<InvalidDataException>(() => session.PrepareRename(id, "tail"));
        Assert.Throws<InvalidDataException>(() => session.PrepareRename(id, "bad.name"));
        var stale = session.PrepareRename(id, "renamed"); session.Commit(session.PrepareDelete(id));
        Assert.Throws<InvalidOperationException>(() => session.Commit(stale));
        session.Commit(session.PrepareInsert(session.RootId, true, "first", Type("int")));
        Assert.Throws<InvalidDataException>(() => session.PrepareRestore(id));
        session.Commit(session.PrepareRestore(id, "restored")); Assert.Equal("10", Value(session, "restored"));
    }

    [Fact]
    public void ArrayLengthChangesKeepDeletedRowsAndSchemaRoundtrips()
    {
        var session = Session(); var catalog = new DatTypeCatalog(); catalog.SetDocument(session.Document);
        var type = catalog.Resolve("Item[2]"); var removed = session.Find("items[2]")!.Id;
        session.Commit(session.PrepareType(session.Find("items")!.Id, type, "ASCII"));
        Assert.True(session.Entries[removed].IsDeleted); session.Commit(session.PrepareRestore(removed));
        var schema = DatSchemaFile.Read(DatSchemaFile.Serialize(session.Document.Schema, session.Data), session.Data);
        Assert.Equal(session.Document.Schema.Root.Size, schema.Root.Size); Assert.Equal("31", Value(session, "items[2].a"));
    }

    [Fact]
    public void GrowingAnArrayThenEditingItsMemberAlsoChangesNewElements()
    {
        var session = Session(); var catalog = new DatTypeCatalog(); catalog.SetDocument(session.Document);
        session.Commit(session.PrepareType(session.Find("items")!.Id, catalog.Resolve("Item[5]"), "ASCII"));
        var plan = session.PrepareRename(session.Find("items[0].a")!.Id, "code");
        Assert.Equal(5, plan.AffectedCount); session.Commit(plan);
        Assert.Equal("0", Value(session, "items[4].code")); Assert.Equal("31", Value(session, "items[2].code"));
        session.Commit(session.PrepareDelete(session.Find("items[4].b")!.Id));
        Assert.Null(session.Find("items[0].b")); Assert.Equal("99", Value(session, "tail"));
    }

    [Fact]
    public void MemberEditsDoNotAffectUnrelatedInstancesOfTheSameNamedType()
    {
        var session = new DatEditSession(DatTestData.Document("struct Item { int a; }; struct Root { Item first[2]; Item second[2]; };", ("second[1].a", "18")));
        session.Commit(session.PrepareRename(session.Find("first[0].a")!.Id, "code"));
        Assert.NotNull(session.Find("first[1].code")); Assert.Equal("18", Value(session, "second[1].a"));
        var catalog = new DatTypeCatalog(); catalog.SetDocument(session.Document);
        Assert.Throws<InvalidDataException>(() => catalog.Resolve("Item"));
        Assert.Equal("code", catalog.Resolve("Item", catalog.Choices.First(c => c.Name == "Item")).Members[0].Name);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    public void EditedLayoutMatchesCompilerIncludingUnchangedBitfields(int pack)
    {
        var header = $"#pragma pack({pack})\nstruct Root {{ char c; unsigned int flags:3; unsigned int other:5; int tail; }};";
        var session = new DatEditSession(DatTestData.Document(header, ("flags", "5"), ("other", "17"), ("tail", "42")));
        Assert.False(session.CanChange(session.Find("flags")!.Id));
        session.Commit(session.PrepareInsert(session.Find("c")!.Id, false, "added", Type("double")));
        var expected = DatBinaryTests.Parse(header.Replace("char c;", "char c; double added;"));
        Assert.True(DatSchemaFile.SameLayout(expected.Root, session.Document.Schema.Root));
        Assert.Equal("5", Value(session, "flags")); Assert.Equal("17", Value(session, "other")); Assert.Equal("42", Value(session, "tail"));
    }

    [Fact]
    public void DeletedParentCanBeRestoredAfterSaveButNotAfterReload()
    {
        using var files = new DatTestData.Files();
        var session = new DatEditSession(files.Write("edit.dat", Session().Document));
        var deleted = session.Find("items")!; var child = session.Find("items[1].b")!;
        session.Commit(session.PrepareDelete(deleted.Id)); session.Save();
        Assert.Equal(child.Field.Offset, session.Entries[child.Id].RemovedOffset);
        Assert.Equal("987", session.Value(child.Id, "ASCII"));
        Assert.Throws<InvalidDataException>(() => session.PrepareRestore(child.Id));
        var schema = DatSchemaFile.Read(File.ReadAllBytes(DatSchemaFile.PathFor(session.Document.FilePath)), File.ReadAllBytes(session.Document.FilePath));
        var reloaded = new DatEditSession(DatDocument.Load(session.Document.FilePath, schema));
        Assert.DoesNotContain(reloaded.Entries.Values, e => e.IsDeleted); Assert.Null(reloaded.Find("items"));
        session.Commit(session.PrepareRestore(deleted.Id)); Assert.True(session.IsDirty); Assert.Equal("987", Value(session, "items[1].b"));
        session.Save(); Assert.False(session.IsDirty);
    }

    [Fact]
    public void RestoreRejectsIncompatibleParentAndLastArrayElementDeletion()
    {
        var session = new DatEditSession(DatTestData.Document("struct Root { int a[2]; int last; };", ("a[1]", "12")));
        var id = session.Find("a[0]")!.Id;
        session.Commit(session.PrepareDelete(id));
        Assert.Throws<InvalidDataException>(() => session.PrepareDelete(session.Find("a[0]")!.Id));
        session.Commit(session.PrepareType(session.Find("a[0]")!.Id, Type("long long"), "ASCII"));
        Assert.Throws<InvalidDataException>(() => session.PrepareRestore(id));
        session.Undo(); session.Commit(session.PrepareRestore(id)); Assert.Equal("12", Value(session, "a[1]"));
    }

    [Fact]
    public void TextConversionPreservesWholeStringOrPreviewsDefaultWithoutTruncation()
    {
        var session = new DatEditSession(DatTestData.Document("struct Root { char text[8]; };", ("text", "\"abcdef\"")));
        var id = session.Find("text")!.Id;
        var safe = session.PrepareType(id, Type("char[20]"), "ASCII"); Assert.Equal(0, safe.LossCount); session.Commit(safe);
        Assert.Contains("abcdef", Value(session, "text"));
        var lossy = session.PrepareType(id, Type("char[3]"), "ASCII"); Assert.Equal(1, lossy.LossCount); Assert.True(lossy.RequiresConfirmation);
        session.Commit(lossy); Assert.Equal("\"\"", Value(session, "text")); session.Undo(); Assert.Contains("abcdef", Value(session, "text"));
    }

    [Fact]
    public void CancelledPreparationAndUnchangedValuesDoNotAddHistory()
    {
        var session = Session(); var first = session.Find("first")!;
        session.Commit(session.PrepareValue(first.Id, "10", "ASCII")); session.Commit(session.PrepareRename(first.Id, "first"));
        Assert.False(session.CanUndo); Assert.False(session.IsDirty);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => session.PrepareInsert(first.Id, false, "extra", Type("int"), cancellation.Token));
        var plan = session.PrepareDelete(first.Id);
        Assert.Throws<OperationCanceledException>(() => session.Commit(plan, cancellation.Token));
        Assert.False(session.CanUndo); Assert.NotNull(session.Find("first"));
    }

    [Fact]
    public void EqualLayoutTypeReplacementUpdatesNestedTypesAndKeepsTheirIdentities()
    {
        var session = new DatEditSession(DatTestData.Document(
            "struct Old { int value; }; struct Root { Old items[2]; };", ("items[1].value", "42")));
        var field = session.Find("items[1].value")!;
        var replacement = DatBinaryTests.Parse("struct Root { long value; };").Root;
        session.Commit(session.PrepareType(session.Find("items[0]")!.Id, replacement, "ASCII"));
        Assert.Equal(field.Id, session.Find("items[1].value")!.Id);
        Assert.Equal("long", session.Find("items[1].value")!.Field.Type.Name);
        Assert.Equal("42", Value(session, "items[1].value"));
        session.Commit(session.PrepareRename(field.Id, "renamed"));
        Assert.Equal("long", session.Document.Schema.Root.Members[0].Type.Element!.Members[0].Type.Name);
        Assert.Equal("42", Value(session, "items[1].renamed"));
    }

    [Fact]
    public void RecordTypeConversionKeepsValuesWhenBitPositionsMove()
    {
        var session = new DatEditSession(DatTestData.Document(
            "struct Flags { unsigned int a:3; unsigned int b:5; }; struct Root { Flags flags[2]; int tail; };",
            ("flags[0].a", "5"), ("flags[0].b", "17"), ("flags[1].b", "25"), ("tail", "123")));
        var replacement = DatBinaryTests.Parse("struct Root { unsigned int b:5; unsigned int a:3; int extra; };").Root;
        var plan = session.PrepareType(session.Find("flags[0]")!.Id, replacement, "ASCII");
        Assert.Equal(0, plan.LossCount); Assert.Equal(2, plan.AffectedCount); session.Commit(plan);
        Assert.Equal("5", Value(session, "flags[0].a")); Assert.Equal("17", Value(session, "flags[0].b"));
        Assert.Equal("25", Value(session, "flags[1].b")); Assert.Equal("0", Value(session, "flags[1].extra"));
        Assert.Equal("123", Value(session, "tail"));
    }

    [Theory]
    [InlineData("single_load", "SingleLoadSample", 3)]
    [InlineData("large_20000", "SingleLoad20000", 20_000)]
    [InlineData("large_100000", "SingleLoad100000", 100_000)]
    public void LocalSamplePairsMatchHeaderLayoutAndContainExpectedValues(string name, string structName, int count)
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../TestHeaders/SingleLoad"));
        var document = new DatFileStore().Load(Path.Combine(directory, name + ".dat"), () => throw new Exception("Must use sidecar"), useSchemaFile: true);
        var schema = new ShmViewer.Core.Parser.HeaderParserService().ParseBinary(new[] { Path.Combine(directory, "single_load.h") }, structName, new() { Pack = 8 });
        Assert.True(DatSchemaFile.SameLayout(schema.Root, document.Schema.Root));
        var array = document.Schema.Root.Members.Single(m => m.Name == "items");
        Assert.Equal(count, array.Type.Count);
        var tail = document.Root.Children().Single(f => f.Path == "tail");
        Assert.Equal("123456", BinaryValueCodec.Format(document.Data, tail));
        if (count > 3)
            Assert.Equal(count, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(document.Bytes.Span.Slice(array.Offset + (count - 1) * array.Type.Element!.Size)));
        else
        {
            var session = new DatEditSession(document);
            Assert.Equal("300", Value(session, "items[2].quantity"));
            Assert.Contains("Single Load Demo", Value(session, "title"));
        }
    }

    [Fact]
    public void ReferenceCatalogFindsUnusedQualifiedTypesAliasesAndRefreshesDefinitions()
    {
        using var files = new DatTestData.Files(); var path = files.PathFor("references.h");
        File.WriteAllText(path, "#pragma pack(push, 1)\nnamespace Extra { struct Info { char c; long value; }; typedef unsigned long Counter; enum class Mode:short { A=1 }; }\n#pragma pack(pop)");
        var catalog = new DatTypeCatalog { Headers = new[] { path } };
        catalog.RefreshHeaders();
        Assert.Contains(catalog.Choices, c => c.Name == "Extra::Info" && c.Type == null);
        Assert.Equal(5, catalog.Resolve("Extra::Info").Size); Assert.Equal(40, catalog.Resolve("Extra::Counter[10]").Size);
        Assert.Equal(BinaryKind.Enum, catalog.Resolve("Extra::Mode").Kind);
        File.WriteAllText(path, "namespace Extra { struct Info { long long value; }; }"); catalog.RefreshHeaders();
        Assert.Equal(8, catalog.Resolve("Extra::Info").Size); Assert.DoesNotContain(catalog.Choices, c => c.Name == "Extra::Counter");
        catalog.Headers = new[] { "missing.h" }; Assert.Equal(40, catalog.Resolve("long[10]").Size);
        Assert.Throws<InvalidDataException>(() => catalog.Resolve("int[0]"));
    }
}
