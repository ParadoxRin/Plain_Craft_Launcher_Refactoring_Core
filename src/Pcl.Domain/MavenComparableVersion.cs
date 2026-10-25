// C# adaptation of Apache Maven 3.9.9 ComparableVersion (Apache-2.0).
// Copyright Apache Software Foundation contributors. See docs/Maven-LICENSE.txt and Maven-NOTICE.txt.
// Adaptation: numeric item variants collapsed into BigInteger; input bounds added for untrusted metadata.
using System.Numerics;

namespace Pcl.Core;
// 模组依赖分析：读取归档中的声明和版本范围，选择兼容候选，报告缺失前置或冲突。
public static class MavenComparableVersion
{
    // 模组依赖分析：读取归档中的声明和版本范围，选择兼容候选，报告缺失前置或冲突。
    private abstract class Item
    {
        // [实现] Compare 的实现：仅声明调用契约，由实现类提供行为。
        public abstract int Compare(Item? other);
        public abstract bool Empty { get; }
    }

    // 模组依赖分析：读取归档中的声明和版本范围，选择兼容候选，报告缺失前置或冲突。
    private sealed class Number(string text) : Item
    {
        private readonly BigInteger value = BigInteger.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        public override bool Empty => value.IsZero;

        // [实现] Compare 的实现：计算并返回 other switch { null => value.Sign, Number n => value.CompareTo(n.value), _ => 1 }。
        public override int Compare(Item? other) => other switch
        {
            null => value.Sign,
            Number n => value.CompareTo(n.value),
            _ => 1
        };
    }

    // 模组依赖分析：读取归档中的声明和版本范围，选择兼容候选，报告缺失前置或冲突。
    private sealed class Qualifier : Item
    {
        private static readonly string[] Known = [
            "alpha",
            "beta",
            "milestone",
            "rc",
            "snapshot",
            "",
            "sp"
        ];
        private readonly string comparable;
        // [实现] Qualifier 构造 的实现：按 digit 分支处理；执行 value = value switch { "ga" or "final" or "release" => "", "cr" => "rc", _ => value }；准备 var i = Array.IndexOf(Known, value)。
        public Qualifier(string value, bool digit)
        {
            if (digit)
                value = value switch
                {
                    "a" => "alpha",
                    "b" => "beta",
                    "m" => "milestone",
                    _ => value
                };
            value = value switch
            {
                "ga" or "final" or "release" => "",
                "cr" => "rc",
                _ => value
            };
            var i = Array.IndexOf(Known, value);
            comparable = i < 0
                ? "7-" + value
                : i.ToString();
        }

        public override bool Empty => comparable == "5";

        // [实现] Compare 的实现：计算并返回 other switch { null => string.CompareOrdinal(comparable, "5"), Qualifier q => string.CompareOrd…。
        public override int Compare(Item? other) => other switch
        {
            null => string.CompareOrdinal(comparable, "5"),
            Qualifier q => string.CompareOrdinal(comparable, q.comparable),
            _ => -1
        };
    }

    // 模组依赖分析：读取归档中的声明和版本范围，选择兼容候选，报告缺失前置或冲突。
    private sealed class Items : Item
    {
        public readonly List<Item> Values = [];
        public override bool Empty => Values.Count == 0;

        // [实现] Normalize 的实现：在 i >= 0 条件下迭代。
        public void Normalize()
        {
            for (int i = Values.Count - 1; i >= 0; i--)
            {
                // [实现] 每次迭代（i >= 0）：按 Values[i].Empty 分支处理。
                if (Values[i].Empty)
                    Values.RemoveAt(i);
                else if (Values[i] is not Items)
                    break;
            }
        }

        // [实现] Compare 的实现：按 other is null 分支处理；按 other is Number 分支处理；按 other is Qualifier 分支处理。
        public override int Compare(Item? other)
        {
            if (other is null)
            {
                // [实现] 条件成立（other is null）时：遍历 Values 处理 v；返回 0。
                foreach (var v in Values)
                {
                    // [实现] 对 Values 中的每一项：准备 var c = v.Compare(null)；按 c != 0 分支处理。
                    var c = v.Compare(null);
                    if (c != 0)
                        return c;
                }

                return 0;
            }

            if (other is Number)
                return -1;
            if (other is Qualifier)
                return 1;
            var right = ((Items)other).Values;
            for (int i = 0; i < Math.Max(Values.Count, right.Count); i++)
            {
                // [实现] 每次迭代（i < Math.Max(Values.Count, right.Count)）：准备 var a = i < Values.Count ? Values[i] : null；准备 var b = i < right.Count ? right[i] : null；准备 var c = a is null ? -(b?.Compare(null) ?? 0) : a.Compare(b)。
                var a = i < Values.Count
                    ? Values[i]
                    : null;
                var b = i < right.Count
                    ? right[i]
                    : null;
                var c = a is null
                    ? -(b?.Compare(null) ?? 0)
                    : a.Compare(b);
                if (c != 0)
                    return c;
            }

            return 0;
        }
    }

    // [实现] Parse 的实现：按 string.IsNullOrEmpty(text) || text.Length > 512 || text.Any( c => c > 127 || char.IsControl(c) … 分支处理；执行 text = text.ToLowerInvariant()；准备 var root = new Items()。
    private static Items Parse(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 512 || text.Any(
            c => c > 127 || char.IsControl(c) || char.IsWhiteSpace(c)
        ))
            throw new InvalidDataException("版本需为 1–512 个 ASCII 非空白字符。");
        text = text.ToLowerInvariant();
        var root = new Items();
        var list = root;
        var stack = new Stack<Items>();
        stack.Push(root);
        bool digit = false;
        int start = 0;
        // [实现] Part 的实现：计算并返回 number ? new Number(value) : new Qualifier(value, false)。
        Item Part(bool number, string value) => number
            ? new Number(value)
            : new Qualifier(value, false);
        // [实现] Nest 的实现：准备 var child = new Items()；执行 list.Values.Add(child)；执行 list = child。
        void Nest()
        {
            var child = new Items();
            list.Values.Add(child);
            list = child;
            stack.Push(child);
        }

        for (int i = 0; i < text.Length; i++)
        {
            // [实现] 每次迭代（i < text.Length）：准备 var c = text[i]；按 c is '.' or '-' 分支处理。
            var c = text[i];
            if (c is '.' or '-')
            {
                // [实现] 条件成立（c is '.' or '-'）时：执行 list.Values.Add( i == start ? new Number("0") : Part(digit, text[start..i]) )；执行 start = i + 1；按 c == '-' 分支处理。
                list.Values.Add(
                    i == start
                        ? new Number("0")
                        : Part(digit, text[start..i])
                );
                start = i + 1;
                if (c == '-')
                    Nest();
            }
            else if (char.IsAsciiDigit(c))
            {
                // [实现] 条件成立（char.IsAsciiDigit(c)）时：按 !digit && i > start 分支处理；执行 digit = true。
                if (!digit && i > start)
                {
                    // [实现] 条件成立（!digit && i > start）时：按 list.Values.Count > 0 分支处理；执行 list.Values.Add( new Qualifier(text[start..i], true) )；执行 start = i。
                    if (list.Values.Count > 0)
                        Nest();
                    list.Values.Add(
                        new Qualifier(text[start..i], true)
                    );
                    start = i;
                    Nest();
                }

                digit = true;
            }
            else
            {
                // [实现] 前一条件不成立时：按 digit && i > start 分支处理；执行 digit = false。
                if (digit && i > start)
                {
                    // [实现] 条件成立（digit && i > start）时：执行 list.Values.Add( Part(true, text[start..i]) )；执行 start = i；执行 Nest()。
                    list.Values.Add(
                        Part(true, text[start..i])
                    );
                    start = i;
                    Nest();
                }

                digit = false;
            }
        }

        if (text.Length > start)
        {
            // [实现] 条件成立（text.Length > start）时：按 !digit && list.Values.Count > 0 分支处理；执行 list.Values.Add( Part(digit, text[start..]) )。
            if (!digit && list.Values.Count > 0)
                Nest();
            list.Values.Add(
                Part(digit, text[start..])
            );
        }

        while (stack.TryPop(out var item))
            item.Normalize();
        return root;
    }

    // [实现] Compare 的实现：计算并返回 Math.Sign( Parse(left) .Compare( Parse(right) ) )。
    public static int Compare(string left, string right) => Math.Sign(
        Parse(left)
            .Compare(
                Parse(right)
            )
    );
}
