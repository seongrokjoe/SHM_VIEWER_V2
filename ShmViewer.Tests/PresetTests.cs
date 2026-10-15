using System.IO;
using ShmViewer.Core;
using ShmViewer.ViewModels;
using Xunit;

namespace ShmViewer.Tests;

public sealed class PresetTests
{
    private sealed class MemoryStore : IRealTimeSettingsStore
    {
        public RealTimeSettings State { get; set; } = new(false, new(), new());
        public RealTimeSettings Load() => State;
        public void Save(RealTimeSettings settings) => State = settings;
    }
    [Fact]
    public async Task PresetCreatesAllStoppedTabsAtOneSecondAndRetainsMissingHeaders()
    {
        var store = new MemoryStore();
        using var vm = new MainViewModel(store, _ => true);
        var notices = 0; vm.ShowSctcNotice = () => notices++;
        Assert.True(vm.SetDefaultPreset(true)); await vm.Initialization;
        Assert.True(vm.SetDefaultPreset(true)); Assert.Equal(1, notices);
        Assert.Equal(7, vm.HeaderFiles.Count); Assert.Equal(2, vm.HeaderGroups.Count());
        Assert.Equal(5, vm.Tabs.Count);
        Assert.All(vm.Tabs, t => { Assert.Equal(RefreshMode.Ms1000, t.RefreshMode); Assert.False(t.IsRunning); });
        Assert.All(vm.HeaderFiles, f => Assert.Contains("SCTCApplication", f.Path));
        var recipe = Assert.Single(vm.HeaderFiles, f => f.Name == "_Recipe.h");
        Assert.Equal(Path.Combine(SctcPreset.Load().HeaderGroups[0].Directory, "_Recipe.h"), recipe.Path);
        Assert.Contains(recipe.Path, store.State.Headers);
        Assert.Equal("tagRun", vm.Tabs[0].StructName);
        Assert.DoesNotContain(vm.Tabs, t => t.ShmName == "OBJECT_INFO_MEMORY");
        Assert.True(store.State.UseDefaultSctcClean);
    }
    [Fact]
    public async Task CancelPreservesEverything_ConfirmOnlyResetsRealTime()
    {
        var approve = true;
        var store = new MemoryStore(); using var vm = new MainViewModel(store, _ => approve);
        vm.ShowSctcNotice = () => { };
        vm.SetDefaultPreset(true); await vm.Initialization;
        var dat = vm.DatDiff; dat.Left.StructName = "MyStruct";
        var tab = vm.Tabs[0]; approve = false;
        Assert.False(vm.SetDefaultPreset(false)); Assert.Same(tab, vm.Tabs[0]); Assert.True(vm.UseDefaultSctcClean);
        approve = true; Assert.True(vm.SetDefaultPreset(false));
        Assert.Empty(vm.Tabs); Assert.Empty(vm.HeaderFiles); Assert.False(store.State.UseDefaultSctcClean);
        Assert.Same(dat, vm.DatDiff); Assert.Equal("MyStruct", dat.Left.StructName);
        Assert.Empty(store.State.Tabs); Assert.Empty(store.State.Headers);
    }
    [Fact]
    public async Task RestorationRetainsUnresolvedTabsAndSavesChangedRefresh()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.h");
        var store = new MemoryStore { State = new(false, new() { missing }, new() { new("MEMORY", "MissingStruct", "Ms1000") }) };
        using var vm = new MainViewModel(store, _ => true); await vm.Initialization;
        Assert.False(Assert.Single(vm.HeaderFiles).IsAvailable);
        var tab = Assert.Single(vm.Tabs); Assert.False(tab.CanRun); Assert.False(tab.IsRunning);
        tab.RefreshMode = RefreshMode.Ms5000;
        Assert.Equal("Ms5000", store.State.Tabs.Single().RefreshMode);
        Assert.Equal(missing, store.State.Headers.Single());
    }
    [Fact]
    public async Task RestartAfterSctcStartsUncheckedAndEmptyAndClearsSavedState()
    {
        var obsolete = Path.Combine(SctcPreset.Load().HeaderGroups[0].Directory, "jobinfo.h");
        var custom = Path.Combine(Path.GetTempPath(), "custom", "jobinfo.h");
        var store = new MemoryStore { State = new(true, new() { obsolete, custom }, new()
        {
            new("OBJECT_INFO_MEMORY", "STJOBINFO", "Ms1000"), new("CUSTOM", "STJOBINFO", "Ms1000")
        }) };
        using var vm = new MainViewModel(store, _ => true); await vm.Initialization;
        Assert.False(vm.UseDefaultSctcClean); Assert.Empty(vm.HeaderFiles); Assert.Empty(vm.Tabs);
        Assert.False(store.State.UseDefaultSctcClean); Assert.Empty(store.State.Headers); Assert.Empty(store.State.Tabs);
    }
    [Fact]
    public void DatPresetsAreIndependentAndManualChangesClearOnlyThatSide()
    {
        using var vm = new DatDiffViewModel(_ => true);
        var notices = 0;
        vm.Left.ShowSctcNotice = vm.Right.ShowSctcNotice = () => notices++;
        Assert.True(vm.IsRunData); Assert.False(vm.Left.UseSctcPath); Assert.False(vm.Right.UseSctcPath);
        vm.Left.Pack = "2"; vm.Right.StructName = "Custom";
        vm.Left.UseSctcPath = true;
        Assert.Equal("tagRun", vm.Left.StructName); Assert.Equal("2", vm.Left.Pack);
        Assert.Equal(new[] { "_enumdef.h", "_Run.h" }, vm.Left.Headers.Select(h => h.Name));
        Assert.EndsWith("MEM_RUNDATA.DAT", vm.Left.DatPath);
        vm.IsInitData = true;
        Assert.Equal("tagUnitInitial", vm.Left.StructName); Assert.Equal("Custom", vm.Right.StructName);
        Assert.Equal(new[] { "_enumdef.h", "K12Initial.h" }, vm.Left.Headers.Select(h => h.Name));
        Assert.EndsWith("INIT_UNIT.DAT", vm.Left.DatPath);
        vm.Right.UseSctcPath = true; vm.Left.DatPath = "custom.dat";
        Assert.False(vm.Left.UseSctcPath); Assert.True(vm.Right.UseSctcPath);
        vm.IsRunData = true;
        Assert.Equal("custom.dat", vm.Left.DatPath); Assert.Equal("tagRun", vm.Right.StructName);
        vm.Right.UseSctcPath = false; Assert.Equal("tagRun", vm.Right.StructName);
        Assert.Equal(2, notices);
        vm.Right.UseSctcPath = true; vm.Right.UseSctcPath = true;
        Assert.Equal(3, notices);
    }
    [Fact]
    public void PresetConfirmationCanCancelWithoutChangingInputsAndMissingPathIsReported()
    {
        var calls = 0;
        var input = new DatInputViewModel("버전1", () => { calls++; return false; }, () => SctcDataKind.Run);
        var notices = 0; input.ShowSctcNotice = () => notices++;
        input.UseSctcPath = true;
        Assert.False(input.UseSctcPath); Assert.Empty(input.Headers); Assert.Equal(1, calls);
        Assert.Equal(0, notices);
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.h");
        var custom = new DatInputViewModel("버전1", () => true, () => SctcDataKind.Run);
        custom.AddFiles(new[] { missing }); custom.StructName = "Root"; custom.DatPath = "missing.dat";
        Assert.Contains(missing, Assert.Throws<InvalidDataException>(() => custom.Capture()).Message);
    }
    [Fact]
    public void LegacySettingsMigrateWithoutDroppingMissingEntries()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ShmViewer.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "header_paths.json"), "[\"missing.h\"]");
            File.WriteAllText(Path.Combine(directory, "shm_tabs.json"), "[{\"ShmName\":\"A\",\"StructName\":\"B\",\"RefreshMode\":\"Ms1000\"}]");
            var store = new RealTimeSettingsStorage(directory);
            var state = store.Load(); Assert.Equal("missing.h", Assert.Single(state.Headers)); Assert.Single(state.Tabs);
            store.Save(new(true, new(), new()));
            var restored = store.Load(); Assert.True(restored.UseDefaultSctcClean); Assert.Empty(restored.Headers); Assert.Empty(restored.Tabs);
        }
        finally { Directory.Delete(directory, true); }
    }
}
