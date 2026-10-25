using System.Text.Json;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Operations;

namespace Pcl.Client;
/// <summary>页面操作传输与事件映射；只依赖 Contract，不计算安装计划或读取账号凭据。</summary>
public sealed class HostPageOperations(HostClient client) : IPageOperations
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };
    public Task<OperationAcceptance> AcceptAsync(PageCommand command, CancellationToken ct = default) => client.Call<OperationAcceptance>("page.begin", command, ct);
    public Task<PageSnapshot> GetAsync(string id, CancellationToken ct = default) => client.Call<PageSnapshot>(
        "page.get",
        new
        {
            operationId = id
        },
        ct
    );
    public Task<PageSnapshot> CancelAsync(string id, CancellationToken ct = default) => client.Call<PageSnapshot>(
        "page.cancel",
        new
        {
            operationId = id
        },
        ct
    );
    public async Task<PageSnapshot> WaitAsync(string id, CancellationToken ct = default)
    {
        await foreach (var row in WatchAsync(id, ct))
        {
            if (row.Terminal)
            {
                return row;
            }
        }

        throw new OperationRejectedException("SUBSCRIPTION_CLOSED");
    }

    public async IAsyncEnumerable<PageSnapshot> WatchAsync(
        string id,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
    )
    {
        await using var subscription = await client.SubscribeAsync("page", id, ct);
        await foreach (var change in subscription.ReadAsync(ct))
        {
            var row = change.Snapshot.Deserialize<PageSnapshot>(Json)!;
            yield return row;
            if (row.Terminal)
            {
                yield break;
            }
        }
    }

    public async Task<IReadOnlyList<PageSnapshot>> ListAsync(CancellationToken ct = default)
    {
        var resource = await client.OpenResourceAsync("page-operations", ct: ct);
        try
        {
            var result = new List<PageSnapshot>();
            for (int offset = 0;; offset += 50)
            {
                var page = await client.ReadPageAsync(resource.ResourceId, offset, 50, ct);
                result.AddRange(
                    page.Items.Select(
                        x => x.Deserialize<PageSnapshot>(Json)!
                    )
                );
                if (page.Eof)
                {
                    return result;
                }
            }
        }
        finally
        {
            try
            {
                await client.CloseResourceAsync(resource.ResourceId);
            }
            catch (OperationRejectedException)
            {
            }
        }
    }
}
