using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ShmViewer.Core;
using System.IO;

namespace ShmViewer.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private bool _isLoadingSchema;
    public bool CanLoadShmSchema => !_disposed && !IsParsing && !IsLoadingSchema;
    partial void OnIsLoadingSchemaChanged(bool value) => NotifyHeaderCommands();

    [RelayCommand(CanExecute = nameof(CanLoadShmSchema))]
    private async Task LoadShmSchema()
    {
        var dialog = new OpenFileDialog { Title = "SHM 구조 파일 열기", Filter = "SHM 구조 파일|*.shm.schema.json|JSON 파일|*.json" };
        if (dialog.ShowDialog() == true) await LoadShmSchemaAsync(dialog.FileName);
    }

    public async Task<bool> LoadShmSchemaAsync(string path)
    {
        if (!CanLoadShmSchema) return false;
        var version = _parseVersion;
        ShmTabViewModel? candidate = null;
        IsLoadingSchema = true; ParserStatus = "SHM 구조 파일 로드 중...";
        try
        {
            var schema = await Task.Run(() => new ShmSchemaStore().Load(path));
            if (_disposed || version != _parseVersion) return false;
            candidate = new() { ShowErrorNotice = (title, message) => ShowErrorNotice(title, message) };
            candidate.InitializeFromSchema(schema, path);
            await candidate.BuildTreeAsync();
            if (_disposed || version != _parseVersion) return false;
            if (!candidate.IsTreeBuilt || candidate.HasUnresolvedTypes) throw new InvalidDataException(candidate.StatusText);
            var existing = Tabs.FirstOrDefault(t => t.ShmName == candidate.ShmName);
            if (existing != null)
            {
                if (!_confirm($"'{candidate.ShmName}' 탭을 구조 파일로 교체하시겠습니까?\n실행 중이면 중지하고 기본값으로 다시 로드합니다."))
                { ParserStatus = "구조 파일 로드를 취소했습니다."; return false; }
                existing.Dispose(); Tabs.Remove(existing);
            }
            candidate.StatusText = $"구조 파일 로드 완료 · 기본값 표시 · {schema.RootType.TotalSize:N0} bytes";
            Tabs.Add(candidate); SelectedTab = candidate; candidate = null;
            SaveSettings(); ParserStatus = "SHM 구조 파일 로드 완료: " + Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex)
        {
            if (!_disposed && version == _parseVersion)
            {
                ParserStatus = "SHM 구조 파일 로드 실패: " + ex.Message;
                ShowErrorNotice("RealTime Viewer · 구조 파일 로드 실패", path + "\n" + ex.Message);
            }
            return false;
        }
        finally { candidate?.Dispose(); if (!_disposed) IsLoadingSchema = false; }
    }
}
