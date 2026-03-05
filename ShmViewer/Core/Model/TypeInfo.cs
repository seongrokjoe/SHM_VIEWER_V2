namespace ShmViewer.Core.Model;

public class TypeInfo
{
    public string Name { get; set; } = string.Empty;
    public int TotalSize { get; set; }
    public bool IsUnion { get; set; }
    public List<MemberInfo> Members { get; set; } = new();
}
