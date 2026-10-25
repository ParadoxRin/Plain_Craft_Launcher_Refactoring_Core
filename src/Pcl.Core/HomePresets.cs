using Newtonsoft.Json;

namespace Pcl.Core;
public sealed record HomePresetEntry(string Id, string Name, HomeDocument Document);
/// <summary>将主页内容保存为自包含预设；原文件更新或移动后，预设仍可离线使用。</summary>
public sealed class HomePresets(AppPaths paths)
{
    private string FileName => Path.Combine(paths.Root, "home-presets.json");

    // 从当前数据目录恢复用户预设，不依赖原主页文件仍存在。
    public IReadOnlyList<HomePresetEntry> List() => File.Exists(FileName)
        ? JsonConvert.DeserializeObject<HomePresetEntry[]>(
            File.ReadAllText(FileName)
        ) ?? []
        : [];
    // 为内容副本创建独立标识，然后通过 JSON 原子写入保存列表。
    public HomePresetEntry Save(string name, HomeDocument document)
    {
        document.Validate();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
        {
            throw new InvalidDataException("预设名称需要 1–100 个字符。");
        }

        var entries = List();
        if (entries.Count >= 100)
        {
            throw new InvalidDataException("最多保存 100 个主页预设。");
        }

        var entry = new HomePresetEntry(
            "local-" + Guid.NewGuid()
                .ToString("N"),
            name.Trim(),
            document
        );
        JsonFiles.Write(
            FileName,
            entries.Append(entry)
                .ToArray()
        );
        return entry;
    }

    // 只移除选中预设，不删除用户的原始主页文件。
    public void Remove(string id)
    {
        JsonFiles.Write(
            FileName,
            List()
                .Where(
                    x => x.Id != id
                )
                .ToArray()
        );
    }

    // 读取指定预设并应用正常主页的内容约束。
    public HomeDocument Read(string id)
    {
        var page = List()
            .SingleOrDefault(
                x => x.Id == id
            )?.Document ?? throw new FileNotFoundException("此主页预设已移除。");
        page.Validate();
        return page;
    }
}
