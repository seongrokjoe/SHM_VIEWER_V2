using System.IO;

namespace ShmViewer.Core.Dat;

public sealed class DatDocument
{
    public string FilePath { get; }
    public BinarySchema Schema { get; }
    internal byte[] Data { get; }
    public ReadOnlyMemory<byte> Bytes => Data;
    public BinaryField Root => new(string.Empty, Schema.Root, 0);
    public DatDocument(string filePath, BinarySchema schema, byte[] data)
    {
        if (data.Length != schema.Root.Size) throw new InvalidDataException($"DAT 크기 불일치: 파일 {data.Length} bytes / {schema.StructName} {schema.Root.Size} bytes");
        FilePath = filePath; Schema = schema; Data = (byte[])data.Clone();
    }
    public static DatDocument Load(string path, BinarySchema schema)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != schema.Root.Size) throw new InvalidDataException($"DAT 크기 불일치: 파일 {stream.Length} bytes / {schema.StructName} {schema.Root.Size} bytes");
        var bytes = new byte[schema.Root.Size]; stream.ReadExactly(bytes);
        return new(path, schema, bytes);
    }
    public static void SaveAtomic(string path, byte[] bytes, params string[] sourcePaths)
    {
        path = Path.GetFullPath(path);
        if (sourcePaths.Any(p => !string.IsNullOrEmpty(p) && string.Equals(Path.GetFullPath(p), path, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("원본 DAT와 다른 경로에 저장하세요.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            if (!File.ReadAllBytes(temporary).AsSpan().SequenceEqual(bytes)) throw new IOException("저장 검증에 실패했습니다.");
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
