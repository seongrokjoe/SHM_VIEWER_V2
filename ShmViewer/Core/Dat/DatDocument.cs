using System.IO;

namespace ShmViewer.Core.Dat;

public sealed class DatDocument
{
    public DatDiskStamp? DiskStamp { get; internal set; }
    public bool LoadedFromSchema { get; internal set; }
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
        return new DatFileStore().Load(path, () => schema, useHeader: true);
    }
}
