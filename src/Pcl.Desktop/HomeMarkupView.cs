using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
/// <summary>把声明式主页节点转为真实 Avalonia 控件；属性和事件均显式映射。</summary>
internal static class HomeMarkupView
{
    internal static Control Build(string markup, Func<HomeLink, Task> action) => Build(
        HomeMarkup.Parse(markup),
        action
    );
    private static Control Build(XElement element, Func<HomeLink, Task> action)
    {
        string Attr(string name, string fallback = "") => element.Attributes()
            .FirstOrDefault(
                x => x.Name.LocalName == name
            )?.Value ?? fallback;
        double Number(string name, double fallback) => double.TryParse(
            Attr(name),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value
        ) && double.IsFinite(value)
            ? Math.Clamp(value, 0, 4096)
            : fallback;
        var children = element.Elements()
            .Where(
                x => !x.Name.LocalName.Contains('.')
            )
            .ToArray();
        Control result;
        switch (element.Name.LocalName)
        {
            case "StackPanel":
            case "WrapPanel":
            case "Grid":
                Panel panel = element.Name.LocalName switch
                {
                    "WrapPanel" => new WrapPanel(),
                    "Grid" => new Grid(),
                    _ => new StackPanel
                    {
                        Spacing = Number("Spacing", 8),
                        Orientation = Attr("Orientation") == "Horizontal"
                            ? Orientation.Horizontal
                            : Orientation.Vertical
                    }
                };
                if (panel is Grid grid)
                {
                    foreach (var row in element.Elements()
                        .FirstOrDefault(
                            x => x.Name.LocalName == "Grid.RowDefinitions"
                        )?.Elements() ?? [])
                    {
                        grid.RowDefinitions.Add(
                            new RowDefinition(
                                GridLength.Parse(
                                    (string? )row.Attribute("Height") ?? "*"
                                )
                            )
                        );
                    }

                    foreach (var column in element.Elements()
                        .FirstOrDefault(
                            x => x.Name.LocalName == "Grid.ColumnDefinitions"
                        )?.Elements() ?? [])
                    {
                        grid.ColumnDefinitions.Add(
                            new ColumnDefinition(
                                GridLength.Parse(
                                    (string? )column.Attribute("Width") ?? "*"
                                )
                            )
                        );
                    }
                }

                foreach (var child in children)
                {
                    panel.Children.Add(
                        Build(child, action)
                    );
                }

                result = panel;
                break;
            case "Border":
            case "MyCard":
            case "MyHint":
                var content = new StackPanel
                {
                    Spacing = 10
                };
                var title = Attr(
                    "Title",
                    Attr("Text")
                );
                if (title.Length > 0)
                {
                    content.Children.Add(
                        new TextBlock
                        {
                            Text = title,
                            FontWeight = FontWeight.SemiBold,
                            TextWrapping = TextWrapping.Wrap
                        }
                    );
                }

                foreach (var child in children)
                {
                    content.Children.Add(
                        Build(child, action)
                    );
                }

                result = new Border
                {
                    Background = HomeBrush(
                        Attr("Background", "#FFFFFF")
                    ),
                    BorderBrush = HomeBrush(
                        Attr("BorderBrush", "#DCEAF9")
                    ),
                    BorderThickness = Thickness.Parse(
                        Attr("BorderThickness", "0")
                    ),
                    Padding = Thickness.Parse(
                        Attr("Padding", "16")
                    ),
                    CornerRadius = new CornerRadius(
                        Number("CornerRadius", 6)
                    ),
                    Child = content
                };
                break;
            case "TextBlock":
                result = new TextBlock
                {
                    Text = Attr("Text", element.Value),
                    FontSize = Number("FontSize", 14),
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = Attr("FontWeight") == "Bold"
                        ? FontWeight.Bold
                        : FontWeight.Normal,
                    Foreground = HomeBrush(
                        Attr("Foreground", "#303B45")
                    )
                };
                break;
            case "Button":
            case "MyButton":
            case "MyIconButton":
            case "MyListItem":
                var button = new Button
                {
                    Content = Attr(
                        "Text",
                        Attr(
                            "Title",
                            Attr("Content", "刷新")
                        )
                    )
                };
                if (Attr("Logo")
                    .Length > 0)
                {
                    button.Content = new Avalonia.Controls.Shapes.Path
                    {
                        Data = Geometry.Parse(
                            Attr("Logo")
                        ),
                        Fill = Brush.Parse("#0875D1"),
                        Stretch = Stretch.Uniform,
                        Width = 20,
                        Height = 20
                    };
                }

                var link = HomeMarkup.Action(element);
                if (link is not null)
                {
                    button.Click += async (_, _) =>
                    {
                        button.IsEnabled = false;
                        try
                        {
                            await action(link);
                        }
                        finally
                        {
                            button.IsEnabled = true;
                        }
                    };
                }

                result = button;
                break;
            case "Path":
                result = new Avalonia.Controls.Shapes.Path
                {
                    Data = Geometry.Parse(
                        Attr("Data")
                    ),
                    Fill = HomeBrush(
                        Attr("Fill", "#0875D1")
                    ),
                    Stretch = Stretch.Uniform
                };
                break;
            case "ScrollViewer":
                result = new ScrollViewer
                {
                    Content = children.Length == 1
                        ? Build(children[0], action)
                        : throw new InvalidDataException("ScrollViewer 需要一个子布局。")
                };
                break;
            default:
                throw new InvalidDataException("主页控件尚不支持：" + element.Name.LocalName);
        }

        result.Margin = Thickness.Parse(
            Attr("Margin", "0")
        );
        if (Attr("Width")
            .Length > 0)
        {
            result.Width = Number("Width", 100);
        }

        if (Attr("Height")
            .Length > 0)
        {
            result.Height = Number("Height", 40);
        }

        if (Enum.TryParse<HorizontalAlignment>(
            Attr("HorizontalAlignment"),
            out var horizontal
        ))
        {
            result.HorizontalAlignment = horizontal;
        }

        if (Enum.TryParse<VerticalAlignment>(
            Attr("VerticalAlignment"),
            out var vertical
        ))
        {
            result.VerticalAlignment = vertical;
        }

        result.IsVisible = Attr("Visibility")is not ("Collapsed" or "Hidden");
        Grid.SetColumn(
            result,
            (int)Number("Grid.Column", 0)
        );
        Grid.SetRow(
            result,
            (int)Number("Grid.Row", 0)
        );
        Grid.SetColumnSpan(
            result,
            Math.Max(
                1,
                (int)Number("Grid.ColumnSpan", 1)
            )
        );
        Grid.SetRowSpan(
            result,
            Math.Max(
                1,
                (int)Number("Grid.RowSpan", 1)
            )
        );
        return result;
    } // 兼容原版主页常用动态颜色引用，只映射颜色资源，不解释任意标记扩展。

    private static IBrush HomeBrush(string value)
    {
        if (value.StartsWith("{DynamicResource ColorBrush", StringComparison.Ordinal))
        {
            return Brush.Parse("#0875D1");
        }

        return Brush.Parse(value);
    }
}
