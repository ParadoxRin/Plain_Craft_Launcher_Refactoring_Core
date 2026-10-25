using Newtonsoft.Json.Linq;

namespace Pcl.Core;
/// <summary>明确选择来源与失败回退；只有元数据请求走镜像，不转发账号、Cookie 或授权头。</summary>
public static class MetadataSourcePolicy
{
    // [实现] Sources 的实现：计算并返回 mirror.Length == 0 || mode == "official" ? [ official ] : mode == "mirror" ? [ mirror, official…。
    public static string[] Sources(string official, string mirror, string mode) => mirror.Length == 0 || mode == "official"
        ? [
            official
        ]
        : mode == "mirror"
            ? [
                mirror,
                official
            ]
            : [
                official,
                mirror
            ];
    // [实现] ReadAsync 的实现：准备 Exception? last = null；遍历 Sources(official, mirror, mode) 处理 url；抛出 new IOException("所有配置来源读取失败。", last)。
    public static async Task<string> ReadAsync(HttpClient client, string official, string mirror, string mode, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var url in Sources(official, mirror, mode))
        {
            // [实现] 对 Sources(official, mirror, mode) 中的每一项：准备 var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct)；执行 timeout.CancelAfter( TimeSpan.FromSeconds(12) )；执行受异常处理保护的操作。
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(
                TimeSpan.FromSeconds(12)
            );
            try
            {
                // [实现] 在异常保护范围内：准备 var text = await MetadataRequest.GetAsync(client, url, timeout.Token)；执行 JToken.Parse(text)；返回 text。
                var text = await MetadataRequest.GetAsync(client, url, timeout.Token);
                JToken.Parse(text);
                return text;
            }
            catch (Exception ex)when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or Newtonsoft.Json.JsonException)
            {
                // [实现] 捕获 (Exception ex) 后：执行 last = ex。
                last = ex;
            }
        }

        throw new IOException("所有配置来源读取失败。", last);
    }
}
