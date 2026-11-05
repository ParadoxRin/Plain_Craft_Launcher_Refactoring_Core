using System.Collections.Frozen;
using Pcl.Core.Contracts.Pages;

namespace Pcl.Core.Application.Pages;

/// <summary>Reviewed resource ownership, not the protocol's retry-safe Reads classification.</summary>
public static class PageResourcePolicy
{
    // These methods return snapshots/remote catalogs. Their private result records are keyed by
    // operation ID; network metadata caches use the adapter's atomic download path. One read worker
    // owns mutable search sessions. Java inspect/discover are controlled external probes, not pure reads.
    // Account challenges and prepared write batches stay on writer lane.
    private static readonly FrozenSet<string> ConcurrentReads = new[]
    {
        "instance.list", "component.versions", "artifact.list", "artifact.notes",
        "workspace.shell.profile", "workspace.shell.diagnostic", "workspace.shell.gameExit",
        "workspace.shell.classifyDrop", "workspace.shell.releaseNotice",
        "workspace.desktop.versions", "workspace.desktop.loaderVersions", "workspace.desktop.optiFabricValidate",
        "workspace.catalog.categories", "workspace.catalog.search", "workspace.catalog.versions",
        "workspace.catalog.description", "workspace.catalog.changelog", "workspace.catalog.icon",
        "workspace.settings.networkTargets", "workspace.settings.read", "workspace.settings.home",
        "workspace.instances.list", "workspace.instances.preferences", "workspace.instances.settings",
        "workspace.instances.icon", "workspace.instances.listTrash",
        "workspace.java.inspect", "workspace.java.discover", "workspace.link.status",
        "workspace.search.page", "workspace.search.versions",
        "workspace.browser.backends",
        "workspace.packs.exportOptions"
    }.ToFrozenSet(StringComparer.Ordinal);

    // ZIP/resource scans keep file handles open without delete sharing on Windows. Serialize them
    // with installs/removals, while still permitting read-only browsing during a running game.
    private static readonly FrozenSet<string> SerializedReads = new[]
    {
        "workspace.browser.list", "workspace.browser.display"
    }.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsConcurrentRead(string method) => ConcurrentReads.Contains(method);

    // AccountManager owns an immutable public index, but account mutations also update secrets and
    // sessions. Never infer independence merely because a method is called read/discover/preview.
    public static bool ConflictsWithAccounts(string method) =>
        method.StartsWith("workspace.account.", StringComparison.Ordinal) ||
        !(WorkspaceMethods.Known.Contains(method) || method is "instance.list" or "component.versions" or
            "component.confirmation" or "component.preview" or "component.apply" or "artifact.list" or
            "artifact.download" or "artifact.notes");

    public static bool CanRunDuringGame(string method) => IsConcurrentRead(method) || SerializedReads.Contains(method) ||
        method is "workspace.link.start" or "workspace.link.stop";
}
