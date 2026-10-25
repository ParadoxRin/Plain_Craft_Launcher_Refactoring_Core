using System.IO.Compression;
using System.Text.RegularExpressions;
using Pcl.Domain;

namespace Pcl.Core;
/// <summary>按目标Java选择模块描述符，然后回退manifest和文件名；不把库文件名误作显式模块身份。</summary>
public static class JarModuleIdentity
{
    public static string Read(ZipArchive zip, string fileName, int javaMajor)
    {
        var manifests = zip.Entries.Where(
            x => x.FullName == "META-INF/MANIFEST.MF"
        )
            .ToArray();
        if (manifests.Length > 1 || manifests.FirstOrDefault()?.Length > 2 * 1024 * 1024)
        {
            throw new InvalidDataException("JAR manifest重复或过大。");
        }

        var manifest = "";
        if (manifests.Length == 1)
        {
            using var reader = new StreamReader(
                manifests[0].Open()
            );
            // Manifest续行合并，仅主段定义模块属性；命名条目的属性不冒充主属性。
            manifest = reader.ReadToEnd()
                .Replace("\r\n", "\n")
                .Replace("\n ", "")
                .Split("\n\n", 2)[0];
        }

        string Field(string key) => Regex.Match(
            manifest,
            "(?im)^" + Regex.Escape(key) + @":[ \t]*([^\n]+)"
        )
            .Groups[1].Value.Trim();
        var descriptors = zip.Entries.Where(
            x => x.FullName == "module-info.class"
        )
            .Select(
                x => (Entry: x, Version: 0)
            )
            .ToList();
        if (Field("Multi-Release")
            .Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var entry in zip.Entries)
            {
                var match = Regex.Match(entry.FullName, @"^META-INF/versions/(\d+)/module-info\.class$");
                if (match.Success && int.TryParse(match.Groups[1].Value, out var version) && version >= 9 && version <= javaMajor)
                {
                    descriptors.Add((entry, version));
                }
            }
        }

        if (javaMajor >= 9 && descriptors.Count > 0)
        {
            var chosen = descriptors.Where(
                x => x.Version == descriptors.Max(
                    d => d.Version
                )
            )
                .ToArray();
            if (chosen.Length != 1 || chosen[0].Entry.Length > 2 * 1024 * 1024)
            {
                throw new InvalidDataException("模块描述符重复或过大。");
            }

            using var input = chosen[0].Entry.Open();
            using var memory = new MemoryStream();
            input.CopyTo(memory);
            return JavaModuleDescriptor.ReadName(
                memory.ToArray()
            );
        }

        var automatic = Field("Automatic-Module-Name");
        if (automatic.Length > 0)
        {
            return automatic;
        }

        var name = Regex.Replace(
            Path.GetFileNameWithoutExtension(fileName),
            @"-\d+(?:\.|$).*$",
            ""
        );
        return Regex.Replace(
            Regex.Replace(name, @"[^A-Za-z0-9]", "."),
            @"\.+",
            "."
        )
            .Trim('.');
    }
}
