using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Pcl.Protocol;
// Unix 使用短绝对路径，避免 macOS 的长 TMPDIR 超过 socket 路径上限。
internal static class PrivatePipePath
{
    [DllImport("libc")]
    private static extern uint geteuid();
    internal static string ForRoot(string root)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    Path.GetFullPath(root)
                )
            )
        )[..24].ToLowerInvariant();
        if (OperatingSystem.IsWindows())
        {
            return "pcl-mac-" + Environment.UserName + "-" + hash;
        }

        return Path.Combine(
            "/tmp",
            "pcl-host-" + geteuid(),
            hash
        );
    }

    internal static void SecureParent(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0 || (File.GetUnixFileMode(parent) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
        {
            throw new IOException("PRIVATE_ENDPOINT_PERMISSIONS_INVALID");
        }
    }
}
