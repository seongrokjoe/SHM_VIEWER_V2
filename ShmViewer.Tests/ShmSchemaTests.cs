using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json.Nodes;
using ShmViewer.Core;
using ShmViewer.Core.Mapper;
using ShmViewer.Core.Model;
using ShmViewer.Core.Parser;
using ShmViewer.ViewModels;
using Xunit;

namespace ShmViewer.Tests;

public sealed class ShmSchemaTests
{
    internal sealed class MemoryStore : IRealTimeSettingsStore
    {
        public RealTimeSettings State { get; private set; } = new(false, new(), new());
        public RealTimeSettings Load() => State;
        public void Save(RealTimeSettings settings) => State = settings;
    }

    internal sealed class Files : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "ShmViewer.Tests", Guid.NewGuid().ToString("N"));
        public string SchemaPath => Path.Combine(DirectoryPath, "sample.shm.schema.json");
        public string HeaderPath => Path.Combine(DirectoryPath, "sample.h");
        public string ShmName { get; } = "shm_schema_" + Guid.NewGuid().ToString("N");
        public TypeDatabase Database { get; }
        public TypeInfo Root => Database.ResolveType("Root")!;
        public Files(string header = "struct Child { int value; }; struct Root { int value; bool flag; char text[8]; Child nested; };")
        {
            Directory.CreateDirectory(DirectoryPath); File.WriteAllText(HeaderPath, header);
            Database = new HeaderParserService().Parse(new[] { HeaderPath }).Database;
            Assert.NotNull(Root);
            File.WriteAllBytes(SchemaPath, ShmSchemaFile.Serialize(ShmName, "Root", "Manual", Root));
        }
        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }

    [Theory]
    [InlineData("enum E { Off=0, On=7 }; struct Child { E state; }; typedef Child Alias; struct Root { Alias a; Alias b; short matrix[2][3]; void *ptr; char text[8]; bool flag; double value; };")]
    [InlineData("struct Root { union { int x; float y; }; unsigned int a:3; unsigned int :5; unsigned int b:8; int reserved[2]; };")]
    [InlineData("#pragma pack(push, 1)\nstruct Child { char c; int n; }; struct Root { Child a[3]; short b; };\n#pragma pack(pop)")]
    public void RoundtripPreservesLayoutEnumsArraysAnonymousMembersAndMapperValues(string header)
    {
        using var files = new Files(header);
        var loaded = new ShmSchemaStore().Load(files.SchemaPath);
        Assert.Equal(files.Root.TotalSize, loaded.RootType.TotalSize); Assert.Equal(files.Root.Alignment, loaded.RootType.Alignment);
        var bytes = Enumerable.Range(0, files.Root.TotalSize).Select(i => (byte)(i * 7)).ToArray();
        static string[] Describe(TreeNodeViewModel node)
        {
            if (node.IsLazy) node.ExpandLoad();
            return new[] { $"{node.Name}|{node.TypeName}|{node.Offset}|{node.Size}|{node.Value}" }
                .Concat(node.Children.SelectMany(Describe)).ToArray();
        }
        Assert.Equal(Describe(new DataMapper(files.Database).Map(bytes, files.Root)), Describe(new DataMapper(loaded.Database).Map(bytes, loaded.RootType)));
        Assert.Equal(File.ReadAllBytes(files.SchemaPath), ShmSchemaFile.Serialize(loaded.ShmName, loaded.StructName, loaded.RefreshMode, loaded.RootType));
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("version")]
    [InlineData("platform")]
    [InlineData("root")]
    [InlineData("reference")]
    [InlineData("offset")]
    [InlineData("array")]
    [InlineData("cycle")]
    [InlineData("null")]
    [InlineData("json")]
    public void InvalidSchemaIsRejectedBeforeBuildingTree(string corruption)
    {
        using var files = new Files();
        var json = JsonNode.Parse(File.ReadAllBytes(files.SchemaPath))!;
        var member = json["Types"]![0]!["Members"]![0]!;
        if (corruption == "kind") json["FileKind"] = "DAT";
        if (corruption == "version") json["FormatVersion"] = 99;
        if (corruption == "platform") json["Platform"] = "x86";
        if (corruption == "root") json["RootTypeId"] = "missing";
        if (corruption == "reference") member["TypeId"] = "missing";
        if (corruption == "offset") member["Offset"] = int.MaxValue;
        if (corruption == "array") member["ArrayCount"] = int.MaxValue;
        if (corruption == "null") json["Types"] = null;
        if (corruption == "cycle")
        {
            var child = json["Types"]![1]!;
            child["Members"]![0]!["TypeId"] = child["Id"]!.GetValue<string>();
            child["Members"]![0]!["Primitive"] = 0;
        }
        var bytes = Encoding.UTF8.GetBytes(corruption == "json" ? "{" : json.ToJsonString());
        Assert.Throws<InvalidDataException>(() => ShmSchemaFile.Read(bytes));
    }

    [Fact]
    public async Task SavedLiveValuesAreNotIncludedAndImportedTabRunsWithoutHeaders()
    {
        using var files = new Files();
        using var original = new ShmTabViewModel { ShmName = files.ShmName, StructName = "Root", RefreshMode = RefreshMode.Manual };
        original.Initialize(files.Database, files.Root); original.BuildTree();
        var data = new byte[files.Root.TotalSize]; BitConverter.GetBytes(1234).CopyTo(data, 0); data[4] = 1;
        using (var live = MemoryMappedFile.CreateOrOpen(original.ShmName, data.Length))
        {
            using (var access = live.CreateViewAccessor()) { access.WriteArray(0, data, 0, data.Length); access.Flush(); }
            await original.Run(); Assert.True(original.IsRunning);
            Assert.True(await original.SaveSchemaAsync(files.SchemaPath)); Assert.True(original.IsRunning);
            original.Stop();
        }
        File.Delete(files.HeaderPath);
        var store = new MemoryStore(); using var vm = new MainViewModel(store);
        vm.ShowErrorNotice = (_, message) => Assert.Fail(message);
        Assert.True(await vm.LoadShmSchemaAsync(files.SchemaPath));
        var tab = Assert.Single(vm.Tabs); var root = Assert.Single(tab.RootNodes);
        var value = root.Children.Single(n => n.Name == "value");
        Assert.Equal("0", value.Value); Assert.Equal("false", root.Children.Single(n => n.Name == "flag").Value);
        Assert.Equal("\"\"", root.Children.Single(n => n.Name == "text").Value);
        Assert.False(tab.IsLoaded); Assert.False(tab.IsRunning); Assert.True(tab.CanRun);
        Assert.Equal(ShmTabSource.StructureFile, tab.Source); Assert.Empty(store.State.Tabs);
        var nested = root.Children.Single(n => n.Name == "nested"); nested.IsExpanded = true; nested.ExpandLoad();
        using var memory = MemoryMappedFile.CreateOrOpen(tab.ShmName, data.Length);
        void Write(int number)
        {
            BitConverter.GetBytes(number).CopyTo(data, 0);
            using var access = memory.CreateViewAccessor(); access.WriteArray(0, data, 0, data.Length); access.Flush();
        }
        Write(77); await tab.Run(); Assert.Equal("77", value.Value); Assert.True(tab.IsRunning);
        Write(88); tab.ManualRefreshCommand.Execute(null); Assert.Equal("88", value.Value);
        tab.Stop(); Write(99); await tab.Run(); Assert.Equal("99", value.Value);
        Assert.Same(root, Assert.Single(tab.RootNodes)); Assert.True(nested.IsExpanded);
        tab.Stop();
        using var restarted = new MainViewModel(store); await restarted.Initialization;
        Assert.Empty(restarted.Tabs); Assert.False(restarted.UseDefaultSctcClean);
    }

    [Fact]
    public async Task HeaderRefreshDoesNotInvalidateSchemaTabsAndOnlyHeaderTabsArePersisted()
    {
        using var files = new Files(); var store = new MemoryStore(); using var vm = new MainViewModel(store);
        Assert.True(await vm.LoadShmSchemaAsync(files.SchemaPath)); var schemaTab = vm.SelectedTab!;
        var root = Assert.Single(schemaTab.RootNodes);
        vm.AddHeaderFiles(new[] { files.HeaderPath }); await vm.Initialization;
        Assert.Same(root, Assert.Single(schemaTab.RootNodes));
        vm.NewShmName = "header_tab"; vm.NewStructName = "Root"; vm.LoadShmTabCommand.Execute(null);
        Assert.Equal(2, vm.Tabs.Count); Assert.Equal("header_tab", Assert.Single(store.State.Tabs).ShmName);
        vm.RemoveHeaderFiles(vm.HeaderFiles.ToArray()); await vm.Initialization;
        Assert.Same(root, Assert.Single(schemaTab.RootNodes)); Assert.True(schemaTab.CanRun);
        Assert.False(vm.Tabs.Single(t => t.Source == ShmTabSource.Headers).IsTreeBuilt);
    }

    [Fact]
    public async Task FailedLoadAndDeclinedReplacementPreserveCurrentTabAndNotifyOnlyOnFailure()
    {
        using var files = new Files(); var accept = false; var confirms = 0;
        using var vm = new MainViewModel(new MemoryStore(), _ => { confirms++; return accept; });
        var errors = new List<string>(); vm.ShowErrorNotice = (_, message) => errors.Add(message);
        Assert.True(await vm.LoadShmSchemaAsync(files.SchemaPath)); var tab = vm.SelectedTab!;
        Assert.False(await vm.LoadShmSchemaAsync(files.SchemaPath)); Assert.Same(tab, vm.SelectedTab);
        Assert.Equal(1, confirms); Assert.Empty(errors);
        Assert.False(await vm.LoadShmSchemaAsync(files.SchemaPath + ".missing"));
        Assert.Single(errors); Assert.Same(tab, Assert.Single(vm.Tabs)); Assert.False(vm.IsLoadingSchema);
        accept = true; Assert.True(await vm.LoadShmSchemaAsync(files.SchemaPath));
        Assert.NotSame(tab, vm.SelectedTab); Assert.False(tab.CanRun); Assert.Single(vm.Tabs);
    }

    [Fact]
    public async Task FailedSaveKeepsOriginalFileAndReportsOnce()
    {
        using var files = new Files(); using var vm = new MainViewModel(new MemoryStore());
        Assert.True(await vm.LoadShmSchemaAsync(files.SchemaPath)); var tab = vm.SelectedTab!;
        var before = File.ReadAllBytes(files.SchemaPath); var notices = 0;
        tab.ShowErrorNotice = (_, message) => { Assert.Contains("write failure", message); notices++; };
        tab.SchemaStore = new() { BeforeReplace = _ => throw new IOException("write failure") };
        Assert.False(await tab.SaveSchemaAsync(files.SchemaPath));
        Assert.Equal(before, File.ReadAllBytes(files.SchemaPath)); Assert.Equal(1, notices);
        Assert.True(tab.CanRun); Assert.False(tab.IsBusy); Assert.Empty(Directory.GetFiles(files.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task LargeLazyArraysStayCollapsedAndSchemaSizeDependsOnTypes()
    {
        using var files = new Files("struct Child { int x; }; struct Root { Child items[100000]; };");
        using var vm = new MainViewModel(new MemoryStore());
        Assert.True(await vm.LoadShmSchemaAsync(files.SchemaPath)); var tab = vm.SelectedTab!;
        var array = Assert.Single(Assert.Single(tab.RootNodes).Children);
        Assert.True(array.IsLazy); Assert.Single(array.Children);
        Assert.True(await tab.SaveSchemaAsync(files.SchemaPath));
        Assert.True(array.IsLazy); Assert.Single(array.Children);
        Assert.True(new FileInfo(files.SchemaPath).Length < 10_000);
    }
}
