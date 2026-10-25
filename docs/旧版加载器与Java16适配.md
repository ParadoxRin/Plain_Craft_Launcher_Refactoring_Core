# 旧版 Minecraft、加载器与 Java 16 的 Mac 适配

日期：2026-10-23。基于既有 0.7 核心的增量实现。以下运行结果来自独立的 `runtime-0.8-compat` 目录，不使用或覆盖此前实例与存档。

## 本轮实际运行结果

| Minecraft | 加载器 | Java / 架构 | 本轮证据 |
| --- | --- | --- | --- |
| 1.12.2 | 原版 | Azul 8 / aarch64 | LWJGL 2、OpenAL、图集初始化，存活 40 秒 |
| 1.12.2 | Forge 14.23.5.2864 | Azul 8 / aarch64 | 官方客户端安装器返回 0，FML 加载、图集初始化，存活 40 秒 |
| 1.12.2 | LiteLoader 1.12.2-SNAPSHOT-r4CC2BB0-b4-4 | Azul 8 / aarch64 | 官方目录、Maven 快照及依赖安装，加载器与图集初始化，存活 40 秒 |
| 1.12.2 | OptiFine HD U G5 | Azul 8 / aarch64 | 作者官网下载、官方 Patcher 返回 0，独立实例渲染初始化，存活 40 秒 |
| 1.17.1 | 原版 | Azul 16.0.2 / aarch64 | 实际 Java 版本检查、LWJGL 3 及图集初始化，存活 40 秒 |
| 1.20.1 | Quilt 0.30.1 | Mojang 17 / aarch64 | 官方 Meta v3、Maven SHA-1，Quilt 与图集初始化，存活 40 秒 |
| 1.20.1 | NeoForge 47.1.106 | Mojang 17 / aarch64 | 旧 `net.neoforged:forge` 坐标、官方客户端安装器返回 0，图集初始化，存活 40 秒 |
| 1.20.1 | OptiFine HD U I6 | Mojang 17 / aarch64 | 作者官网与官方 Patcher，独立实例渲染初始化，存活 40 秒 |

8 个用例均使用离线账户。脚本只终止各自创建的进程，返回 137 表示该测试定时停止；通过条件同时要求图形库日志、图集生成和存活或正常退出。**没有把这些日志验收写成世界内游玩或光影效果验收。** 本轮也未测试微软真实登录、任意模组组合、联机、Intel Mac 或 Rosetta。

机器可读报告：`artifacts/evidence-0.8-compat/compatibility-results.json`。原始运行日志、安装日志、Java 回执与 preflight 记录存放在同目录及 `runtime-results/` 中。第一次 1.12.2 启动已产生图集，但旧 CLI 只识别 LWJGL 3 的日志，结果正确保留为待复查；扩展旧版日志判据后重新启动并通过。

## 实现与来源

- `LegacyLaunchAdapter.cs` 将官方 `minecraftArguments` 转成参数数组，支持引号及转义而不调用 shell；加入旧启动变量、原生库安全解压、`virtual` / `map_to_resources` 资源布局。旧参数解析与路径越界有自动测试；本轮运行覆盖 1.12.2，不代表每种早期资源布局均经过游戏实测。
- `LegacyMacLibraries.cs` 使用 [XMCL 固定提交的 osx-arm64 映射](https://github.com/voxelum/x-minecraft-launcher/blob/029f9ba466c181aebe5884439e4c65b481b175e3/xmcl-runtime/nativeReplacer/natives.json)。新增此前遗漏的 native-only 映射，LWJGL 2 使用其中指定的 `org.glavo.hmcl:lwjgl2-natives:2.9.3-rc1`，下载 SHA-1 为 `042ef2024a9a4054952c496c9ebcdb83345dabe0`。维持逐个 Mach-O arm64 审核。该映射明确移除的旧 JInput / text2speech 原生组件不会回退混入 Intel 二进制；旧手柄及旁白功能未验收。
- LWJGL 2 不添加 `-XstartOnFirstThread`，LWJGL 3 保持该参数；已分别以 Java 8 / 16 / 17 真启动验证。对应 `RequiresFirstThread` 容错和 Forge 完整旧参数覆盖有测试。
- `JavaService.cs` 从 [Azul 官方 Metadata API](https://api.azul.com/metadata/v1/zulu/packages/?java_version=16&os=macos&arch=aarch64&archive_type=tar.gz&java_package_type=jre&release_status=ga&availability_types=CA&latest=true) 得到 ARM64 Java 16.0.2，校验官方 SHA-256 和实际进程架构。该包既有 bundle 内 `bin/java`，也有指向它的目录符号链接；修复只枚举实体运行时文件，避免重复候选异常。Mojang 运行时入口直接取自官方清单，避免残留修复目录干扰。
- `FabricService.cs` 加入 [Quilt Meta v3](https://meta.quiltmc.org/v3/versions/loader/1.20.1) 分支；按语义数值排序稳定版，而不是假设接口首项最新。库以官方 Maven SHA-1 校验，Quilt 使用自己的实例标识与加载器标记。已有同名实例的加载器版本不同会明确停止，防止安装操作默默替换加载器。
- `ForgeService.cs` 对 NeoForge 1.20.1 使用 [旧官方 Maven 坐标](https://maven.neoforged.net/releases/net/neoforged/forge/maven-metadata.xml) 和 `forge-...-installer.jar` / `--installClient`；其他 NeoForge 版本保留新坐标及命令。Forge 1.12.2 的完整 `minecraftArguments` 替换原版参数，避免丢失 FMLTweaker。更早的 `versionInfo` 安装器模式给出明确未适配提示，避免空引用异常或猜测依赖。
- `LiteLoaderService.cs` 使用 [官方目录](https://dl.liteloader.com/versions/versions.json) 和 [官方 Maven](https://repo.mumfrey.com/content/repositories/snapshots/com/mumfrey/liteloader/1.12.2-SNAPSHOT/maven-metadata.xml)，解析时间戳快照及 SHA-1。Sponge 旧 `/maven/` 地址映射到其现行官方 `/repository/maven-public/`，ASM 使用 Maven Central，均保持 HTTPS。没有把 2017 年的 LiteLoader 目录更新时间误写为新版本发布。
- `OptiFineService.cs` 解析 [作者官方下载页](https://optifine.net/downloads)，从作者域名取得下载地址；调用安装包原有 `optifine.Patcher <base.jar> <diff.jar> <mod.jar>`，提取其内置 `launchwrapper-of`，生成独立实例。已有 PCL 2.99.0.0 的 `PCLCS/Minecraft/OptifineHashes.json` 用作已知版本 SHA-1 数据，来源标为 PCL 校验表；没有已知校验的较新官方包记录 HTTPS 来源与本地 SHA-256，不冒称有官方预期哈希。生成库记录补丁器产物哈希。OptiFine 仅在用户机器运行时下载，[作者版权说明](https://optifine.net/copyright) 所述二进制没有加入本软件和源码发布包。

XMCL 映射按已有 `docs/XMCL-LICENSE.txt` 与 `THIRD-PARTY-NOTICES.md` 归属保留；原始 PCL 校验表仍受其原项目许可证约束。

## 光影与组合安装边界

现有 Mac 光影快捷预设仍是 **1.21.1 + 固定 Fabric / Iris / Sodium / Complementary** 的独立实例。`MacSetupService` 在进入下载前校验游戏版本。其他 Minecraft 版本应在对应实例的资源页选择适配资源，不能直接套用 1.21.1 的固定依赖。Iris 的 Apple Silicon 支持边界及此前用户确认的世界内效果仅适用于原已测组合，不能推广为本轮 OptiFine 或全版本光影兼容。

最初八组 OptiFine 验证是独立安装，不代表 OptiFine + Forge、Iris/Sodium 共存或任意光影包通过。后续已完成一个固定 OptiFabric 组合的启动验证与独立安装服务，详见 [OptiFabric 固定组合与文件另存为](OptiFabric固定组合与文件另存为.md)。安全的后续组合安装需要读取 OptiFine 官方页面逐版本的 Forge 兼容声明、按目标实例检查冲突、备份并生成新实例、再实际启动。现有接口没有为了提供组合按钮而强行把 Iris/Sodium/OptiFine 放进同一实例。

后续已实现 `InstanceComponentService` 的新副本事务：组件备份、版本与 Java 重新匹配、模组显式禁用、数据指纹和失败清理。已有实例不原位覆盖；模组需在新副本重新检查启用。Fabric 1.20.1 从 0.18.4 升级到 0.19.5 的副本实际启动通过，证据见 `artifacts/evidence-0.8-compat/migration-smoke/result.json`。未经逐版验证的远古版本和不在固定 native 映射内的二进制仍可能拒绝启动，不能据此文宣称“所有 Minecraft 版本均兼容”。
