using System.Text.Json;

namespace Pcl.Core;
/// <summary>可开关、限长的运行诊断；只接收固定事件与数值，不记录请求参数、账户、令牌或命令行。</summary>
public static class RuntimeTrace
{
    private static readonly object Gate = new();
    private static string? destination;
    // 根据设置切换当前数据目录的日志目标；关闭后立即停止写入。
    public static void Configure(AppPaths paths, bool enabled)
    {
        lock (Gate)
        {
            destination = enabled
                ? Path.Combine(paths.Root, "logs", "runtime-debug.jsonl")
                : null;
            if (destination is not null)
            {
                Directory.CreateDirectory(
                    Path.GetDirectoryName(destination)!
                );
            }
        }

        Write("debug.enabled");
    }

    // 在锁内轮换并追加结构化事件，避免多个后台任务交错写入。
    public static void Write(string operation, long value = 0)
    {
        lock (Gate)
        {
            if (destination is null)
            {
                return;
            }

            try
            {
                // 单份最多 2 MiB，加一个轮换备份；磁盘日志错误不阻止启动器操作。
                if (File.Exists(destination) && new FileInfo(destination).Length > 2 * 1024 * 1024)
                {
                    File.Move(destination, destination + ".previous", true);
                }

                File.AppendAllText(
                    destination,
                    JsonSerializer.Serialize(
                        new
                        {
                            time = DateTimeOffset.UtcNow,
                            operation,
                            value
                        }
                    ) + "\n"
                );
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
