namespace Pcl.Core.Contracts.Workspaces;
// 搜索只传显示数据和 Host 生成的选择句柄；下载地址、校验信息与依赖留在 Host。
public sealed record SearchHit(
    string Selection,
    string Title,
    string Description,
    string[] Sources,
    string? LocalDirectory,
    string? WebUrl,
    string? McmodUrl,
    string? DownloadPage
)
{
    public override string ToString() => Title + " · " + string.Join(" + ", Sources);
}

public sealed record SearchPage(
    string Session,
    int Page,
    bool HasNext,
    int FilteredDuplicates,
    SearchHit[] Hits,
    string[] SourceStatus,
    bool HasErrors
)
{
    // Additive field: older clients keep using SourceStatus and HasErrors.
    public SearchSourceAvailability[] Availability { get; init; } = [];
}
public sealed record SearchSourceAvailability(string Source, bool Available, string Code, string Message, bool Retryable);
public sealed record SearchRelease(string Selection, string Name, string FileName, string? Page, bool Manual)
{
    public string Source { get; init; } = "";
    public string[] GameVersions { get; init; } = [];
    public string[] Loaders { get; init; } = [];
    public string Channel { get; init; } = "";

    public override string ToString() => Source + " · " + Name + " · " + Channel + (Manual
        ? " · 需手动下载"
        : "");
}

public sealed record SearchVersions(SearchRelease[] Releases, string Message);
public sealed record SearchInstalled(int Files, string[] Warnings, PackInspection? Pack);
