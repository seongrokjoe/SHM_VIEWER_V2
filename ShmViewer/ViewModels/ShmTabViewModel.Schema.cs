using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ShmViewer.Core;
using ShmViewer.Views.Dialogs;
using System.IO;

namespace ShmViewer.ViewModels;

public enum ShmTabSource { Headers, StructureFile }

public partial class ShmTabViewModel
{
    public ShmTabSource Source { get; private set; }
    public string? StructureFilePath { get; private set; }
    public Action<string, string> ShowErrorNotice { get; set; } = UserNotice.ShowError;
    public ShmSchemaStore SchemaStore { get; set; } = new();
    public bool CanSaveSchema => !_disposed && IsTreeBuilt && !HasUnresolvedTypes && !IsBusy;

    internal void InitializeFromSchema(ShmSchemaSnapshot schema, string path)
    {
        Source = ShmTabSource.StructureFile;
        StructureFilePath = Path.GetFullPath(path);
        ShmName = schema.ShmName; StructName = schema.StructName;
        RefreshMode = Enum.Parse<RefreshMode>(schema.RefreshMode);
        Initialize(schema.Database, schema.RootType);
        OnPropertyChanged(nameof(Source)); OnPropertyChanged(nameof(StructureFilePath));
    }

    [RelayCommand(CanExecute = nameof(CanSaveSchema))]
    private async Task SaveSchema()
    {
        var name = new string(ShmName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(name)) name = "SHM";
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(name.Split('.')[0], StringComparer.OrdinalIgnoreCase)) name = "SHM_" + name;
        var dialog = new SaveFileDialog { Title = "SHM 구조 파일 저장", Filter = "SHM 구조 파일|*.shm.schema.json",
            FileName = name + ".shm.schema.json", DefaultExt = ".shm.schema.json", AddExtension = true, OverwritePrompt = true };
        if (dialog.ShowDialog() == true) await SaveSchemaAsync(dialog.FileName);
    }

    public async Task<bool> SaveSchemaAsync(string path)
    {
        if (!CanSaveSchema || _rootType == null) return false;
        var version = _lifetimeVersion;
        var type = _rootType; var shmName = ShmName; var structName = StructName; var refresh = RefreshMode.ToString();
        SetBusy("SHM 구조 파일 저장 중...");
        try
        {
            await Task.Run(() => SchemaStore.Save(path, ShmSchemaFile.Serialize(shmName, structName, refresh, type)));
            if (!_disposed && version == _lifetimeVersion)
            { IsStatusError = false; StatusText = "구조 파일 저장 완료: " + Path.GetFullPath(path); }
            return true;
        }
        catch (Exception ex)
        {
            if (!_disposed && version == _lifetimeVersion)
            {
                IsStatusError = true; StatusText = "구조 파일 저장 실패: " + ex.Message;
                ShowErrorNotice("RealTime Viewer · 구조 파일 저장 실패", $"{shmName}\n{path}\n{ex.Message}");
            }
            return false;
        }
        finally { if (!_disposed && version == _lifetimeVersion) ClearBusy(); }
    }
}
