# Core diagnostics

This development tool preserves the original in-process Pcl.Cli commands. It directly references Core/Application and is not a client SDK example or a user update component.

Build `PCLCS.Tools.slnx`, then invoke `tools/Pcl.CoreDiagnostics/bin/Release/net10.0/Pcl.CoreDiagnostics.dll` with the previous diagnostic arguments. The remote `host-status`, `host-install` and `host-launch` commands belong to `src/Pcl.Cli` instead.

Use an isolated data root for development. Do not run in-process diagnostics against a data root owned by a running Host. Real-account/game acceptance scripts retain their existing explicit account and user-interaction requirements.
