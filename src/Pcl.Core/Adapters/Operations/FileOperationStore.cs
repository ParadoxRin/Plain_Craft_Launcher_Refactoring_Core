using System.Text.Json;
using System.Text.RegularExpressions;
using Pcl.Core.Ports.Operations;

namespace Pcl.Core.Adapters.Operations;
// OS 独占句柄覆盖恢复、受理和全部写入；残留锁文件不是占锁证据，不删除或按 PID/TTL 抢锁。
public sealed class FileOperationStore : IOperationStore
{
    private readonly string directory;
    private readonly FileStream lease;
    public FileOperationStore(string root, string area = "operations-v3")
    {
        if (area is not ("operations-v3" or "launch-operations-v1" or "account-operations-v1" or "page-operations-v1" or "page-execution-v1"))
        {
            throw new ArgumentException("OPERATION_AREA_INVALID");
        }

        var lockName = area.StartsWith("page-", StringComparison.Ordinal)
            ? "." + area + ".lock"
            : area == "operations-v3"
                ? ".operations.lock"
                : area == "account-operations-v1"
                    ? ".account-operations.lock"
                    : ".launch-operations.lock";
        root = Path.GetFullPath(root);
        EnsureSafePath(
            Path.Combine(root, lockName)
        );
        Directory.CreateDirectory(root);
        lease = new(
            Path.Combine(root, lockName),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );
        try
        {
            directory = Path.Combine(root, area);
            EnsureSafePath(
                Path.Combine(directory, "probe")
            );
            Directory.CreateDirectory(directory);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public IReadOnlyList<byte[]> ReadAll()
    {
        var result = new List<byte[]>();
        foreach (var file in Directory.EnumerateFiles(directory, "op-*.json"))
        {
            EnsureSafePath(file);
            if (new FileInfo(file).Length > 16 * 1024 * 1024)
            {
                throw new InvalidDataException("OPERATION_STORE_INVALID");
            }

            var bytes = File.ReadAllBytes(file);
            using var parsed = JsonDocument.Parse(bytes);
            var id = parsed.RootElement.GetProperty("Snapshot")
                .GetProperty("OperationId")
                .GetString()!;
            if (!Valid(id) || Path.GetFileName(file) != id + ".json")
            {
                throw new InvalidDataException("OPERATION_STORE_INVALID");
            }

            result.Add(bytes);
        }

        return result;
    }

    public void Save(string operationId, byte[] document)
    {
        if (!Valid(operationId) || document.Length > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("OPERATION_ID_INVALID");
        }

        var file = Path.Combine(directory, operationId + ".json");
        EnsureSafePath(file);
        var temporary = file + ".tmp";
        EnsureSafePath(temporary);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(document);
                stream.Flush(true);
            }

            File.Move(temporary, file, true);
            FlushDirectory();
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    // 不沿符号链接写状态；每次读写都重新检查祖先，异常记录保留并拒绝启动，不静默清空历史。
    private static void EnsureSafePath(string file)
    {
        for (var node = new FileInfo(
            Path.GetFullPath(file)
        ) as FileSystemInfo; node is not null; node = node is FileInfo f
            ? f.Directory
            : ((DirectoryInfo)node).Parent)
        {
            if (node.LinkTarget is not null || (node.Exists && (node.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                throw new InvalidDataException("OPERATION_PATH_UNSAFE");
            }
        }
    }

    // Unix 将 rename 所在目录同步后才返回受理；仅 Flush 文件不足以覆盖目录项的崩溃窗口。
    private void FlushDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var descriptor = open(directory, 0);
        if (descriptor < 0)
        {
            throw new IOException("OPERATION_DIRECTORY_FLUSH_FAILED");
        }

        try
        {
            if (fsync(descriptor) != 0)
            {
                throw new IOException("OPERATION_DIRECTORY_FLUSH_FAILED");
            }
        }
        finally
        {
            close(descriptor);
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);
    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int fsync(int descriptor);
    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern int close(int descriptor);
    private static bool Valid(string id) => Regex.IsMatch(id, "^op-[a-f0-9]{32}$", RegexOptions.CultureInvariant);
    public void Dispose() => lease.Dispose();
}
