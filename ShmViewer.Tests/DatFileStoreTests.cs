using System.IO;
using System.Text.Json.Nodes;
using ShmViewer.Core.Dat;
using Xunit;
using static ShmViewer.Tests.DatTestData;
using static ShmViewer.Core.Dat.DatSourceVersion;

namespace ShmViewer.Tests;

public sealed class DatFileStoreTests
{
    [Fact]
    public void SavingChangedStructureBacksUpExactOriginalAndReloadsWithoutHeaders()
    {
        using var files = new Files();
        var a = files.Write("a.dat", Document("struct Root { int a; int b; };", ("a", "7"), ("b", "9")));
        var b = files.Write("b.dat", Document("struct Root { int a; };", ("a", "2")));
        var session = new DatDiffSession(a, b);
        session.Apply(new[] { "b" }, Version1);
        var saved = session.Save(Version2);
        Assert.Equal(b.Data, File.ReadAllBytes(saved.BackupPath)); Assert.Equal(4, saved.PreviousSize); Assert.Equal(8, saved.Size);
        Assert.True(saved.StructureChanged); Assert.False(session.Right.IsDirty);
        Assert.Equal(session.Right.Current.Data, File.ReadAllBytes(b.FilePath));
        var loaded = new DatFileStore().Load(b.FilePath, () => throw new Exception("Headers must not be read"));
        Assert.True(loaded.LoadedFromSchema);
        Assert.Equal(session.Right.Current.Schema.Root.Signature, loaded.Schema.Root.Signature);
        Assert.Equal(session.Right.Current.Data, loaded.Data);
        Assert.Contains("기존 매핑으로 Load할 수 없습니다", saved.Message("버전2"));
        Assert.Contains(saved.SchemaPath, saved.Message("버전2"));
        Assert.Empty(Directory.GetFiles(files.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public void RepeatSaveUsesUniqueBackupsAndBacksUpMetadataPair()
    {
        using var files = new Files();
        var a = files.Write("a.dat", Document("struct Root { int a; };"));
        var store = new DatFileStore { LocalNow = () => new(2026, 10, 14, 15, 30, 0) };
        var session = new DatDiffSession(a, a);
        session.SetValue(Version1, "a", "1", "ASCII"); var first = session.Save(Version1, store);
        var metadata = File.ReadAllBytes(first.SchemaPath); var bytes = File.ReadAllBytes(first.DatPath);
        session.SetValue(Version1, "a", "2", "ASCII"); var second = session.Save(Version1, store);
        Assert.NotEqual(first.BackupPath, second.BackupPath);
        Assert.Equal(bytes, File.ReadAllBytes(second.BackupPath));
        Assert.Equal(metadata, File.ReadAllBytes(second.BackupSchemaPath!));
        var backup = store.Load(second.BackupPath, () => throw new Exception());
        Assert.Equal(bytes, backup.Data);
        Assert.Equal(a.Data, File.ReadAllBytes(first.BackupPath));
    }

    [Theory]
    [InlineData("backup", false)]
    [InlineData("replace-data", false)]
    [InlineData("replace-schema", false)]
    [InlineData("verify", false)]
    [InlineData("replace-schema", true)]
    [InlineData("verify", true)]
    public void FailureRetainsOriginalPairAndDirtyState(string stage, bool existingMetadata)
    {
        using var files = new Files();
        var doc = files.Write("data.dat", Document("struct Root { int a; };"));
        var session = new DatDiffSession(doc, doc);
        if (existingMetadata) session.Save(Version1);
        var before = File.ReadAllBytes(doc.FilePath);
        var schemaPath = DatSchemaFile.PathFor(doc.FilePath);
        var metadata = File.Exists(schemaPath) ? File.ReadAllBytes(schemaPath) : null;
        session.SetValue(Version1, "a", "33", "ASCII");
        var store = new DatFileStore { BeforeStage = current => { if (current == stage) throw new IOException("simulated failure"); } };
        Assert.Throws<IOException>(() => session.Save(Version1, store));
        Assert.Equal(before, File.ReadAllBytes(doc.FilePath)); Assert.True(session.Left.IsDirty);
        if (metadata == null) Assert.False(File.Exists(schemaPath));
        else Assert.Equal(metadata, File.ReadAllBytes(schemaPath));
        Assert.Empty(Directory.GetFiles(files.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public void ExternalEditsAndSamePathOtherSideAreNotOverwritten()
    {
        using var files = new Files();
        var doc = files.Write("data.dat", Document("struct Root { int a; };"));
        var session = new DatDiffSession(doc, doc);
        session.SetValue(Version1, "a", "1", "ASCII"); session.Save(Version1);
        session.SetValue(Version2, "a", "2", "ASCII");
        Assert.Throws<IOException>(() => session.Save(Version2));
        Assert.Equal(BitConverter.GetBytes(1), File.ReadAllBytes(doc.FilePath));
        File.WriteAllBytes(doc.FilePath, BitConverter.GetBytes(8));
        Assert.Throws<IOException>(() => session.Save(Version1));
        Assert.Equal(BitConverter.GetBytes(8), File.ReadAllBytes(doc.FilePath));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("offset")]
    [InlineData("array")]
    [InlineData("json")]
    public void InvalidMetadataDoesNotSilentlyFallback(string kind)
    {
        using var files = new Files();
        var doc = files.Write("data.dat", Document("struct Root { int a[2]; };"));
        var json = JsonNode.Parse(DatSchemaFile.Serialize(doc.Schema, doc.Data))!;
        if (kind == "version") json["FormatVersion"] = 99;
        if (kind == "hash") json["DataSha256"] = "invalid";
        if (kind == "offset") json["Root"]!["Members"]![0]!["Offset"] = 999;
        if (kind == "array") json["Root"]!["Members"]![0]!["Type"]!["Count"] = int.MaxValue;
        File.WriteAllText(DatSchemaFile.PathFor(doc.FilePath), kind == "json" ? "invalid json" : json.ToJsonString());
        var parsed = false;
        Assert.Throws<InvalidDataException>(() => new DatFileStore().Load(doc.FilePath, () => { parsed = true; return doc.Schema; }));
        Assert.False(parsed);
        var forced = new DatFileStore().Load(doc.FilePath, () => doc.Schema, useHeader: true);
        Assert.False(forced.LoadedFromSchema);
    }

    [Fact]
    public void MetadataRoundtripPreservesEnum64BitfieldsPackAndAnonymousUnion()
    {
        var doc = Document("""
            #pragma pack(push, 1)
            enum E : unsigned long long { Max=18446744073709551615ULL };
            struct __declspec(align(8)) Root { E e; unsigned int a:3; unsigned int :5; unsigned int b:8; union { int x; float y; }; short matrix[2][3]; };
            #pragma pack(pop)
            """, ("e", "18446744073709551615"), ("a", "7"), ("matrix[1][2]", "-9"));
        var schema = DatSchemaFile.Read(DatSchemaFile.Serialize(doc.Schema, doc.Data), doc.Data);
        Assert.Equal(DatSchemaFile.SchemaKey(doc.Schema), DatSchemaFile.SchemaKey(schema));
    }

    [Fact]
    public void SameSizeStructureChangesAndValuesOnlyHaveAccurateNotices()
    {
        using var files = new Files();
        var left = files.Write("a.dat", Document("struct Root { float x; };", ("x", "1.5")));
        var right = files.Write("b.dat", Document("struct Root { int x; };"));
        var session = new DatDiffSession(left, right);
        session.Apply(new[] { "x" }, Version1); var changed = session.Save(Version2);
        Assert.Equal(changed.PreviousSize, changed.Size); Assert.True(changed.StructureChanged);
        Assert.Contains("크기는 같지만 구조가 변경", changed.Message("버전2"));
        session.SetValue(Version2, "x", "2.5", "ASCII"); var value = session.Save(Version2);
        Assert.False(value.StructureChanged); Assert.DoesNotContain("매핑을 사용할 수 없습니다", value.Message("버전2"));
        Assert.Contains("자동으로 읽습니다", value.Message("버전2"));
    }

    [Fact]
    public void LoadOnlySaveBacksUpAndUndoRestoresDirtyState()
    {
        using var files = new Files();
        var doc = files.Write("a.dat", Document("struct Root { unsigned int a:3; unsigned int b:5; };", ("b", "17")));
        var session = new DatEditSession(doc); var field = doc.Root.Children().First();
        session.SetValue(field, "5", "ASCII"); var result = session.Save();
        Assert.Equal(doc.Data, File.ReadAllBytes(result.BackupPath)); Assert.False(session.IsDirty);
        Assert.Equal("17", BinaryValueCodec.Format(session.Data, doc.Root.Children().Last()));
        session.Undo(); Assert.True(session.IsDirty); session.Redo(); Assert.False(session.IsDirty);
    }

    [Theory]
    [InlineData("version1", "DemoV1", "version2", "DemoV2")]
    [InlineData("version2", "DemoV2", "version1", "DemoV1")]
    [InlineData("structure_v1", "Root", "structure_v2", "Root")]
    [InlineData("structure_v2", "Root", "structure_v1", "Root")]
    public void RepositorySamplesSaveWholeSourceAndReloadWithExactBytes(string sourceName, string sourceStruct, string targetName, string targetStruct)
    {
        using var files = new Files();
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../TestHeaders/DatDiff"));
        DatDocument Load(string name, string structName)
        {
            var header = Path.Combine(directory, name + ".h");
            var schema = new ShmViewer.Core.Parser.HeaderParserService().ParseBinary(new[] { header }, structName, new() { Pack = 8 });
            return files.Write(name + ".dat", DatDocument.Load(Path.Combine(directory, name + ".dat"), schema));
        }
        var source = Load(sourceName, sourceStruct); var target = Load(targetName, targetStruct);
        var session = new DatDiffSession(source, target);
        session.Apply(new[] { "" }, Version1);
        var saved = session.Save(Version2);
        Assert.Equal(source.Data, File.ReadAllBytes(saved.DatPath));
        Assert.Equal(target.Data, File.ReadAllBytes(saved.BackupPath));
        Assert.Equal(source.Data, new DatFileStore().Load(saved.DatPath, () => throw new Exception()).Data);
        Assert.Equal(target.Data.Length, saved.PreviousSize); Assert.Equal(source.Data.Length, saved.Size);
        Assert.Contains("크기가 변경", saved.Message("버전2"));
    }
}
