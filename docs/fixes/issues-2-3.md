# Issues #2 / #3: detached logs and macOS credential bounds

## Behavior

- Keychain writes reject values above 65,536 UTF-8 bytes before native queries or updates. Reads use the same bound. Account/service identity is unchanged.
- Unix detached games use a small native companion embedded in `Pcl.Core`. The companion forks a logger and then execs the game, retaining the owned game PID and exit status. The logger receives merged stdout/stderr and survives the launching Host process.
- Sensitive values arrive over a private stdin pipe, not additional command arguments, environment variables, or temporary credential files. Redaction matches UTF-8 bytes across read boundaries and newlines, including short registered secrets. Bounded lookahead depends on a configuration limit of 1 MiB / 4,096 values.
- The raw log is limited to 16 MiB, including its truncation marker. After truncation or a file write failure the logger continues draining output. Each launch resets its log; existing logs are no longer appended indefinitely. Log targets must be regular files, symlinks are rejected, and permissions are restricted to the owner.
- Host stdout observes logger completion only. Game output does not depend on a Host-owned output pump. Soft termination still targets the game PID; forced termination retains the existing owned-tree semantics.

## Build and scope

Unix builds require a C11 `cc` toolchain. macOS embeds a universal arm64/x86_64 companion; Linux builds use the native toolchain. Build Linux targets with a matching native SDK/toolchain and macOS targets on macOS. Windows detached mode remains unsupported; managed Windows processes are unchanged.

The executable is extracted to a unique owner-only temporary directory and removed by the companion after log drain (or startup failure). Abruptly killing the logger can leave this credential-free temporary executable for OS temporary-file cleanup. Missing/unusable helpers fail closed instead of reverting to unprotected raw redirection. The log protection covers stdout/stderr only, not files written independently by games or mods. No real credential disclosure is alleged by the synthetic regression fixtures. Packaging/signing acceptance is separate from these source-level tests.

## Regression harness

```
dotnet run --project tests/Pcl.LogProtection.Tests -- /absolute/fresh/test-root
```

On macOS, `--keychain` additionally tests synthetic native credential values at the UTF-8 boundary, rejected oversize updates, and unchanged previous values. CI runs it in its temporary test keychain. The harness tests actual parent-process exit, continued redaction, dual-stream and chunk/newline boundary matching, final output drain, exit codes, the hard file cap, repeated-log reset, symlink rejection, and soft termination. Use a fresh directory per run.

## Local validation (2026-11-01, Apple Silicon macOS)

- Core regression harness: 241/241 passed, using its local HTTP fixtures.
- Log protection harness: passed UTF-8 preflight, streaming redaction, hard cap/drain, existing-log reset/symlink rejection, owned-PID soft stop, and actual parent-process exit with continuing redaction.
- Native Keychain synthetic test: passed 65,535/65,536-byte roundtrips and oversize rejection preserving previous values; synthetic item deleted in `finally`.
- Final build: zero warnings/errors using the existing package cache with audit disabled. An earlier restore reported NU1900; this is not a dependency vulnerability audit.
- The initial sandbox core run could not bind its loopback server; the authorized run above passed. The initial log harness exposed an early-unlink macOS executable termination; cleanup was moved into the companion lifecycle and the harness then passed.
- Linux/Windows CI and packaged-app signing are separate checks; no real game-window or Microsoft-account acceptance is claimed here.
