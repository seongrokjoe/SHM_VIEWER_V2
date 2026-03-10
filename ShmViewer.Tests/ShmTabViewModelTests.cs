using System.IO;
using System.IO.MemoryMappedFiles;
using ShmViewer.Core.Mapper;
using ShmViewer.Core.Model;
using ShmViewer.Core.Parser;
using ShmViewer.ViewModels;
using Xunit;

namespace ShmViewer.Tests;

public sealed class ShmTabViewModelTests
{
    [Fact]
    public async Task Run_PreservesExistingTreeAndExpandedState()
    {
        const string header = """
            typedef struct Nested {
                int inner;
            } Nested;

            typedef struct Root {
                int value;
                Nested nested;
            } Root;
            """;

        var result = ParseHeader(header);
        var rootType = AssertType(result.Database, "Root");
        var nestedMember = Assert.Single(rootType.Members, member => member.Name == "nested");
        var innerMember = Assert.Single(nestedMember.ResolvedType!.Members, member => member.Name == "inner");
        var valueMember = Assert.Single(rootType.Members, member => member.Name == "value");

        var tab = CreateTab(result.Database, rootType);
        tab.BuildTree();

        var rootNode = Assert.Single(tab.RootNodes);
        var nestedNode = Assert.Single(rootNode.Children, child => child.Name == "nested");
        nestedNode.IsExpanded = true;
        nestedNode.ExpandLoad();

        var snapshot = new byte[rootType.TotalSize];
        BitConverter.GetBytes(7).CopyTo(snapshot, valueMember.Offset);
        BitConverter.GetBytes(42).CopyTo(snapshot, nestedMember.Offset + innerMember.Offset);

        using var shm = CreateSharedMemory(tab.ShmName, snapshot);

        await tab.Run();

        var currentRoot = Assert.Single(tab.RootNodes);
        var currentNested = Assert.Single(currentRoot.Children, child => child.Name == "nested");
        var innerNode = Assert.Single(currentNested.Children, child => child.Name == "inner");

        Assert.Same(rootNode, currentRoot);
        Assert.Same(nestedNode, currentNested);
        Assert.True(currentNested.IsExpanded);
        Assert.False(currentNested.IsLazy);
        Assert.Equal("7", Assert.Single(currentRoot.Children, child => child.Name == "value").Value);
        Assert.Equal("42", innerNode.Value);
    }

    [Fact]
    public async Task StopThenRun_ReusesExistingTreeAndAppliesLatestSnapshot()
    {
        const string header = """
            typedef struct Nested {
                int inner;
            } Nested;

            typedef struct Root {
                Nested nested;
            } Root;
            """;

        var result = ParseHeader(header);
        var rootType = AssertType(result.Database, "Root");
        var nestedMember = Assert.Single(rootType.Members, member => member.Name == "nested");
        var innerMember = Assert.Single(nestedMember.ResolvedType!.Members, member => member.Name == "inner");

        var tab = CreateTab(result.Database, rootType);
        tab.BuildTree();

        var rootNode = Assert.Single(tab.RootNodes);
        var nestedNode = Assert.Single(rootNode.Children, child => child.Name == "nested");
        nestedNode.IsExpanded = true;
        nestedNode.ExpandLoad();
        var innerNode = Assert.Single(nestedNode.Children, child => child.Name == "inner");

        using var shm = CreateSharedMemory(tab.ShmName, CreateSnapshot(rootType.TotalSize, (nestedMember.Offset + innerMember.Offset, 11)));

        await tab.Run();
        Assert.Equal("11", innerNode.Value);

        tab.Stop();
        WriteSnapshot(shm, CreateSnapshot(rootType.TotalSize, (nestedMember.Offset + innerMember.Offset, 99)));

        await tab.Run();

        var currentRoot = Assert.Single(tab.RootNodes);
        var currentNested = Assert.Single(currentRoot.Children, child => child.Name == "nested");
        var currentInner = Assert.Single(currentNested.Children, child => child.Name == "inner");

        Assert.Same(rootNode, currentRoot);
        Assert.Same(nestedNode, currentNested);
        Assert.Same(innerNode, currentInner);
        Assert.True(currentNested.IsExpanded);
        Assert.Equal("99", currentInner.Value);
    }

    [Fact]
    public void RefreshValues_UpdatesPendingLazyDataBeforeExpansion()
    {
        const string header = """
            typedef struct Nested {
                int inner;
            } Nested;

            typedef struct Root {
                Nested nested;
            } Root;
            """;

        var result = ParseHeader(header);
        var rootType = AssertType(result.Database, "Root");
        var nestedMember = Assert.Single(rootType.Members, member => member.Name == "nested");
        var innerMember = Assert.Single(nestedMember.ResolvedType!.Members, member => member.Name == "inner");
        var mapper = new DataMapper(result.Database);

        var rootNode = mapper.MapEmpty(rootType);
        var nestedNode = Assert.Single(rootNode.Children, child => child.Name == "nested");
        Assert.True(nestedNode.IsLazy);

        var snapshot = CreateSnapshot(rootType.TotalSize, (nestedMember.Offset + innerMember.Offset, 77));

        mapper.RefreshValues(rootNode, snapshot);
        nestedNode.ExpandLoad();

        var innerNode = Assert.Single(nestedNode.Children, child => child.Name == "inner");
        Assert.Equal("77", innerNode.Value);
    }

    private static ShmTabViewModel CreateTab(TypeDatabase database, TypeInfo rootType)
    {
        var tab = new ShmTabViewModel
        {
            ShmName = $"shm_viewer_test_{Guid.NewGuid():N}",
            StructName = rootType.Name,
            RefreshMode = RefreshMode.Manual
        };

        tab.Initialize(database, rootType);
        return tab;
    }

    private static MemoryMappedFile CreateSharedMemory(string shmName, byte[] data)
    {
        var mmf = MemoryMappedFile.CreateOrOpen(shmName, data.Length);
        WriteSnapshot(mmf, data);
        return mmf;
    }

    private static void WriteSnapshot(MemoryMappedFile mmf, byte[] data)
    {
        using var accessor = mmf.CreateViewAccessor(0, data.Length, MemoryMappedFileAccess.ReadWrite);
        accessor.WriteArray(0, data, 0, data.Length);
        accessor.Flush();
    }

    private static byte[] CreateSnapshot(int size, params (int offset, int value)[] writes)
    {
        var data = new byte[size];
        foreach (var (offset, value) in writes)
            BitConverter.GetBytes(value).CopyTo(data, offset);
        return data;
    }

    private static ParseResult ParseHeader(string headerContent)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ShmViewer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var headerPath = Path.Combine(tempDir, "tab.h");
        File.WriteAllText(headerPath, headerContent);

        try
        {
            var parser = new HeaderParserService();
            return parser.Parse(new[] { headerPath });
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private static TypeInfo AssertType(TypeDatabase db, string name)
    {
        Assert.True(db.Structs.TryGetValue(name, out var typeInfo), $"Type '{name}' was not parsed.");
        return typeInfo!;
    }
}
