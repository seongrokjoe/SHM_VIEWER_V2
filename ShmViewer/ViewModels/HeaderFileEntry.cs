using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;

namespace ShmViewer.ViewModels;

public partial class HeaderFileEntry : ObservableObject
{
    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string FullPath => System.IO.Path.GetFullPath(Path);
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
    [ObservableProperty] private string _error = string.Empty;
    public bool IsAvailable => string.IsNullOrEmpty(Error);
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(IsAvailable));
    public HeaderFileEntry(string path) { Path = path; Check(); }
    public void Check()
    {
        try { using var stream = File.Open(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); Error = string.Empty; }
        catch (DirectoryNotFoundException) { Error = "경로 없음"; }
        catch (FileNotFoundException) { Error = "파일 없음"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error = "읽기 실패: " + ex.Message; }
    }
}
public sealed record HeaderFileGroup(string Directory, IEnumerable<HeaderFileEntry> Files);
