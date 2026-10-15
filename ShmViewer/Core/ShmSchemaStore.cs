using System.IO;

namespace ShmViewer.Core;

public sealed class ShmSchemaStore
{
    internal Action<string>? BeforeReplace { get; set; }

    public ShmSchemaSnapshot Load(string path) => ShmSchemaFile.Read(File.ReadAllBytes(path));

    public void Save(string path, byte[] json)
    {
        ShmSchemaFile.Read(json);
        path = Path.GetFullPath(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(json); stream.Flush(true); }
            ShmSchemaFile.Read(File.ReadAllBytes(temporary));
            BeforeReplace?.Invoke(path);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
