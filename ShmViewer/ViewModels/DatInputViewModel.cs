using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ShmViewer.Core.Dat;
using ShmViewer.Core.Parser;
using System.Collections.ObjectModel;
using System.IO;

namespace ShmViewer.ViewModels;

public sealed record DatInputSnapshot(string Label, string[] Headers, string DatPath, string StructName, HeaderParseOptions Options)
{
    public BinarySchema Parse() => new HeaderParserService().ParseBinary(Headers, StructName, Options);
}

public partial class DatInputViewModel : ObservableObject
{
    private readonly Func<bool> _beforeChange;
    public string Label { get; }
    public bool IsTarget { get; }
    public ObservableCollection<HeaderFileEntry> Headers { get; } = new();
    [ObservableProperty] private HeaderFileEntry? _selectedHeader;
    private string _datPath = string.Empty;
    private string _structName = string.Empty;
    private string _includeDirectories = string.Empty;
    private string _defines = string.Empty;
    private string _pack = "8";
    public string DatPath { get => _datPath; set => Change(ref _datPath, value); }
    public string StructName { get => _structName; set => Change(ref _structName, value); }
    public string IncludeDirectories { get => _includeDirectories; set => Change(ref _includeDirectories, value); }
    public string Defines { get => _defines; set => Change(ref _defines, value); }
    public string Pack { get => _pack; set => Change(ref _pack, value); }
    public DatInputViewModel(string label, bool isTarget, Func<bool> beforeChange)
    { Label = label; IsTarget = isTarget; _beforeChange = beforeChange; }
    private void Change(ref string field, string value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field == value) return;
        if (_beforeChange()) SetProperty(ref field, value, name);
        else OnPropertyChanged(name);
    }
    [RelayCommand]
    private void AddHeaders()
    {
        var dialog = new OpenFileDialog { Filter = "C/C++ Headers|*.h;*.hpp|All Files|*.*", Multiselect = true };
        if (dialog.ShowDialog() == true) AddFiles(dialog.FileNames);
    }
    public void AddFiles(IEnumerable<string> files)
    {
        if (!_beforeChange()) return;
        foreach (var file in files)
        {
            if (file.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) && !IsTarget)
            { _datPath = file; OnPropertyChanged(nameof(DatPath)); }
            else if (file.EndsWith(".h", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".hpp", StringComparison.OrdinalIgnoreCase))
                if (!Headers.Any(h => string.Equals(h.Path, file, StringComparison.OrdinalIgnoreCase))) Headers.Add(new(file));
        }
    }
    [RelayCommand]
    private void ChooseDat()
    {
        var dialog = new OpenFileDialog { Filter = "DAT Files|*.dat|All Files|*.*" };
        if (dialog.ShowDialog() == true) DatPath = dialog.FileName;
    }
    [RelayCommand]
    private void RemoveHeader()
    {
        if (SelectedHeader != null && _beforeChange()) Headers.Remove(SelectedHeader);
    }
    [RelayCommand]
    private void MoveHeader(string direction)
    {
        if (SelectedHeader == null) return;
        var index = Headers.IndexOf(SelectedHeader);
        var next = index + (direction == "up" ? -1 : 1);
        if (next >= 0 && next < Headers.Count && _beforeChange()) Headers.Move(index, next);
    }
    public DatInputSnapshot Capture()
    {
        foreach (var header in Headers) header.Check();
        if (Headers.Count == 0 || string.IsNullOrWhiteSpace(StructName)) throw new InvalidDataException(Label + ": 헤더와 Struct를 지정하세요.");
        if (Headers.Any(h => !h.IsAvailable)) throw new InvalidDataException(Label + ": 누락되거나 읽을 수 없는 헤더가 있습니다.");
        if (!int.TryParse(Pack, out var pack)) throw new InvalidDataException(Label + ": Pack은 정수로 입력하세요.");
        string[] Lines(string text) => text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new(Label, Headers.Select(h => h.Path).ToArray(), DatPath.Trim(), StructName.Trim(),
            new() { StrictBinaryLayout = true, Pack = pack, IncludeDirectories = Lines(IncludeDirectories), Defines = Lines(Defines) });
    }
}
