namespace ShmViewer.Core.Parser;

public sealed record HeaderParseOptions
{
    public bool StrictBinaryLayout { get; init; }
    public int Pack { get; init; } = 8;
    public IReadOnlyList<string> IncludeDirectories { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Defines { get; init; } = Array.Empty<string>();
}
