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
        Assert.True(vm.SetDefaultPreset(true)); await vm.Initialization;
        Assert.Equal(8, vm.HeaderFiles.Count); Assert.Equal(2, vm.HeaderGroups.Count());
        Assert.Equal(6, vm.Tabs.Count);
        Assert.All(vm.Tabs, t => { Assert.Equal(RefreshMode.Ms1000, t.RefreshMode); Assert.False(t.IsRunning); });
        Assert.All(vm.HeaderFiles, f => Assert.Contains("SCTCApplication", f.Path));
        var recipe = Assert.Single(vm.HeaderFiles, f => f.Name == "_Recipe.h");
        Assert.Equal(Path.Combine(SctcPreset.Load().HeaderGroups[0].Directory, "_Recipe.h"), recipe.Path);
        Assert.Contains(recipe.Path, store.State.Headers);
        Assert.Equal("tagRun", vm.Tabs[0].StructName);
        Assert.Equal("OBJECT_INFO_MEMORY", vm.Tabs[5].ShmName);
        Assert.True(store.State.UseDefaultSctcClean);
    }
    [Fact]
    public async Task CancelPreservesEverything_ConfirmOnlyResetsRealTime()
    {
        var approve = true;
        var store = new MemoryStore(); using var vm = new MainViewModel(store, _ => approve);
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
        var store = new MemoryStore { State = new(true, new() { missing }, new() { new("MEMORY", "MissingStruct", "Ms1000") }) };
        using var vm = new MainViewModel(store, _ => true); await vm.Initialization;
        Assert.False(Assert.Single(vm.HeaderFiles).IsAvailable);
        var tab = Assert.Single(vm.Tabs); Assert.False(tab.CanRun); Assert.False(tab.IsRunning);
        tab.RefreshMode = RefreshMode.Ms5000;
        Assert.Equal("Ms5000", store.State.Tabs.Single().RefreshMode);
        Assert.Equal(missing, store.State.Headers.Single());
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
