using System.Xml;
using System.Xml.Linq;

namespace Pcl.Core;
/// <summary>读取主页布局数据；禁止 DTD、代码和任意类型实例化，Windows 控件由界面层映射。</summary>
public static class HomeMarkup
{
    // 支持完整 XML 及原版的 local: 控件片段；限制数量和嵌套深度。
    public static XElement Parse(string text)
    {
        XElement Read(string source)
        {
            using var reader = XmlReader.Create(
                new StringReader(source),
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 1_048_576
                }
            );
            return XElement.Load(reader);
        }

        XElement root;
        try
        {
            root = Read(text);
        }
        catch (XmlException)
        {
            var fragment = System.Text.RegularExpressions.Regex.Replace(text, @"^\s*<\?xml[^?]*\?>", "");
            root = Read(
                "<StackPanel xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:local=\"clr-namespace:PCL\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" + fragment + "</StackPanel>"
            );
        }

        if (root.DescendantsAndSelf()
            .Count() > 1000 || root.DescendantsAndSelf()
            .Any(
                x => x.Ancestors()
                    .Count() > 32
            ))
        {
            throw new InvalidDataException("主页最多 1000 个元素、32 层嵌套。");
        }

        return root;
    }

    // 原版常见事件映射到已有主页命令；不执行网页提供的程序或脚本。
    public static HomeLink? Action(XElement element)
    {
        string Read(string key) => element.Attributes()
            .FirstOrDefault(
                a => a.Name.LocalName.EndsWith(key, StringComparison.Ordinal)
            )?.Value ?? "";
        var type = Read("EventType");
        if (type.Length == 0)
        {
            return null;
        }

        var kind = type switch
        {
            "打开网页" => "url",
            "复制文本" => "copy",
            "弹出窗口" => "message",
            "启动游戏" => "launch",
            "刷新主页" => "refresh",
            "打开页面" => "navigate",
            _ => throw new InvalidDataException("此主页事件尚不支持：" + type)
        };
        var link = new HomeLink(
            Read("Text")is { Length: > 0 } label
                ? label
                : Read("Content"),
            Read("EventData"),
            kind
        );
        new HomeDocument(
            "",
            "",
            [
                link
            ]
        ).Validate();
        return link;
    }

    // 将布局和命令共同保存，使本地预设可以恢复完整主页。
    public static HomeDocument Document(string text)
    {
        var element = Parse(text);
        var actions = element.DescendantsAndSelf()
            .Select(Action)
            .OfType<HomeLink>()
            .ToArray();
        var document = new HomeDocument("自定义主页", "", actions)
        {
            Markup = text
        };
        document.Validate();
        return document;
    }
}
