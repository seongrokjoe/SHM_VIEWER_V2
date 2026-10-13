using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ShmViewer.Core;
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
    private readonly Func<SctcDataKind> _dataKind;
    private bool _useSctcPath;
    public bool UseSctcPath
    {
        get => _useSctcPath;
        set
        {
            if (value == _useSctcPath) return;
            if (value && !_beforeChange()) { OnPropertyChanged(); return; }
            SetProperty(ref _useSctcPath, value);
            if (value) ApplySctcPreset();
        }
    }
    public ObservableCollection<HeaderFileEntry> Headers { get; } = new();
    [ObservableProperty] private HeaderFileEntry? _selectedHeader;
    public List<HeaderFileEntry> SelectedHeaders { get; } = new();
    public bool CanMoveHeader => SelectedHeader != null && SelectedHeaders.Count <= 1;
    public void SelectHeaders(IEnumerable<HeaderFileEntry> headers)
    {
        SelectedHeaders.Clear(); SelectedHeaders.AddRange(headers);
        SelectedHeader = SelectedHeaders.FirstOrDefault();
        MoveHeaderCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedHeaderChanged(HeaderFileEntry? value) => MoveHeaderCommand.NotifyCanExecuteChanged();
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
    public DatInputViewModel(string label, Func<bool> beforeChange, Func<SctcDataKind> dataKind)
    { Label = label; _beforeChange = beforeChange; _dataKind = dataKind; }
    public bool HasInput => Headers.Count > 0 || UseSctcPath || DatPath.Length > 0 || StructName.Length > 0 ||
        IncludeDirectories.Length > 0 || Defines.Length > 0 || Pack != "8";
    internal void Reset()
    {
        Headers.Clear(); SelectHeaders(Array.Empty<HeaderFileEntry>());
        _useSctcPath = false; _datPath = _structName = _includeDirectories = _defines = string.Empty; _pack = "8";
        foreach (var name in new[] { nameof(UseSctcPath), nameof(DatPath), nameof(StructName), nameof(IncludeDirectories), nameof(Defines), nameof(Pack) })
            OnPropertyChanged(name);
    }
    internal void ApplySctcPreset()
    {
        var preset = SctcPreset.Load().ForData(_dataKind());
        Headers.Clear();
        SelectHeaders(Array.Empty<HeaderFileEntry>());
        foreach (var path in preset.Headers) Headers.Add(new(path));
        _datPath = preset.DatPath; _structName = preset.StructName;
        OnPropertyChanged(nameof(DatPath)); OnPropertyChanged(nameof(StructName));
    }
    private void MarkCustom()
    {
        if (!_useSctcPath) return;
        _useSctcPath = false; OnPropertyChanged(nameof(UseSctcPath));
    }
    private void Change(ref string field, string value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field == value) return;
        if (_beforeChange())
        {
            SetProperty(ref field, value, name);
            if (name is nameof(DatPath) or nameof(StructName)) MarkCustom();
        }
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
        MarkCustom();
        foreach (var file in files)
        {
            if (file.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
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
        var selected = SelectedHeaders.Count > 0 ? SelectedHeaders.ToArray() : SelectedHeader == null ? Array.Empty<HeaderFileEntry>() : new[] { SelectedHeader };
        if (selected.Length == 0 || !_beforeChange()) return;
        MarkCustom();
        foreach (var header in selected) Headers.Remove(header);
        SelectHeaders(Array.Empty<HeaderFileEntry>());
    }
    [RelayCommand(CanExecute = nameof(CanMoveHeader))]
    private void MoveHeader(string direction)
    {
        if (!CanMoveHeader || SelectedHeader == null) return;
        var index = Headers.IndexOf(SelectedHeader);
        var next = index + (direction == "up" ? -1 : 1);
        if (next >= 0 && next < Headers.Count && _beforeChange()) { MarkCustom(); Headers.Move(index, next); }
    }
    public DatInputSnapshot Capture()
    {
        foreach (var header in Headers) header.Check();
        if (Headers.Count == 0 || string.IsNullOrWhiteSpace(StructName)) throw new InvalidDataException(Label + ": 헤더와 Struct를 지정하세요.");
        var missing = Headers.Where(h => !h.IsAvailable).Select(h => h.Path).ToArray();
        if (missing.Length > 0) throw new InvalidDataException(Label + ": 누락되거나 읽을 수 없는 헤더:\n" + string.Join("\n", missing));
        if (!int.TryParse(Pack, out var pack)) throw new InvalidDataException(Label + ": Pack은 정수로 입력하세요.");
        string[] Lines(string text) => text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new(Label, Headers.Select(h => h.Path).ToArray(), DatPath.Trim(), StructName.Trim(),
            new() { StrictBinaryLayout = true, Pack = pack, IncludeDirectories = Lines(IncludeDirectories), Defines = Lines(Defines) });
    }
}
