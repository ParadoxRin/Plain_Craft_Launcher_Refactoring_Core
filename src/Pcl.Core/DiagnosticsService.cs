using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Pcl.Core;
// [实现] 数据约定：DiagnosticFinding 汇集 Cause、Advice、Evidence，供该模块传递状态。
public sealed record DiagnosticFinding(string Cause, string Advice, string Evidence)
{
    public string RuleId { get; init; } = "";
    public string Confidence { get; init; } = "匹配到错误特征";
}

// 任务诊断：读取任务和日志上下文，识别已知错误，脱敏后展示或导出诊断结果。
public static class DiagnosticsService
{
    private const string WorldEntryFailurePattern = @"Error loading server minimap properties|MinecraftServerData\.getLevelProperties";
    private const string ForgeWorldEntryFailurePattern = @"Can not retrieve LootModifierManager until resources have loaded once";
    /// <summary>识别已实测的世界进入失败；进程退出码 0 不代表没有在世界内被模组断开。</summary>
    public static bool HasWorldEntryFailure(string text) => (Has(text, WorldEntryFailurePattern) && Has(text, "xaero")) || (Has(text, ForgeWorldEntryFailurePattern) && Has(text, "net.minecraftforge"));
    /// <summary>读取本次日志末尾的有限数据，区分正常退出与已记录的世界进入失败。</summary>
    public static async Task<string> GameExitStatusAsync(int exitCode, bool stoppedByUser, string logFile)
    {
        var message = stoppedByUser
            ? "游戏已由用户结束"
            : exitCode == 0
                ? "游戏已正常退出"
                : $"游戏异常退出（{exitCode}），可在任务与诊断中检查日志";
        try
        {
            // [实现] 在异常保护范围内：准备 var file = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)；执行 file.Seek( Math.Max(0, file.Length - 4_000_000), SeekOrigin.Begin )；准备 var reader = new StreamReader(file)。
            await using var file = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            file.Seek(
                Math.Max(0, file.Length - 4_000_000),
                SeekOrigin.Begin
            );
            using var reader = new StreamReader(file);
            if (HasWorldEntryFailure(
                await reader.ReadToEndAsync()
            ))
            {
                // [实现] 条件成立（HasWorldEntryFailure( await reader.ReadToEndAsync() )）时：返回 "游戏已退出，但日志记录了进入世界失败；请在任务与诊断中检查世界和模组日志"。
                return "游戏已退出，但日志记录了进入世界失败；请在任务与诊断中检查世界和模组日志";
            }
        }
        catch (Exception ex)when (ex is IOException or UnauthorizedAccessException)
        {
            // [实现] 捕获 (Exception ex) 后：返回 message + "（本次日志无法读取）"。
            return message + "（本次日志无法读取）";
        }

        return message;
    }

    private static readonly (string Pattern, string Cause, string Advice)[] rules = [
        (@"OutOfMemoryError|Could not reserve enough space|Cannot allocate memory", "内存不足或配置过大", "检查实例内存上限，关闭占用内存的应用；降低光影设置。"),
        (@"UnsupportedClassVersionError|Unsupported class file major version|Unsupported major.minor version|compiled by a more recent version of the Java Runtime|compatibility level JAVA_\d+ could not be set", "Java 版本不匹配", "在实例设置恢复自动选择 Java，检查模组要求的 Java 主版本。"),
        (@"UnsatisfiedLinkError|mach-o.*incompatible|incompatible architecture|wrong architecture", "原生库架构不匹配", "检查 Java 和模组中的原生库是否都支持当前 CPU；修复实例依赖。"),
        (@"GLFW error|GLX|Failed to create.*window|OpenGL[^\r\n]*unsupported|OpenGL[^\r\n]*error(?![^\r\n]*1282)|Pixel format not accelerated", "图形环境或光影不兼容", "暂时关闭光影及渲染模组，检查 Mac 图形兼容记录。"),
        (@"DuplicateModsFoundException|Duplicate mod|duplicate mods", "重复模组", "打开本地资源管理，保留同一模组的一个兼容版本。"),
        (@"Incompatible mod set|ModResolutionException|requires[^\r\n]{0,400}(?:missing|not installed|incompatible|but only|wrong version)|Missing[^\r\n]{0,120}dependencies|MissingModsException", "模组依赖或版本冲突", "根据下方原始错误补齐前置，确认游戏版本和加载器一致。"),
        (@"Mixin.*(failed|error)|MixinApplyError|InvalidMixinException", "模组注入失败", "检查错误附近的模组名，回退最近更新的模组并逐个定位。"),
        (@"ZipException|invalid LOC header|zip END header not found|Invalid or corrupt jarfile", "JAR 或压缩文件损坏", "重新校验并下载对应文件，保留原文件用于排查。"),
        (@"SSLHandshakeException|PKIX path|UnknownHostException|ConnectException|SocketTimeoutException", "网络或证书错误", "检查网络、系统时间和代理；保持证书校验开启。"),
        (@"AccessDeniedException|Permission denied|Read-only file system", "文件访问失败", "检查实例目录权限，确保位于可写目录。"),
        (@"No space left on device|磁盘空间不足", "磁盘空间不足", "释放空间后重试；不要删除其他实例正在使用的共享库。"),
        (@"Failed to verify username|Invalid session|AuthenticationException", "账户会话失效", "重新登录所选账号；正版服务器需要有效正版会话。"),
        (@"EXCEPTION_ACCESS_VIOLATION|SIGSEGV|A fatal error has been detected", "Java 或原生组件崩溃", "保留 hs_err 日志，核对 Java、原生库与图形模组版本。")
    ];
    // [实现] 数据约定：Rule 汇集 Id、Pattern、Cause、Advice、Context、Suppress、WindowsOnly，供该模块传递状态。
    private sealed record Rule(
        string Id,
        string Pattern,
        string Cause,
        string Advice,
        string? Context = null,
        string? Suppress = null,
        bool WindowsOnly = false
    );
    // Source-by-source mapping (all 46 public CrashReason entries) is in docs/crash-rule-source-map.json.
    private static readonly Rule[] pclRules = [
        new(
            "PCL-JVM-OPTIONS",
            @"Unrecognized (?:VM )?option:|Improperly specified VM option",
            "Java 虚拟机参数有误",
            "检查实例附加 JVM 参数；先保存当前配置，再恢复自动 Java 与默认参数逐项复测。"
        ),
        new(
            "PCL-EXTRACTED-MOD",
            @"directories below appear to be extracted jar files|Extracted mod jars found, loading will NOT continue",
            "模组文件被解压",
            "模组目录应放完整 JAR。备份被解压的目录，在资源管理中禁用对应重复内容后重新下载完整文件。"
        ),
        new(
            "PCL-MIXIN-BOOTSTRAP",
            @"ClassNotFoundException: org\.spongepowered\.asm\.launch\.MixinTweaker",
            "缺少 Mixin 引导组件",
            "核对这个旧版模组要求的加载器和 MixinBootstrap 前置；从项目说明选择对应游戏版本，先在副本中验证。"
        ),
        new(
            "PCL-JAVA-MODULE",
            @"ClassCastException: (?:java\.base/jdk|class jdk\.)|NoSuchFieldException: ucp|module java\.base does not export|Unable to make protected final java\.lang\.Class java\.lang\.ClassLoader\.defineClass|ClassNotFoundException: (?:jdk\.nashorn\.api\.scripting\.NashornScriptEngineFactory|java\.lang\.invoke\.LambdaMetafactory)",
            "Java 与加载器接口不兼容",
            "恢复实例自动 Java，核对游戏及加载器主版本。JDK 本身可运行游戏，错误说明组件使用了当前 Java 不兼容或缺失的接口。"
        ),
        new(
            "PCL-OPENJ9",
            @"Open ?J9 (?:is not supported|is incompatible)|\.J9VMInternals\.",
            "组件不兼容 OpenJ9",
            "改用启动器匹配的 Apple Silicon HotSpot Java，并保留原始日志核对不兼容组件。"
        ),
        new(
            "PCL-MODULE-NAME",
            @"Invalid module name: '' is not a Java identifier",
            "模组自动模块名称无效",
            "从错误附近定位 JAR 文件名，在实例副本中使用项目原始文件名或重新下载对应版本；不批量重命名其他模组。"
        ),
        new(
            "PCL-HEAP-CONFIG",
            @"Invalid maximum heap size|Could not reserve enough space for 1048576KB object heap",
            "Java 内存上限或位数配置异常",
            "核对内存上限与 Java 实际架构。Apple Silicon 应使用 ARM64 Java；仅凭此错误不能断定是 32 位 Java。"
        ),
        new(
            "PCL-MANUAL-CRASH",
            @"Manually triggered debug crash",
            "手动触发的调试崩溃",
            "日志表明触发了游戏调试崩溃快捷键。重新启动并复测；这条记录不说明模组、Java 或显卡有故障。"
        ),
        new(
            "PCL-SIGNATURE",
            @"signer information does not match signer information of other classes in the same package|Invalid signature file digest",
            "JAR 签名或内容校验失败",
            "保留对应文件并校验来源和哈希；核对同包的重复类或修改版 JAR，通过修复或重新下载替换，不关闭签名校验。"
        ),
        new(
            "PCL-CONFIG",
            @"Failed loading config file [^\r\n]{1,400}",
            "模组配置文件无法读取",
            "证据中包含配置路径和 modid。先备份该配置与存档，再检查格式、权限和模组版本；在副本中验证恢复默认配置。"
        ),
        new(
            "PCL-MOD-ENTRY",
            @"Caught exception from [^\r\n]{1,180}|due to errors, provided by '[^']{1,120}'",
            "加载器报告模组入口异常",
            "错误列出涉及的模组，仍需检查根异常及其前置。在实例副本中回退最近改动、逐项禁用测试，保留原文件。",
            Suppress: @"MixinApplyError|Mixin apply failed|Mixin prepare failed|MixinTransformerError"
        ),
        new(
            "PCL-MOD-FAILURE",
            @"^\s*Mod File: [^\r\n]{1,300}\.jar[^\r\n]{0,200}",
            "加载器报告模组加载失败",
            "结合该模组的 Failure message 与依赖要求检查，JAR 出现在报告中并不证明它是唯一根因。先在副本中验证修复。",
            @"-- MOD [^\r\n]+|Failure message:"
        ),
        new(
            "PCL-DUPLICATE-KEY",
            @"Multiple entries with same key: [^\r\n]{1,250}",
            "模组注册项发生重复",
            "保留错误中的注册键，检查重复模组及版本组合；先在副本中禁用重复项，不直接修改或删除世界数据。"
        ),
        new(
            "PCL-MOD-INIT",
            @"Failed to create mod instance\.[^\r\n]{0,300}",
            "模组初始化失败",
            "提取 ModID 后检查根异常和前置版本；若此前出现 Mixin 错误，应先处理注入失败。",
            Suppress: @"MixinApplyError|Mixin apply failed|Mixin prepare failed|MixinTransformerError"
        ),
        new(
            "PCL-FABRIC-SOLUTION",
            @"A potential solution has been determined[^\r\n]*:|确定了一种可能的解决方法，这样做可能会解决你的问题：",
            "Fabric 给出依赖调整建议",
            "下方保留加载器给出的 Install/Replace/Remove 及版本范围。核对目标游戏版本后，在副本中按顺序调整并复测；启动器不会自动删除模组。"
        ),
        new(
            "PCL-MANDATORY-DEPENDENCY",
            @"Missing or unsupported mandatory dependencies:",
            "缺少必需前置或游戏版本不符",
            "下方包含依赖 Mod ID、请求范围和实际版本。下载与该游戏版本、加载器相符的依赖，或更换发起依赖的模组版本。"
        ),
        new(
            "PCL-INCOMPATIBLE-MODS",
            @"Incompatible mods found!|Some of your mods are incompatible with the game or each other!",
            "加载器明确报告模组不兼容",
            "根据后续依赖范围与替换建议调整同一加载器版本的模组；先复制实例，保留可回退的原组合。"
        ),
        new(
            "PCL-MOD-RESOLUTION",
            @"Mod resolution failed|net\.fabricmc\.loader[^\r\n]{0,160}(?:FormattedException|ModResolutionException)",
            "模组加载器解析失败",
            "检查同一报告中的前置缺失、冲突和版本范围。普通 Warnings were found 提示不会单独作为崩溃原因。"
        ),
        new(
            "PCL-FORGE-ERROR",
            @"An exception was thrown, the game will display an error screen and halt\.",
            "Forge 提供加载错误",
            "下方保留 Forge 后续异常，核对依赖和加载器版本；无法确定时导出完整脱敏诊断包。"
        ),
        new(
            "PCL-FORGE-INCOMPLETE",
            @"Cannot find launch target fmlclient, unable to launch|Invalid paths argument, contained no existing paths[^\r\n]{0,300}fmlcore",
            "Forge 安装文件不完整",
            "在实例管理校验修复；若缺失的是安装器生成文件，用调整加载器创建新副本重新安装并保留原实例。"
        ),
        new(
            "PCL-FORGE-DUPLICATE",
            @"Found multiple arguments for option fml\.forgeVersion, but you asked for only one",
            "版本参数中存在多个 Forge",
            "检查实例元数据是否混合多个加载器版本；使用调整加载器新建干净副本，迁移用户数据后验证。"
        ),
        new(
            "PCL-FORGE-JAVA-PATCH",
            @"NoSuchMethodError: [^\r\n]{0,80}sun\.security\.util\.ManifestEntryVerifier",
            "旧 Forge 与当前 Java 更新版不兼容",
            "优先选择同一游戏版本中兼容当前 Java 的 Forge 修订版，使用组件副本升级并复测；不要只修改 Java 主版本而忽略加载器要求。"
        ),
        new(
            "PCL-JAVA11-MINIMUM",
            @"class file version 55\.0[^\r\n]{0,250}only recognizes|compatibility level JAVA_11 could not be set|NoSuchMethodException: no such method: sun\.misc\.Unsafe\.defineAnonymousClass",
            "模组与 Java 11 级别接口不匹配",
            "class file 55 需要至少 Java 11，但游戏和加载器也必须兼容。先核对模组发布说明，选择匹配整个实例的模组/Java 组合。"
        ),
        new(
            "PCL-ID-LIMIT",
            @"maximum id range exceeded",
            "旧版游戏注册 ID 超出上限",
            "备份世界，核对旧版游戏和模组的注册限制；只有项目明确支持时才安装相应兼容修复，先在新世界验证。"
        ),
        new(
            "PCL-NIGHTCONFIG",
            @"com\.electronwill\.nightconfig\.core\.io\.ParsingException: Not enough data available",
            "NightConfig 读取配置失败",
            "先备份并检查对应配置是否被截断，以及模组/加载器的已知问题与修复版本；不自动删除整个 config 目录。",
            Suppress: @"Failed loading config file"
        ),
        new(
            "PCL-SHADERS-OPTIFINE",
            @"Shaders Mod detected\. Please remove it, OptiFine has built-in support for shaders",
            "Shaders Mod 与 OptiFine 重复安装",
            "OptiFine 已带光影功能；在实例副本中禁用额外 Shaders Mod 后验证，并保留原文件供回退。"
        ),
        new(
            "PCL-OPTIFINE-FORGE",
            @"NoSuchMethodError: [^\r\n]{0,220}(?:SpriteContents\.<init>|RenderSystem\.getBackendDescription|BakedQuad\.<init>|ForgeGui\.renderSelectedItemName|DistanceManager|Font\.ellipsize)|The Mod File [^\r\n]{0,180}OptiFine[^\r\n]{0,180}has mods that were not found",
            "可能是 OptiFine 与 Forge 版本不兼容",
            "日志同时包含 OptiFine/Forge 和对应 API 错误。按项目发布的兼容版本表核对，在组件副本中调整；该特征仍需复测确认根因。",
            @"OptiFine"
        ),
        new(
            "PCL-OPTIFINE-WORLD",
            @"NoSuchMethodError: net\.minecraft\.world\.server\.ChunkManager\$ProxyTicketManager\.shouldForceTicks",
            "OptiFine 组合可能导致世界加载失败",
            "保留原世界，在实例副本中换用兼容的 OptiFine/Forge 组合并先测试新世界。",
            @"OptiFine"
        ),
        new(
            "PCL-TEXTURE-SIZE",
            @"Maybe try a lower resolution resourcepack\?|StitcherException|Unable to fit:[^\r\n]{0,200}size",
            "资源包纹理或图集容量超限",
            "先在副本中停用高分辨率资源包，降低光影和纹理负载；保留原包，检查涉及纹理大小及项目兼容说明。"
        ),
        new(
            "PCL-OPENGL-1282",
            @"(?:OpenGL|GL ERROR)[^\r\n]{0,100}1282|1282: Invalid operation",
            "OpenGL 1282 出现在异常上下文",
            "1282 不能单独证明光影是根因。结合异常上下文，分别关闭光影和资源包复测，再核对 Mac 渲染模组组合。",
            @"[A-Za-z.]+Exception\b|\bFATAL\b|crash report|Unreported exception"
        ),
        new(
            "PCL-WIN-INTEL",
            @"#\s*C\s*\[ig[^\]\r\n]{0,160}\.dll",
            "Windows 日志中的 Intel 驱动原生崩溃",
            "这是 Windows 格式驱动模块线索。若排查当前 Mac，请提供同次 Mac 的 hs_err；不在 macOS 安装 Windows 显卡驱动。",
            WindowsOnly: true
        ),
        new(
            "PCL-WIN-AMD",
            @"#\s*C\s*\[atio[^\]\r\n]{0,160}\.dll",
            "Windows 日志中的 AMD 驱动原生崩溃",
            "这是 Windows 格式驱动模块线索。若排查当前 Mac，请提供同次 Mac 的 hs_err；需按产生此日志的 Windows 环境继续诊断。",
            WindowsOnly: true
        ),
        new(
            "PCL-WIN-NVIDIA",
            @"#\s*C\s*\[nvoglv[^\]\r\n]{0,160}\.dll",
            "Windows 日志中的 Nvidia 驱动原生崩溃",
            "这是 Windows 格式驱动模块线索。仅适用于产生此日志的 Windows 环境，不能据此判断 Apple Silicon GPU 故障。",
            WindowsOnly: true
        )
    ];
    // [实现] Redact 的实现：准备 var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)；按 home.Length > 1 分支处理；执行 text = Regex.Replace( text, """(?i)(access[_-]?token|refresh[_-]?token|client[_-]?secret|auth[_…。
    public static string Redact(string text)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 1)
            text = text.Replace(home, "<HOME>", StringComparison.Ordinal);
        text = Regex.Replace(
            text,
            """(?i)(access[_-]?token|refresh[_-]?token|client[_-]?secret|auth[_-]?session|password|authorization)(\s*[=:]\s*|"\s*:\s*")(Bearer\s+)?[^\s,"}]+""",
            "$1=<redacted>",
            RegexOptions.None,
            TimeSpan.FromSeconds(1)
        );
        return Regex.Replace(
            text,
            """(?i)(--(?:accessToken|session|clientToken)(?:\s+|=)|Bearer\s+)[^\s"']+""",
            "$1<redacted>",
            RegexOptions.None,
            TimeSpan.FromSeconds(1)
        );
    }

    // [实现] Analyze 的实现：按 text.Length > 4_000_000 分支处理；执行 text = text.Replace("\r\n", "\n")；准备 var result = new List<DiagnosticFinding>()。
    public static IReadOnlyList<DiagnosticFinding> Analyze(string text)
    {
        if (text.Length > 4_000_000)
            text = text[^4_000_000..];
        text = text.Replace("\r\n", "\n");
        var result = new List<DiagnosticFinding>();
        if (string.IsNullOrWhiteSpace(text))
            return[
                new("没有可用的分析内容", "请选择本次启动的 latest.log、crash-report 或 hs_err 文件；原文件保持不变。", "日志为空")
                {
                    RuleId = "PCL-NO-LOG"
                }

            ];
        if (Find(text, @"Manually triggered debug crash")is { Success: true } manual)
            return[
                new(
                    "手动触发的调试崩溃",
                    "日志表明触发了游戏调试崩溃快捷键。重新启动并复测；此记录不说明模组或显卡损坏。",
                    Evidence(text, manual.Index)
                )
                {
                    RuleId = "PCL-MANUAL-CRASH"
                }

            ];
        if (Has(text, WorldEntryFailurePattern) && Has(text, "xaero"))
        {
            // [实现] 条件成立（Has(text, WorldEntryFailurePattern) && Has(text, "xaero")）时：执行 result.Add( new( "Xaero 小地图世界状态初始化失败", "日志记录了小地图服务器状态未初始化或加载失败。保留存档，在副本中禁用小地图或从主菜单进入作对照，再检查作者兼容…。
            result.Add(
                new(
                    "Xaero 小地图世界状态初始化失败",
                    "日志记录了小地图服务器状态未初始化或加载失败。保留存档，在副本中禁用小地图或从主菜单进入作对照，再检查作者兼容版本；不要删除世界。",
                    Evidence(
                        text,
                        Find(text, WorldEntryFailurePattern)
                            .Index
                    )
                )
                {
                    RuleId = "MAC-XAERO-WORLD-ENTRY",
                    Confidence = "匹配到本机实测错误特征，第三方根因仍需对照"
                }
            );
        }

        if (Has(text, ForgeWorldEntryFailurePattern) && Has(text, "net.minecraftforge"))
        {
            // [实现] 条件成立（Has(text, ForgeWorldEntryFailurePattern) && Has(text, "net.minecraftforge")）时：执行 result.Add( new( "Forge 世界资源尚未初始化", "本机已观察到 Forge 快速进入世界路径触发此错误。先清空快速进入世界设置，从主菜单进入副本对照；保留存档和完整日…。
            result.Add(
                new(
                    "Forge 世界资源尚未初始化",
                    "本机已观察到 Forge 快速进入世界路径触发此错误。先清空快速进入世界设置，从主菜单进入副本对照；保留存档和完整日志，不删除世界或盲目修改 Java。",
                    Evidence(
                        text,
                        Find(text, ForgeWorldEntryFailurePattern)
                            .Index
                    )
                )
                {
                    RuleId = "MAC-FORGE-WORLD-INIT"
                }
            );
        }

        foreach (var rule in pclRules)
        {
            // [实现] 对 pclRules 中的每一项：按 rule.WindowsOnly && !Has(text, @"EXCEPTION_ACCESS_VIOLATION|(?:OS:|os\.name\s*=)[^\n]{0,80}Wind… 分支处理；按 rule.Id == "PCL-OPTIFINE-FORGE" && !Has(text, @"Forge") 分支处理；按 rule.Context is not null && !Has(text, rule.Context) || rule.Suppress is not null && Has(text, … 分支处理。
            if (rule.WindowsOnly && !Has(text, @"EXCEPTION_ACCESS_VIOLATION|(?:OS:|os\.name\s*=)[^\n]{0,80}Windows"))
                continue;
            if (rule.Id == "PCL-OPTIFINE-FORGE" && !Has(text, @"Forge"))
                continue;
            if (rule.Context is not null && !Has(text, rule.Context) || rule.Suppress is not null && Has(text, rule.Suppress))
                continue;
            var match = Find(text, rule.Pattern);
            if (match.Success)
                result.Add(
                    new(
                        rule.Cause,
                        rule.Advice,
                        Evidence(text, match.Index)
                    )
                    {
                        RuleId = rule.Id
                    }
                );
        }

        for (var i = 0; i < rules.Length; i++)
        {
            // [实现] 每次迭代（i < rules.Length）：准备 var rule = rules[i]；准备 var match = Find(text, rule.Pattern)；按 match.Success 分支处理。
            var rule = rules[i];
            var match = Find(text, rule.Pattern);
            if (match.Success)
                result.Add(
                    new(
                        rule.Cause,
                        rule.Advice,
                        Evidence(text, match.Index)
                    )
                    {
                        RuleId = $"CORE-{i + 1:00}"
                    }
                );
        }

        try
        {
            // [实现] 在异常保护范围内：执行 AddStructuredEvidence(text, result)。
            AddStructuredEvidence(text, result);
        }
        catch (RegexMatchTimeoutException)
        {
            // [实现] 此分支没有额外语句；控制流继续遵循外围的返回、异常或迭代规则。
        }

        return result;
    }

    // [实现] Find 的实现：执行受异常处理保护的操作。
    private static Match Find(string text, string pattern)
    {
        try
        {
            // [实现] 在异常保护范围内：返回 Regex.Match( text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeSpan.FromMill…。
            return Regex.Match(
                text,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.Multiline,
                TimeSpan.FromMilliseconds(500)
            );
        }
        catch (RegexMatchTimeoutException)
        {
            // [实现] 捕获 (RegexMatchTimeoutException) 后：返回 Match.Empty。
            return Match.Empty;
        }
    }

    // [实现] Has 的实现：计算并返回 Find(text, pattern) .Success。
    private static bool Has(string text, string pattern) => Find(text, pattern)
        .Success;
    // [实现] Evidence 的实现：准备 var start = text.LastIndexOf( '\n', Math.Max(0, index - 1) )；执行 start = start < 0 ? 0 : start + 1；准备 var end = start。
    private static string Evidence(string text, int index, int after = 7)
    {
        var start = text.LastIndexOf(
            '\n',
            Math.Max(0, index - 1)
        );
        start = start < 0
            ? 0
            : start + 1;
        var end = start;
        for (var i = 0; i <= after && end < text.Length; i++)
        {
            // [实现] 每次迭代（i <= after && end < text.Length）：准备 var next = text.IndexOf('\n', end)；按 next < 0 分支处理；执行 end = next + 1。
            var next = text.IndexOf('\n', end);
            if (next < 0)
            {
                // [实现] 条件成立（next < 0）时：执行 end = text.Length；执行 break;。
                end = text.Length;
                break;
            }

            end = next + 1;
        }

        var snippet = text[start..Math.Min(end, start + 2200)].Trim();
        return Redact(snippet);
    }

    // [实现] AddStructuredEvidence 的实现：执行 void Add(string id, string cause, string advice, Match match, string confidence = "日志中的排查线索") {…；准备 var suspect = Find(text, @"^[ \t]*Suspected Mods?:[^\n]*")；按 suspect.Success && !Has( Evidence(text, suspect.Index), @"Suspected Mods?:[ \t]*(?:None\b|\[\])… 分支处理。
    private static void AddStructuredEvidence(string text, List<DiagnosticFinding> findings)
    {
        // [实现] Add 的实现：按 match.Success 分支处理。
        void Add(string id, string cause, string advice, Match match, string confidence = "日志中的排查线索")
        {
            if (match.Success)
                findings.Add(
                    new(
                        cause,
                        advice,
                        Evidence(text, match.Index, 10)
                    )
                    {
                        RuleId = id,
                        Confidence = confidence
                    }
                );
        }

        var suspect = Find(text, @"^[ \t]*Suspected Mods?:[^\n]*");
        if (suspect.Success && !Has(
            Evidence(text, suspect.Index),
            @"Suspected Mods?:[ \t]*(?:None\b|\[\])|Suspected Mods?:[ \t]*\n[ \t]*None\b"
        ))
            Add("PCL-SUSPECTED-MODS", "崩溃报告列出可疑模组", "这是报告的怀疑名单，不能单独确定根因。保留原实例，在副本中逐项禁用并检查前置关系。", suspect);
        var fatal = Has(text, @"[A-Za-z.]+Exception\b|\bFATAL\b|crash report|Unreported exception");
        if (fatal && Has(
            text,
            @"Description:\s*(?:Ticking (?:block )?entity|Exception ticking world|Rendering entity)|-- (?:Entity|Block entity) being"
        ))
        {
            // [实现] 条件成立（fatal && Has( text, @"Description:\s*(?:Ticking (?:block )?entity|Exception ticking world|Rende…）时：执行 Add( "PCL-WORLD-BLOCK", "崩溃上下文涉及特定方块", "先备份世界，比较同一实例的新世界与原位置是否复现，再检查该方块所属模组。诊断不会删除方块或世界。", Find…；执行 Add( "PCL-WORLD-ENTITY", "崩溃上下文涉及特定实体", "保留实体 ID 和位置，先备份世界并在新世界复测，再检查实体所属模组；不自动删除实体或存档。", Find(…。
            Add(
                "PCL-WORLD-BLOCK",
                "崩溃上下文涉及特定方块",
                "先备份世界，比较同一实例的新世界与原位置是否复现，再检查该方块所属模组。诊断不会删除方块或世界。",
                Find(text, @"^\s*Block(?: location)?:[^\n]{1,250}")
            );
            Add(
                "PCL-WORLD-ENTITY",
                "崩溃上下文涉及特定实体",
                "保留实体 ID 和位置，先备份世界并在新世界复测，再检查实体所属模组；不自动删除实体或存档。",
                Find(text, @"^\s*Entity(?: Type|'s Exact location):[^\n]{1,250}")
            );
        }

        if (!fatal || findings.Count > 0)
            return;
        if (!Has(text, @"Forge|Fabric|Quilt|LiteLoader"))
            return;
        var ignored = new[]
        {
            "java.",
            "javax.",
            "sun.",
            "jdk.",
            "org.lwjgl.",
            "com.sun.",
            "net.minecraftforge.",
            "net.neoforged.",
            "net.minecraft.",
            "com.mojang.",
            "cpw.mods.",
            "com.google.",
            "org.apache.",
            "org.spongepowered.",
            "net.fabricmc.",
            "org.quiltmc.",
            "com.electronwill.nightconfig.",
            "it.unimi.dsi."
        };
        var stackEnd = text.IndexOf("System Details", StringComparison.OrdinalIgnoreCase);
        var stack = stackEnd >= 0
            ? text[..stackEnd]
            : text;
        var frames = Regex.Matches(
            stack,
            @"^\s*at (?<name>[a-zA-Z_][\w.$]{3,240})\([^\r\n]{0,200}\)",
            RegexOptions.Multiline,
            TimeSpan.FromMilliseconds(500)
        )
            .Cast<Match>()
            .Where(
                x => !ignored.Any(
                    prefix => x.Groups["name"].Value.StartsWith(prefix, StringComparison.Ordinal)
                )
            )
            .Take(10)
            .ToArray();
        var words = frames.SelectMany(
            x => x.Groups["name"].Value.Split('.')
                .Take(4)
        )
            .Where(
                x => x.Length > 2 && !new[]
                {
                    "com",
                    "org",
                    "net",
                    "dev",
                    "mods",
                    "mod",
                    "client",
                    "server",
                    "common",
                    "mixin",
                    "mixins",
                    "core",
                    "impl",
                    "api",
                    "util",
                    "render",
                    "world",
                    "entity",
                    "github",
                    "gitlab"
                }.Contains(x)
            )
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(11)
            .ToArray();
        if (words.Length is 0 or > 10)
            return;
        var lines = text.Split('\n')
            .Where(
                x => x.Contains(".jar", StringComparison.OrdinalIgnoreCase) || Has(x, @"^\s{2,}[a-z0-9_-]+:\s+")
            )
            .Where(
                x => words.Any(
                    word => Has(
                        x,
                        @"(?<![a-z0-9])" + Regex.Escape(word) + @"(?![a-z0-9])"
                    )
                )
            )
            .Take(5)
            .ToArray();
        var detail = string.Join(
            "\n",
            frames.Select(
                x => x.Value.Trim()
            )
        );
        if (lines.Length > 0)
            detail += "\n匹配模组表：\n" + string.Join("\n", lines);
        findings.Add(
            new(
                lines.Length > 0
                    ? "堆栈线索匹配到模组表"
                    : "堆栈中发现待核对的模组关键词",
                "这是低置信度关联，不证明该模组有错。结合根异常、依赖与最近改动，在副本中逐项验证；保留完整日志。",
                Redact(
                    detail[..Math.Min(detail.Length, 2200)]
                )
            )
            {
                RuleId = lines.Length > 0
                    ? "PCL-STACK-MOD"
                    : "PCL-STACK-KEYWORD",
                Confidence = "低置信度，需复测"
            }
        );
    }

    // [实现] ReadLog 的实现：按 new FileInfo(file).Length > 20 * 1024 * 1024 分支处理；返回 File.ReadAllText(file)。
    public static string ReadLog(string file)
    {
        if (new FileInfo(file).Length > 20 * 1024 * 1024)
            throw new InvalidDataException("日志超过 20 MiB，请先截取崩溃附近内容。");
        return File.ReadAllText(file);
    }

    // [实现] Export 的实现：准备 var text = Redact( ReadLog(file) )；准备 var output = new FileStream(destination, FileMode.CreateNew)；准备 var zip = new ZipArchive(output, ZipArchiveMode.Create)。
    public static void Export(string file, string destination)
    {
        var text = Redact(
            ReadLog(file)
        );
        using var output = new FileStream(destination, FileMode.CreateNew);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(
            zip.CreateEntry("log-redacted.txt")
                .Open()
        ))
            writer.Write(text);
        using (var writer = new StreamWriter(
            zip.CreateEntry("analysis.txt")
                .Open()
        ))
            foreach (var finding in Analyze(text))
                writer.WriteLine($"{finding.Cause}\n{finding.Advice}\n{finding.Evidence}\n");
    }
}
