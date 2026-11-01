using System.Diagnostics;
using System.Security.Cryptography;
using Pcl.Core.Adapters.Mac;
using Pcl.Core.Adapters.Processes;
using Pcl.Core.Ports.Processes;

var root = Path.GetFullPath(args[0]);
Directory.CreateDirectory(root);
const string secret = "synthetic-token-中文-123456";
ProcessSpec Shell(string script) => new("/bin/sh", ["-c", script], root) { SensitiveValues = [secret, "abc", "multi\nline"] };
void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
if (args.Contains("--parent"))
{
    using var host = new SystemGameProcessHost();
    await host.StartAsync(Shell("sleep 1; printf 'synthetic-token-中文-123456\\nsurvived\\n'"), new(Path.Combine(root, "survival.log"), true));
    return;
}
// Reject before loading native libraries on every platform, including multi-byte input.
var credentials = new KeychainAccountSecrets(root);
foreach (var value in new[] { new string('x', 65537), new string('界', 21846) })
{
    try { credentials.Set("synthetic-overflow", value); throw new Exception("accepted overflow"); }
    catch (CryptographicException) { }
}
Console.WriteLine("PASS keychain UTF-8 overflow preflight");
if (args.Contains("--keychain") && OperatingSystem.IsMacOS())
{
    var id = "log-protection-test-" + Guid.NewGuid().ToString("N");
    try
    {
        foreach (var value in new[] { new string('x', 65535), new string('界', 21845) + "x" })
        {
            credentials.Set(id, value);
            Check(credentials.Get(id) == value, "boundary roundtrip");
            try { credentials.Set(id, value + "界"); throw new Exception("accepted native overflow"); }
            catch (CryptographicException) { }
            Check(credentials.Get(id) == value, "old credential changed");
        }
    }
    finally { credentials.Delete(id); }
    Console.WriteLine("PASS native keychain boundary and old value preservation");
}
if (OperatingSystem.IsWindows()) return;
using (var host = new SystemGameProcessHost())
{
    var log = Path.Combine(root, "redaction.log");
    var handle = await host.StartAsync(Shell("printf 'synthetic-token-'; sleep 0.05; printf '中文-123456\\n' >&2; printf 'abc multi\\nline final'; exit 17"), new(log, true));
    var code = await host.WaitForExitAsync(handle);
    Check(code == 17, "exit code: " + code);
    var text = File.ReadAllText(log);
    Check(!text.Contains(secret) && !text.Contains("abc") && !text.Contains("multi\nline") && text.Contains("<redacted>") && text.EndsWith("final"), "streaming redaction / final drain");
    await host.ReleaseAsync(handle);
    Console.WriteLine("PASS detached dual-stream, chunk/newline boundaries, short secrets and exit code");
    handle = await host.StartAsync(Shell("dd if=/dev/zero bs=1048576 count=20 2>/dev/null | tr '\\000' x; printf tail"), new(log, true));
    Check(await host.WaitForExitAsync(handle) == 0, "large output exit");
    Check(new FileInfo(log).Length <= 16 * 1024 * 1024 && File.ReadAllText(log).Contains("<game log truncated at 16 MiB>"), "hard log cap");
    await host.ReleaseAsync(handle);
    Console.WriteLine("PASS bounded no-newline output and continued drain");
    handle = await host.StartAsync(Shell("printf new-run"), new(log, true));
    Check(await host.WaitForExitAsync(handle) == 0 && File.ReadAllText(log) == "new-run", "existing log reset");
    await host.ReleaseAsync(handle);
    var link = Path.Combine(root, "symlink.log");
    File.CreateSymbolicLink(link, log);
    handle = await host.StartAsync(Shell("printf overwrite"), new(link, true));
    Check(await host.WaitForExitAsync(handle) != 0 && File.ReadAllText(log) == "new-run", "symlink log followed");
    await host.ReleaseAsync(handle);
    Console.WriteLine("PASS existing log cap and symlink rejection");
    var ready = Path.Combine(root, "ready");
    handle = await host.StartAsync(Shell("trap 'exit 9' TERM; touch ready; while :; do sleep 0.05; done"), new(Path.Combine(root, "stop.log"), true));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (!File.Exists(ready)) await Task.Delay(25, timeout.Token);
    await host.TerminateAsync(handle, force: false, timeout.Token);
    Check(await host.WaitForExitAsync(handle) == 9, "soft stop / owned PID");
    await host.ReleaseAsync(handle);
    Console.WriteLine("PASS detached soft termination preserves owned game PID");
}
var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") info.ArgumentList.Add(typeof(Program).Assembly.Location);
info.ArgumentList.Add(root); info.ArgumentList.Add("--parent");
using (var parent = Process.Start(info)!) { await parent.WaitForExitAsync(); Check(parent.ExitCode == 0, "parent exit"); }
var survival = Path.Combine(root, "survival.log");
using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
    while (!File.Exists(survival) || !File.ReadAllText(survival).Contains("survived")) await Task.Delay(50, timeout.Token);
Check(!File.ReadAllText(survival).Contains(secret) && File.ReadAllText(survival).Contains("<redacted>"), "protection after parent exit");
Console.WriteLine("PASS actual Host process exit with continuing redaction");
