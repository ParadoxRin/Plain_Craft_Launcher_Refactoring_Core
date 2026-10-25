namespace Pcl.Core;
/// <summary>A query/filter snapshot. Page fetches commit only after success; cached navigation never reorders remote results.</summary>
public sealed class UnifiedSearchSession(
    ResourceSearchQuery query,
    Func<ResourceSearchQuery, CancellationToken, Task<UnifiedSearchResult>> fetch
)
{
    private readonly SortedDictionary<int, UnifiedSearchResult> pages = [];
    private readonly SemaphoreSlim gate = new(1, 1);
    public int Page { get; private set; } = -1;
    public int CachedPages => pages.Count;
    public UnifiedSearchResult? Current => Page < 0
        ? null
        : Result(Page);
    public int FilteredDuplicates => Page < 0
        ? 0
        : pages[Page].Hits.Count - Current!.Hits.Count;

    // [实现] GoToAsync 的实现：执行 await gate.WaitAsync(ct)；执行受异常处理保护的操作。
    public async Task<UnifiedSearchResult> GoToAsync(int page, bool retry = false, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            // [实现] 在异常保护范围内：按 page < 0 || page > 5000 || page > pages.Count 分支处理；按 page == pages.Count && page > 0 && !pages[page - 1].HasNext 分支处理；按 !pages.ContainsKey(page) || retry 分支处理。
            if (page < 0 || page > 5000 || page > pages.Count)
                throw new ArgumentOutOfRangeException(
                    nameof(page)
                );
            if (page == pages.Count && page > 0 && !pages[page - 1].HasNext)
                throw new InvalidOperationException("已到最后一页。");
            if (!pages.ContainsKey(page) || retry)
            {
                // [实现] 条件成立（!pages.ContainsKey(page) || retry）时：准备 var received = await fetch( query with { Page = page }, ct )；执行 ct.ThrowIfCancellationRequested()；按 retry && pages.TryGetValue(page, out var prior) 分支处理。
                var received = await fetch(
                    query with
                    {
                        Page = page
                    },
                    ct
                );
                ct.ThrowIfCancellationRequested();
                // Retrying a failed source must not discard successful source snapshots or change their pagination.
                if (retry && pages.TryGetValue(page, out var prior))
                {
                    // [实现] 条件成立（retry && pages.TryGetValue(page, out var prior)）时：准备 var sources = prior.Sources.Select( x => x.Error is null ? x : received.Sources.FirstOrDefault(…；执行 received = new( UnifiedResourceSearch.Merge( sources.SelectMany( x => x.Hits ) ), sources )。
                    var sources = prior.Sources.Select(
                        x => x.Error is null
                            ? x
                            : received.Sources.FirstOrDefault(
                                y => y.Source == x.Source
                            ) ?? x
                    )
                        .ToArray();
                    received = new(
                        UnifiedResourceSearch.Merge(
                            sources.SelectMany(
                                x => x.Hits
                            )
                        ),
                        sources
                    );
                }

                pages[page] = received;
            }

            ct.ThrowIfCancellationRequested();
            Page = page;
            return Result(page);
        }
        finally
        {
            // [实现] 无论成功或失败，退出前：执行 gate.Release()。
            gate.Release();
        }
    }

    // [实现] Result 的实现：准备 var owners = new Dictionary<string, int>(StringComparer.Ordinal)；遍历 pages 处理 pair；准备 var enriched = UnifiedResourceSearch.Merge( pages.Values.SelectMany( x => x.Hits ) )。
    private UnifiedSearchResult Result(int page)
    {
        // Identity is source-qualified. Equal titles across stores never establish project identity.
        var owners = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in pages)
            foreach (var hit in pair.Value.Hits)
                owners.TryAdd(hit.Identity, pair.Key);
        var enriched = UnifiedResourceSearch.Merge(
            pages.Values.SelectMany(
                x => x.Hits
            )
        );
        return new(
            enriched.Where(
                x => owners[x.Identity] == page
            )
                .ToArray(),
            pages[page].Sources
        );
    }
}
