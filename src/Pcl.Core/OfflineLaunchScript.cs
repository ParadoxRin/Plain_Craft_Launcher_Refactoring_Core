using System.Text;

namespace Pcl.Core;
/// <summary>把已校验的离线启动计划序列化为 POSIX 脚本；参数逐个引用，不进行 shell 字符串拼接求值。</summary>
public static class OfflineLaunchScript
{
    public static string Render(LaunchPlan plan, GameSession session)
    {
        if (!GameLaunchAuthorization.IsOfflineIdentity(session))
        {
            throw new InvalidOperationException("脚本仅支持离线身份。");
        }

        // 任何额外认证覆盖都拒绝；公开脚本不能携带微软或外置账户的会话参数。
        for (var i = 0; i < plan.Arguments.Count; i++)
        {
            var argument = plan.Arguments[i];
            if (argument == "--accessToken" && (i + 1 == plan.Arguments.Count || plan.Arguments[i + 1] != session.AccessToken))
            {
                throw new InvalidDataException("启动参数包含非离线令牌，无法导出。");
            }

            if (argument.StartsWith("--accessToken=", StringComparison.OrdinalIgnoreCase) || argument.Equals("--session", StringComparison.OrdinalIgnoreCase) || argument.StartsWith("--session=", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("请移除自定义认证参数后再导出。");
            }
        }

        string Quote(string value)
        {
            if (value.Contains('\0'))
            {
                throw new InvalidDataException("启动参数不能含空字符。");
            }

            return "'" + value.Replace("'", "'\"'\"'") + "'";
        }

        var script = new StringBuilder(
            "#!/bin/sh\n# PCL Mac 离线启动：仅适用于当前 Mac 与当前文件位置。\n# 更改 Java、实例设置或路径后请重新导出；不执行启动器的前置命令。\nset -eu\n"
        );
        script.Append("[ -x ")
            .Append(
                Quote(plan.Java)
            )
            .Append(" ] || { printf '%s\\n' 'Java 已移动或不可执行，请重新导出脚本。' >&2; exit 1; }\n");
        script.Append("cd ")
            .Append(
                Quote(plan.WorkingDirectory)
            )
            .Append(" || exit 1\n");
        script.Append("exec ")
            .Append(
                Quote(plan.Java)
            );
        foreach (var argument in plan.Arguments)
        {
            script.Append(" \\\n    ")
                .Append(
                    Quote(argument)
                );
        }

        return script.Append('\n')
            .ToString();
    }
}
