# SDK-only developer quickstart

.NET 10, matching Pcl.Client/Pcl.Contract 0.42.1 NuGet packages, and a separately extracted Host are required. No Core project/source reference is used.

```sh
dotnet restore Pcl.DeveloperQuickstart.csproj --source /absolute/local-sdk-feed
dotnet build Pcl.DeveloperQuickstart.csproj -c Release --no-restore
dotnet bin/Release/net10.0/Pcl.DeveloperQuickstart.dll /absolute/host/Pcl.Host /absolute/fresh-data-root
```

Optional third argument: a real Java executable to inspect through Host. The default sample does hello/content status and explicitly shuts down its isolated Host. It does not install Minecraft, create accounts, change existing profiles, or configure automatic updates.

`Recipes.cs` compiles the installation, subscription, cancellation and launch call signatures. These recipes are not automatically executed: installation downloads files; launch needs an existing instance/account and actual user consent. Retain idempotency keys across timeouts and distinguish accepted operation, terminal result and game process lifetime.

Use the existing Pcl.MinimalClient example for an interactive complete workflow. Preserve the licenses and product attribution described in the source root LICENSE.md and THIRD-PARTY-NOTICES.md.
