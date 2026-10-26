using System.Text;

namespace Pcl.Domain;
// 解析历史参数中的引号与转义，不交给 shell 执行。
public static class LegacyArguments
{
    public static IReadOnlyList<string> SplitArguments(string text)
    {
        var result = new List<string>();
        var word = new StringBuilder();
        char quote = '\0';
        bool started = false;
        for (int i = 0; i < text.Length; i++)
        {
            // [实现] 每次迭代（i < text.Length）：准备 var c = text[i]；按 c == '\\' && i + 1 < text.Length && (text[i + 1] == quote || text[i + 1] == '\\' || quote == '\… 分支处理；按 quote != '\0' 分支处理。
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && (text[i + 1] == quote || text[i + 1] == '\\' || quote == '\0' && char.IsWhiteSpace(text[i + 1])))
            {
                // [实现] 条件成立（c == '\\' && i + 1 < text.Length && (text[i + 1] == quote || text[i + 1] == '\\' || quote == '\…）时：执行 word.Append(text[++i])；执行 started = true；执行 continue;。
                word.Append(text[++i]);
                started = true;
                continue;
            }

            if (quote != '\0')
            {
                // [实现] 条件成立（quote != '\0'）时：按 c == quote 分支处理；执行 started = true；执行 continue;。
                if (c == quote)
                    quote = '\0';
                else
                    word.Append(c);
                started = true;
                continue;
            }

            if (c is '\'' or '"')
            {
                // [实现] 条件成立（c is '\'' or '"'）时：执行 quote = c；执行 started = true；执行 continue;。
                quote = c;
                started = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                // [实现] 条件成立（char.IsWhiteSpace(c)）时：按 started 分支处理。
                if (started)
                {
                    // [实现] 条件成立（started）时：执行 result.Add( word.ToString() )；执行 word.Clear()；执行 started = false。
                    result.Add(
                        word.ToString()
                    );
                    word.Clear();
                    started = false;
                }
            }
            else
            {
                // [实现] 前一条件不成立时：执行 word.Append(c)；执行 started = true。
                word.Append(c);
                started = true;
            }
        }

        if (quote != '\0')
            throw new InvalidDataException("旧版启动参数的引号未闭合。");
        if (started)
            result.Add(
                word.ToString()
            );
        return result;
    }
}
