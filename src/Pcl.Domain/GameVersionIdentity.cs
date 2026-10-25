using System.Text.RegularExpressions;

namespace Pcl.Core.Domain;
// 保持原版本标识校验规则；宿主平台与目录 API 不参与业务标识判断。
public static class GameVersionIdentity
{
    public static void Validate(string id)
    {
        if (!Regex.IsMatch(id, "^[A-Za-z0-9][A-Za-z0-9._-]{0,119}$") || id.Contains(".."))
        {
            throw new InvalidDataException("游戏版本标识无效。");
        }
    }
}
