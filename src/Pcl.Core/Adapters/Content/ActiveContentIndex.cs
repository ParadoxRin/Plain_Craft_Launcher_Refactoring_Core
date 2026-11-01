using System.Reflection;
using PCLCS;

namespace Pcl.Core.Adapters.Content;

/// <summary>One immutable selection per Host process, shared by search and identity projection.</summary>
public static class ActiveContentIndex
{
    private static readonly Lazy<ContentSelection> Selected = new(Select);
    internal static IReadOnlyList<WikiEntry> Entries => Selected.Value.Entries;
    public static ContentSelection Status => Selected.Value;

    private static ContentSelection Select()
    {
        var policyPath = Environment.GetEnvironmentVariable("PCL_CONTENT_POLICY")
            ?? Path.Combine(AppContext.BaseDirectory, "content-update-policy.json");
        var root = Environment.GetEnvironmentVariable("PCL_CONTENT_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PlainCraftLauncherRefactoring", "content", "wiki-project-index");
        try
        {
            if (File.Exists(policyPath))
            {
                var version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
                // AssemblyVersion has four components; release compatibility uses three.
                return ContentPack.Load(root, ContentPack.ReadPolicy(policyPath), new Version(version.Major, version.Minor, version.Build));
            }
        }
        catch (Exception ex) when (ContentPack.Expected(ex))
        {
            return new(WikiEntry.All.Value, McmodCatalog.IndexProvenance, 0, ex.GetType().Name);
        }
        return new(WikiEntry.All.Value, McmodCatalog.IndexProvenance, 0, null);
    }
}
