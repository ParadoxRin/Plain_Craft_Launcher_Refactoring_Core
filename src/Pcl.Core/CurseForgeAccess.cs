namespace Pcl.Core;

/// <summary>Only stable, non-sensitive diagnostics may leave the request boundary.</summary>
public sealed class CurseForgeRequestException(string code) : IOException(code)
{
    public string Code { get; } = code;
    public bool Retryable => Code is "CURSEFORGE_RATE_LIMITED" or "CURSEFORGE_UNAVAILABLE" or "CURSEFORGE_TIMEOUT" or "CURSEFORGE_NETWORK_ERROR";
    public string SafeMessage => Code switch
    {
        "CURSEFORGE_KEY_REQUIRED" => "未配置 CurseForge 密钥或启用的镜像；其他来源仍可使用",
        "CURSEFORGE_KEY_INVALID" => "CurseForge 密钥格式无效，请重新配置",
        "CURSEFORGE_KEY_REJECTED" => "CurseForge 拒绝访问，请检查密钥及授权",
        "CURSEFORGE_CONFIG_INVALID" => "Host 无法读取 CurseForge 密钥配置，请检查部署文件",
        "SECRET_REENTRY_REQUIRED" => "临时密钥已过期，请重新输入",
        "CURSEFORGE_RATE_LIMITED" => "CurseForge 请求过于频繁，请稍后重试",
        "CURSEFORGE_TIMEOUT" => "CurseForge 请求超时，请重试",
        "CURSEFORGE_NETWORK_ERROR" => "CurseForge 网络请求失败，请重试",
        "CURSEFORGE_RESPONSE_INVALID" => "CurseForge 返回的数据无效",
        _ => "CurseForge 来源暂不可用，请稍后重试"
    };
}

public static class CurseForgeAccess
{
    public const string KeyFileVariable = "PCL_CURSEFORGE_API_KEY_FILE";
    // Explicit session input wins; a bad override never silently falls back to another identity.
    internal static string Resolve(string? supplied)
    {
        if (!string.IsNullOrEmpty(supplied)) return Validate(supplied);
        var file = Environment.GetEnvironmentVariable(KeyFileVariable);
        if (string.IsNullOrEmpty(file)) return "";
        try
        {
            if (!Path.IsPathFullyQualified(file)) throw new IOException();
            using var stream = File.OpenRead(file);
            if (stream.Length > 8192) throw new IOException();
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true);
            var buffer = new char[8193];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            if (count > 8192) throw new IOException();
            return Validate(new string(buffer, 0, count).TrimEnd('\r', '\n'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Never retain file paths, file contents or inner exceptions.
            throw new CurseForgeRequestException("CURSEFORGE_CONFIG_INVALID");
        }
    }

    private static string Validate(string key)
    {
        if (key.Length is 0 or > 4096 || key.Any(c => c < 33 || c > 126))
            throw new CurseForgeRequestException("CURSEFORGE_KEY_INVALID");
        return key;
    }

    internal static T ParseResponse<T>(Func<T> parse)
    {
        try { return parse(); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or FormatException or OverflowException or InvalidCastException or InvalidOperationException or Newtonsoft.Json.JsonException)
        {
            throw new CurseForgeRequestException("CURSEFORGE_RESPONSE_INVALID");
        }
    }

    internal static bool HasConfiguration(string? supplied, NetworkOptions options) =>
        !string.IsNullOrEmpty(supplied) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(KeyFileVariable)) ||
        (options.CurseForgeApiMirror.Length > 0 && options.ResourceSource != "official");
}
