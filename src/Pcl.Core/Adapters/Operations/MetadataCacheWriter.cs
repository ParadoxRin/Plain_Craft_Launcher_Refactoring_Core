namespace Pcl.Core.Adapters.Operations;

// Atomic rename protects readers. A bounded striped lock also protects first creation from two
// metadata responses racing the File.Exists / File.Move branch; no network I/O owns these locks.
public static class MetadataCacheWriter
{
    private static readonly object[] Gates = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();
    public static void Write(string path, string text)
    {
        var full = Path.GetFullPath(path);
        var hash = (uint)StringComparer.OrdinalIgnoreCase.GetHashCode(full);
        lock (Gates[hash % Gates.Length])
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            MeloongCore.FileUtils.WriteAtomic(full, text);
        }
    }
}
