using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
// Host 已计算继承设置；Shell 只按用户当前筛选条件展示快照。
internal sealed class InstanceViewCache
{
    internal InstanceSnapshot Snapshot { get; set; } = new(
        [],
        new(),
        ""
    );
    internal InstanceViewCache Settings => this;
    internal LaunchSettings Global => Snapshot.Global;

    private InstanceItem Item(string path) => Snapshot.Items.Single(
        x => x.Profile.GameDirectory == path
    );
    internal InstancePreferences Preferences(string path) => Item(path)
        .Preferences;
    internal LaunchSettings Effective(string path) => Item(path)
        .Effective;
    internal InstanceItem Patch(string path) => Item(path);
    internal IEnumerable<string> Groups() => Snapshot.Items.Select(
        x => x.Preferences.Group
    )
        .Where(
            x => x.Length > 0
        )
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(
            x => x
        );
    internal IEnumerable<GameProfile> Find(
        string? query,
        string? loader,
        bool favorite,
        bool hidden,
        bool includeHidden,
        string? group
    ) => Snapshot.Items.Where(
        x => !x.Preferences.Deleted && (!favorite || x.Preferences.Favorite) && (!hidden || x.Preferences.Hidden) && (includeHidden || !x.Preferences.Hidden) && (loader is null || x.Profile.Loader == loader) && (group is null || string.Equals(group, x.Preferences.Group, StringComparison.OrdinalIgnoreCase)) && (string.IsNullOrEmpty(query) || x.Profile.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Preferences.Description.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Profile.GameVersion.Contains(query, StringComparison.OrdinalIgnoreCase))
    )
        .Select(
            x => x.Profile
        );
}
