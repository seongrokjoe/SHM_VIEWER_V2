using System.IO;
using ShmViewer.Core.Dat;

namespace ShmViewer.Tests;

internal static class DatTestData
{
    public static DatDocument Document(string header, params (string Field, string Value)[] values)
    {
        var schema = DatBinaryTests.Parse(header);
        var bytes = new byte[schema.Root.Size];
        var fields = DatDiffSession.Fields(new("", schema.Root, 0)).ToDictionary(f => f.Path);
        foreach (var (field, value) in values) BinaryValueCodec.WriteText(bytes, fields[field], value);
        return new("", schema, bytes);
    }
    public static string Value(DatDiffSession session, DatSourceVersion version, string path) =>
        BinaryValueCodec.Format(session.Version(version).Current.Data, session.Field(version, path)!);
    public static DatDiffNode Node(DatDiffSession session, string path) => session.Nodes.Single(n => n.Path == path);
    public sealed class Files : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "ShmViewer.Tests", Guid.NewGuid().ToString("N"));
        public Files() => Directory.CreateDirectory(DirectoryPath);
        public string PathFor(string name) => Path.Combine(DirectoryPath, name);
        public DatDocument Write(string name, DatDocument document)
        {
            var path = PathFor(name); File.WriteAllBytes(path, document.Bytes.ToArray());
            return DatDocument.Load(path, document.Schema);
        }
        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }
}
