namespace ShmViewer.Core.Model;

public class TypeInfo
{
    public string Name { get; set; } = string.Empty;
    public int TotalSize { get; set; }
    public int Alignment { get; set; } = 1;
    public bool IsUnion { get; set; }
    public bool IsAnonymousRecord { get; set; }
    public List<MemberInfo> Members { get; set; } = new();
}
