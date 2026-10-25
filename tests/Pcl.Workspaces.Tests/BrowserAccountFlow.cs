using System.Text.Json;
using System.Security.Cryptography;
using Pcl.Client;
using Pcl.Core.Contracts.Workspaces;
using Pcl.Core.Contracts.Operations;

internal static class BrowserAccountFlow
{
    // Exercise production Client/Host and public services against a disposable cached game root.
    internal static async Task Run(string root, string report)
    {
        root = Path.GetFullPath(root);
        var checks = new List<string>();
        void Check(bool ok, string name)
        {
            if (!ok)
            {
                throw new Exception(name);
            }

            checks.Add(name);
            Console.WriteLine(name);
        }

        LauncherSession Connect()
        {
            var native = Environment.GetEnvironmentVariable("PCL_HOST_TEST_EXE");
            return new(
                new HostClient(
                    native ?? Path.GetFullPath(".tooling/dotnet/dotnet"),
                    native is null
                        ? Path.GetFullPath("src/Pcl.Host/bin/Debug/net10.0/Pcl.Host.dll")
                        : "",
                    root,
                    reconnectable: true
                )
            );
        }

        await using var session = Connect();
        try
        {
            await session.InitializeAsync();
            var api = new WorkspaceApi(session.Workspaces);
            var start = await api.CallAsync<AccountPageSnapshot>("workspace.account.list", null);
            Check(
                start.Accounts.All(
                    x => x.Kind == AccountKind.Offline
                ),
                "isolated-offline-account-index-without-keychain"
            );
            var first = await api.CallAsync<LauncherAccount>(
                "workspace.account.addOffline",
                null,
                new
                {
                    name = "PCL030_Skin"
                }
            );
            var second = await api.CallAsync<LauncherAccount>(
                "workspace.account.addOffline",
                null,
                new
                {
                    name = "PCL030_Second"
                }
            );
            var chosen = await api.CallAsync<AccountPageSnapshot>(
                "workspace.account.select",
                null,
                new
                {
                    accountId = first.Id
                }
            );
            Check(
                chosen.SelectedId == first.Id && chosen.Accounts.Any(
                    x => x.Id == second.Id
                ),
                "create-and-select-offline-accounts-through-host"
            );
            var fetched = await api.CallAsync<SkinPreview>(
                "workspace.account.skinFetch",
                null,
                new
                {
                    accountId = first.Id,
                    name = "Notch"
                }
            );
            Check(
                fetched.PlayerName == "Notch" && fetched.Png.Length > 100 && fetched.Png[0] == 137,
                "official-player-name-skin-fetch-and-normalized-png"
            );
            var refused = false;
            try
            {
                await api.CallAsync<bool>(
                    "workspace.account.skinSaveFetched",
                    null,
                    new
                    {
                        accountId = second.Id,
                        handle = fetched.Handle
                    }
                );
            }
            catch (OperationRejectedException e)when (e.Code == "WORKSPACE_SELECTION_INVALID")
            {
                refused = true;
            }

            Check(refused, "skin-preview-cannot-be-saved-to-another-account");
            await api.CallAsync<bool>(
                "workspace.account.skinSaveFetched",
                null,
                new
                {
                    accountId = first.Id,
                    handle = fetched.Handle
                }
            );
            var saved = await api.CallAsync<SkinState>(
                "workspace.account.skinState",
                null,
                new
                {
                    accountId = first.Id
                }
            );
            Check(
                saved.PlayerName == "Notch" && saved.Png!.SequenceEqual(fetched.Png),
                "save-public-skin-and-read-back-through-host"
            );
            var latest = await api.CallAsync<SkinPreview>(
                "workspace.account.skinRefresh",
                null,
                new
                {
                    accountId = first.Id
                }
            );
            Check(
                latest.PlayerName == "Notch" && latest.Png.Length > 100,
                "refresh-public-skin-from-official-services"
            );
            var export = Path.Combine(root, "exported-public-skin.png");
            await api.ExportAsync(
                "workspace.account.skinExport",
                null,
                new
                {
                    accountId = first.Id
                },
                export
            );
            Check(
                File.Exists(export) && new FileInfo(export).Length > 100,
                "skin-export-using-verified-host-blob"
            );
            var invalid = Path.Combine(root, "invalid-skin.png");
            await File.WriteAllTextAsync(invalid, "not png");
            var rejected = false;
            try
            {
                await api.CallAsync<SkinState>(
                    "workspace.account.skinImport",
                    null,
                    new
                    {
                        accountId = first.Id,
                        path = invalid,
                        model = "wide"
                    }
                );
            }
            catch (OperationRejectedException)
            {
                rejected = true;
            }

            var afterInvalid = await api.CallAsync<SkinState>(
                "workspace.account.skinState",
                null,
                new
                {
                    accountId = first.Id
                }
            );
            Check(
                rejected && afterInvalid.Png!.SequenceEqual(latest.Png),
                "invalid-skin-import-keeps-current-skin"
            );
            var imported = await api.CallAsync<SkinState>(
                "workspace.account.skinImport",
                null,
                new
                {
                    accountId = second.Id,
                    path = export,
                    model = "wide"
                }
            );
            Check(
                imported.Png!.Length > 100 && imported.Model == "wide",
                "local-skin-import-host-validation"
            );
            foreach (var preset in new[]
            {
                "steve",
                "alex",
                "random"
            }

            )
            {
                var result = await api.CallAsync<SkinState>(
                    "workspace.account.skinPreset",
                    null,
                    new
                    {
                        accountId = second.Id,
                        preset
                    }
                );
                Check(
                    result.Png!.Length > 100 && (preset != "alex" || result.Model == "slim"),
                    "installed-client-skin-preset-" + preset
                );
            }

            await api.CallAsync<bool>(
                "workspace.account.skinRemove",
                null,
                new
                {
                    accountId = second.Id
                }
            );
            Check(
                (await api.CallAsync<SkinState>(
                    "workspace.account.skinState",
                    null,
                    new
                    {
                        accountId = second.Id
                    }
                )).Png is null,
                "remove-offline-skin-through-host"
            );
            // A login challenge cannot survive Host restart or be invented by a page.
            var expired = false;
            try
            {
                await api.CallAsync<LauncherAccount>(
                    "workspace.account.externalSelect",
                    null,
                    new
                    {
                        handle = "missing",
                        profileId = "fake",
                        remember = false
                    }
                );
            }
            catch (OperationRejectedException e)when (e.Code == "LOGIN_CHALLENGE_EXPIRED")
            {
                expired = true;
            }

            Check(expired, "external-login-rejects-missing-memory-challenge");
            const string dummySecret = "PCL030_TEST_ONLY_NOT_A_PASSWORD";
            var secretHandle = await api.SecretAsync(dummySecret);
            var badServer = false;
            try
            {
                await api.CallAsync<ExternalChallenge>(
                    "workspace.account.externalLogin",
                    null,
                    new
                    {
                        address = "http://invalid.invalid",
                        identifier = "fixture",
                        secretHandle
                    }
                );
            }
            catch (OperationRejectedException)
            {
                badServer = true;
            }

            var consumed = false;
            try
            {
                await api.CallAsync<ExternalChallenge>(
                    "workspace.account.externalLogin",
                    null,
                    new
                    {
                        address = "http://invalid.invalid",
                        identifier = "fixture",
                        secretHandle
                    }
                );
            }
            catch (OperationRejectedException e)when (e.Code == "SECRET_REENTRY_REQUIRED")
            {
                consumed = true;
            }

            Check(badServer && consumed, "external-password-handle-consumed-on-first-attempt");
            Check(
                !Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
                    .Any(
                        file => File.ReadAllText(file)
                            .Contains(dummySecret, StringComparison.Ordinal)
                    ),
                "external-password-not-written-to-receipts-or-index"
            );
            var categories = await api.CallAsync<string[]>(
                "workspace.catalog.categories",
                null,
                new
                {
                    kind = (int)ResourceKind.Mod
                }
            );
            Check(categories.Length > 3, "live-modrinth-categories-through-host");
            ResourceSearch? mods = null;
            foreach (var kind in new[]
            {
                ResourceKind.Mod,
                ResourceKind.Shader,
                ResourceKind.ResourcePack,
                ResourceKind.Modpack
            }

            )
            {
                var result = await api.CallAsync<ResourceSearch>(
                    "workspace.catalog.search",
                    null,
                    new
                    {
                        kind = (int)kind,
                        query = "",
                        gameVersion = "1.21.1",
                        loader = "",
                        offset = 0,
                        sort = "downloads"
                    }
                );
                Check(
                    result.Projects.Length > 0 && result.Projects.All(
                        x => x.Kind == kind
                    ),
                    "live-default-catalog-search-" + kind
                );
                if (kind == ResourceKind.Mod)
                {
                    mods = result;
                }
            }

            var project = mods!.Projects.First(
                x => x.IconHandle is not null
            );
            var next = await api.CallAsync<ResourceSearch>(
                "workspace.catalog.search",
                null,
                new
                {
                    kind = 0,
                    query = "",
                    gameVersion = "1.21.1",
                    loader = "",
                    offset = 20,
                    sort = "downloads"
                }
            );
            Check(
                next.Offset == 20 && next.Projects.Length > 0 && next.Projects[0].Id != mods.Projects[0].Id,
                "live-catalog-pagination"
            );
            var versions = await api.CallAsync<ResourceRelease[]>(
                "workspace.catalog.versions",
                null,
                new
                {
                    kind = 0,
                    projectId = project.Id,
                    gameVersion = "1.21.1",
                    loader = "fabric"
                }
            );
            Check(
                versions.Length > 0 && versions.All(
                    x => x.GameVersions.Contains("1.21.1") && x.Loaders.Contains("fabric")
                ),
                "live-catalog-version-and-loader-filter"
            );
            var description = await api.CallAsync<string>(
                "workspace.catalog.description",
                null,
                new
                {
                    projectId = project.Id
                }
            );
            var changelog = await api.CallAsync<string>(
                "workspace.catalog.changelog",
                null,
                new
                {
                    releaseId = versions[0].Id
                }
            );
            Check(
                description.Length > 0 && changelog is not null,
                "live-project-description-and-release-changelog"
            );
            var icon = await api.ReadBinaryAsync(
                "workspace.catalog.icon",
                null,
                new
                {
                    selection = project.IconHandle
                }
            );
            Check(icon is { Length: > 100 and < 2000000 }, "live-icon-download-and-verified-blob");
            var displays = await api.CallAsync<ManagedResource[]>(
                "workspace.browser.display",
                "1.21.1",
                new
                {
                    kind = (int)ResourceKind.ResourcePack
                }
            );
            Check(displays is not null, "managed-resource-presentation-from-host");
            await api.CallAsync<AccountPageSnapshot>(
                "workspace.account.logout",
                null,
                new
                {
                    accountId = second.Id
                }
            );
            await api.CallAsync<AccountPageSnapshot>(
                "workspace.account.remove",
                null,
                new
                {
                    accountId = second.Id
                }
            );
            Check(
                !(await api.CallAsync<AccountPageSnapshot>("workspace.account.list", null)).Accounts.Any(
                    x => x.Id == second.Id
                ),
                "logout-and-remove-account-through-host"
            );
            await session.ShutdownAsync();
            await using (var reopened = Connect())
            {
                await reopened.InitializeAsync();
                var restored = new WorkspaceApi(reopened.Workspaces);
                var index = await restored.CallAsync<AccountPageSnapshot>("workspace.account.list", null);
                var skin = await restored.CallAsync<SkinState>(
                    "workspace.account.skinState",
                    null,
                    new
                    {
                        accountId = first.Id
                    }
                );
                Check(
                    index.SelectedId == first.Id && skin.PlayerName == "Notch" && skin.Png!.SequenceEqual(latest.Png),
                    "host-restart-restores-selected-account-and-skin"
                );
                var again = await restored.ReadBinaryAsync(
                    "workspace.catalog.icon",
                    null,
                    new
                    {
                        selection = project.IconHandle
                    }
                );
                Check(
                    again!.SequenceEqual(icon!),
                    "host-restart-restores-private-catalog-selection"
                );
                await reopened.ShutdownAsync();
            }

            await File.WriteAllTextAsync(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        Status = "passed",
                        Checks = checks,
                        Limitations = new[]
                        {
                            "No real external-authentication account supplied; challenge failure only.",
                            "No Microsoft account touched in this offline-root suite."
                        }
                    },
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }
                )
            );
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        Status = "failed",
                        Checks = checks,
                        Error = ex.ToString()
                    },
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }
                )
            );
            try
            {
                await session.ShutdownAsync();
            }
            catch
            {
            }

            throw;
        }
    }
}
