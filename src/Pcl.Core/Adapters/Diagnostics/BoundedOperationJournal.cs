using System.Text.Json;
using System.Text.RegularExpressions;
using Pcl.Core.Ports.Operations;

namespace Pcl.Core.Adapters.Diagnostics;
// 只记录受控状态/阶段码；字段中没有自由文本、URL、认证头、命令行或宿主路径，默认不收集调试内容。
public sealed class BoundedOperationJournal(string root) : IOperationJournal
{
    private readonly object gate = new();
    private readonly Dictionary<string, string> last = new();
    public void Write(JournalEntry entry)
    {
        lock (gate)
        {
            var code = Regex.IsMatch(entry.Code, "^[A-Za-z][A-Za-z0-9._:-]{0,95}$")
                ? entry.Code
                : "stage-redacted";
            if (last.GetValueOrDefault(entry.OperationId) == code)
            {
                return;
            }

            last[entry.OperationId] = code;
            var rows = Read(entry.OperationId)
                .Append(
                    entry with
                    {
                        Code = code,
                        Level = entry.Level is "debug" or "info" or "warning" or "error"
                            ? entry.Level
                            : "info"
                    }
                )
                .ToArray();
            bool clipped = rows.Length > 128 || rows.FirstOrDefault()?.Truncated == true;
            rows = rows.TakeLast(128)
                .ToArray();
            if (clipped)
            {
                rows[0] = rows[0] with
                {
                    Truncated = true
                };
            }

            var file = PathFor(entry.OperationId);
            Directory.CreateDirectory(
                Path.GetDirectoryName(file)!
            );
            MeloongCore.FileUtils.WriteAtomic(
                file,
                JsonSerializer.Serialize(rows)
            );
        }
    }

    public IReadOnlyList<JournalEntry> Read(string id)
    {
        lock (gate)
        {
            var file = PathFor(id);
            if (!File.Exists(file))
            {
                return[];
            }

            if (new FileInfo(file).Length > 65536)
            {
                throw new InvalidDataException("JOURNAL_INVALID");
            }

            return JsonSerializer.Deserialize<JournalEntry[]>(
                File.ReadAllText(file)
            ) ?? [];
        }
    }

    private string PathFor(string id)
    {
        if (!Regex.IsMatch(id, "^op-[a-f0-9]{32}$"))
        {
            throw new InvalidDataException("OPERATION_ID_INVALID");
        }

        var file = Path.Combine(root, "operation-logs-v1", id + ".json");
        for (string? path = file; path is not null; path = Path.GetDirectoryName(path))
        {
            if (new FileInfo(path).LinkTarget is not null)
            {
                throw new InvalidDataException("JOURNAL_PATH_UNSAFE");
            }
        }

        return file;
    }
}
