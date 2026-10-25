using Newtonsoft.Json.Linq;
using Pcl.Core.Ports.Operations;
using View = Pcl.Core.Contracts.Workspaces;

namespace Pcl.Core.Adapters.Pages;
public sealed partial class PageExecutionAdapter
{
    // All catalog I/O uses the existing configured source; the UI receives no executable download plan.
    private async Task<object?> ExecuteCatalogAsync(string id, string method, JObject args, CancellationToken ct)
    {
        string Text(string key) => (string? )args[key] ?? "";
        var kind = (ResourceKind)((int? )args["kind"] ?? 0);
        if (!Enum.IsDefined(kind))
        {
            throw new OperationInstallException("RESOURCE_KIND_INVALID");
        }

        using var catalog = new ResourceCatalog(paths: source.Paths);
        switch (method)
        {
            case "workspace.catalog.categories":
                return await catalog.CategoriesAsync(kind, ct);
            case "workspace.catalog.search":
                var found = await catalog.SearchAsync(
                    kind,
                    Text("query"),
                    Text("gameVersion"),
                    Text("loader"),
                    (int? )args["offset"] ?? 0,
                    Text("sort")is "downloads" or "updated"
                        ? Text("sort")
                        : "relevance",
                    ct,
                    (string? )args["category"]
                );
                Save(
                    id,
                    "browser-projects",
                    found.Projects.ToArray()
                );
                return new View.ResourceSearch(
                    found.Projects.Select(
                        (p, i) => new View.ResourceProject(
                            p.Id,
                            p.Slug,
                            p.Title,
                            p.Description,
                            p.Author,
                            p.Downloads,
                            (View.ResourceKind)p.Kind,
                            p.WebUrl,
                            ResourceAliases.WikiUrl(p.Slug, p.Title),
                            p.IconUrl is null
                                ? null
                                : id + ":" + i
                        )
                    )
                        .ToArray(),
                    found.Offset,
                    found.Total,
                    ResourceAliases.SearchQuery(
                        Text("query")
                    )
                );
            case "workspace.catalog.versions":
                return (await catalog.VersionsAsync(
                    Text("projectId"),
                    kind,
                    Text("gameVersion"),
                    Text("loader"),
                    ct
                )).Select(
                    p => new View.ResourceRelease(
                        p.Id,
                        p.ProjectId,
                        p.Name,
                        p.Number,
                        p.Channel,
                        p.Published,
                        p.GameVersions.ToArray(),
                        p.Loaders.ToArray(),
                        new(p.File.Name, p.File.Size)
                    )
                )
                    .ToArray();
            case "workspace.catalog.description":
                return await catalog.ProjectDescriptionAsync(
                    Text("projectId"),
                    ct
                );
            case "workspace.catalog.changelog":
                return await catalog.ChangelogAsync(
                    Text("releaseId"),
                    ct
                );
            case "workspace.catalog.icon":
                var(operation, index) = Selection(
                    Text("selection")
                );
                var projects = Read<ResourceProject[]>(operation, "browser-projects");
                if (index < 0 || index >= projects.Length)
                {
                    throw new OperationInstallException("WORKSPACE_SELECTION_INVALID");
                }

                if (!Uri.TryCreate(projects[index].IconUrl, UriKind.Absolute, out var url) || url.Scheme != "https" || url.Host != "cdn.modrinth.com")
                {
                    return null;
                }

                using (var handler = NetworkOptions.Load(source.Paths)
                    .CreateHandler())
                    using (var http = new HttpClient(handler)
                    {
                        Timeout = TimeSpan.FromSeconds(15)
                    }

                    )
                    {
                        handler.AllowAutoRedirect = false;
                        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                        response.EnsureSuccessStatusCode();
                        using var input = await response.Content.ReadAsStreamAsync(ct);
                        using var memory = new MemoryStream();
                        var buffer = new byte[8192];
                        int count;
                        while ((count = await input.ReadAsync(buffer, ct)) > 0)
                        {
                            if (memory.Length + count > 2_000_000)
                            {
                                throw new OperationInstallException("IMAGE_TOO_LARGE");
                            }

                            await memory.WriteAsync(
                                buffer.AsMemory(0, count),
                                ct
                            );
                        }

                        var file = WorkspaceOutput(id, "resource-icon.bin");
                        await File.WriteAllBytesAsync(
                            file,
                            memory.ToArray(),
                            ct
                        );
                        return RegisterWorkspaceExport(id, file);
                    }

            default:
                throw new OperationInstallException("WORKSPACE_METHOD_UNKNOWN");
        }
    }
}
