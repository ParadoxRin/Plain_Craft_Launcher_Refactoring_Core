# PCLCS Client SDK

This optional .NET 10 SDK connects independently maintained clients to a separately deployed PCLCS Host. `Pcl.Contract` contains public DTOs; `Pcl.Client` implements private-pipe transport, session identity, requests, cancellation, subscriptions, reconnect and Host update selection. Neither package includes Core, vendor implementations, Avalonia or the Mac application's self-updater.

Use matching `Pcl.Client` and `Pcl.Contract` versions. Initialize `HostClient`/`LauncherSession`, inspect `HostHello` protocol, target and capabilities, then invoke public operation/workspace APIs. Installation, accounts, credentials, resource resolution and game-process ownership remain in Host. Reuse operation idempotency keys when recovering a request; losing a connection does not prove cancellation.

SDK package versions are selected by the build configuration (Directory.Build.props); inspect the generated packages rather than assuming a fixed example version. The current protocol is 1 and data epoch is 1. It is not published to NuGet.org. Build it with `scripts/build-client-sdk.sh /absolute/fresh-feed`; restore the two `.nupkg` files from that feed. A client can use them without the PCLCS source checkout. `scripts/export-client-workspace.py` exports the reference clients with a copied local package feed for verification or independent maintenance.

Multiple client implementations are supported; a data root still has a single active writer/session. Separate clients may use separate roots. Concurrent attachment to the same live root is rejected, not silently taken over. This split does not introduce a shared multi-user server or arbitrary plugins.

Non-.NET clients implement the same versioned wire semantics rather than loading CLR assemblies. See the repository's minimal-client README and Host protocol/architecture documentation for lifecycle semantics. No Swift/Kotlin/Rust SDK is provided by these packages.

Mac application self-update lives in `Pcl.Desktop.Updates`, outside this SDK. `Pcl.CoreDiagnostics` is a developer tool, not an example of the permitted client boundary. Original source notices and the scoped licenses bundled in the package remain applicable.

Public API classification and compatibility rules: [公共接口与内部实现](../docs/公共接口与内部实现.md). C# public visibility alone is not an SDK stability guarantee.
