# 多版本管理与 Mac 兼容性：0.8 增量

本节更新于 2026-10-23；下面保留 0.7 原始测试记录，其“尚未适配”等表述是当时状态。

- 新增旧 `minecraftArguments`、LWJGL 2 ARM64、虚拟资源映射与原生库提取。1.12.2 原版、Forge 14.23.5.2864、LiteLoader、OptiFine 已在 Apple Silicon 上完成实际启动验证。
- Java 16.0.2 ARM64 已用于 1.17.1 实测。修复 Azul 压缩包中的同名 bin 链接导致选错 Java 可执行文件的问题。
- NeoForge 1.20.1 使用官方 `net.neoforged:forge` 坐标，47.1.106 已完成安装、原生审计及启动；新增 Quilt 独立实例，1.20.1 / 0.30.1 实测。
- 新增 OptiFine 作者来源下载及内置 Patcher；1.12.2 G5、1.20.1 I6 单独安装实测。新增官方 LiteLoader 目录及 Maven 解析；1.12.2 对应构建实测。OptiFine + Forge 尚未组合适配；后续已加入下述 OptiFabric 固定组合。
- 实例“调整加载器”采用新副本事务：组件备份、变更预览、冲突检查、源文件指纹、失败清理。现有启用模组必须明确在副本中禁用，不覆盖或删除原实例。1.20.1 Fabric 0.18.4 → 0.19.5 副本升级后运行 40 秒，图形后端及纹理图集完成初始化，源元数据及配置保持不变。
- OptiFabric 固定组合：1.20.1 + Fabric 0.16.14 + OptiFabric 1.14.3 + OptiFine I6，Java 17 ARM64 实际运行 45 秒并完成图形/图集初始化；产品仅新建实例，合法 CurseForge API 或用户提供作者文件，检测 Iris / Sodium 冲突。详见 [组合与文件下载报告](OptiFabric固定组合与文件另存为.md)。
- “下载文件”可另存客户端、服务端、Forge / NeoForge / Fabric / Quilt / OptiFine 安装器及 LiteLoader 加载器 JAR；按来源校验并拒绝覆盖，不执行服务端或安装器。官方更新日志读取 Mojang 启动器内容服务，显示为纯文本。7 类实际下载通过；Quilt installer 的官方 Meta/Maven 哈希矛盾被正确拒绝保存（不影响已测 Quilt Loader 实例）。

本轮八组兼容启动、一组加载器迁移启动与一组 OptiFabric 组合启动均采用独立 `runtime-0.8-compat`，只停止本次测试进程。八组回执在 `artifacts/evidence-0.8-compat/compatibility-results.json`；迁移回执在 `artifacts/evidence-0.8-compat/migration-smoke/result.json`。旧版依据与限制详见 [旧版加载器与Java16适配](旧版加载器与Java16适配.md)。这些结果不保证所有历史版本、快照、加载器或模组组合，也没有扩展为世界游玩、微软账户和多人联网验证。光影预设仍明确限定 1.21.1。

---

# 多版本管理与 Mac 兼容性（0.7）

日期：2026-10-23。面向 Apple Silicon；当前原生主机上验证。此文件是实现和测试记录。

## 已实现

- 从 Mojang version_manifest_v2 获取完整版本列表，界面支持搜索、正式版/快照筛选。
- 同时安装多个 Minecraft 版本。1.21.1 保留原路径；其他原版使用 instances/vanilla-<版本>，Fabric/Forge/NeoForge 与整合包各自隔离目录。共享校验后的 assets/libraries 缓存，不跨版本复用存档配置。
- Java 主版本读取每个官方版本 JSON 的 javaVersion.majorVersion，检查真实 JVM 版本和 aarch64 架构。Java 17/21/25 从 Mojang ARM64 清单选择相同主版本（不依赖组件名称）；缺失主版本时查询 Azul 官方 ARM64 JRE API。支持的请求主版本为 8/16/17/21/25，16 尚待实际验证。
- Azul 下载采用官方 SHA-256，解压检查路径和链接边界并保留 POSIX 执行权限。8u504 API 大小为 40,158,500 字节，实际 40,158,493 字节，SHA-256 完全一致，因此不把该 API 大小作为严格完整性依据，实际值与 API 值均记录。
- 加载器列表按游戏版本查询；默认自动选择稳定 Fabric，无稳定版本则原版；可手动选择原版/Fabric/Forge/NeoForge。整合包按其自身依赖选择游戏和加载器。NeoForge 1.20.1 的旧坐标分支未实现，该版本使用 Forge 或 Fabric。
- 模组、光影、资源包与整合包检索跟随当前实例版本与加载器。NeoForge 1.20.2–1.20.4 使用旧式 META-INF/mods.toml 校验，其他现代版本使用 neoforge.mods.toml。本地 Fabric 模组导入读取实例的实际 Java 元数据。
- 旧版 1.16.5/1.18.2 的 LWJGL 3.2.1、Objective-C 桥接库按 XMCL 固定映射升级为 ARM64 兼容项；1.16.5 旧 JNA 升级到 Mojang 1.20.1 使用的 5.12.1。官方 JSON 另存 .official.json，适配元数据记录替换来源，不修改原始材料。
- 启动前校验文件与 Mach-O。对 Netty 文件名明确限定 x86_64 的可选资源、与 ARM64 JAR 成对的 JTracy Intel 资源标注为不适用；ARM 资源仍必须通过检查。遵照官方 logging.client 下载并校验日志配置，包括旧版本官方修复后的配置。

## 实测矩阵

所有下列项目均实际联网下载或校验缓存、以离线账户启动；图形后端与纹理图集初始化后，1.20.1 原版与 1.18.2 正常退出（0）；其余保持运行至观察上限 55 秒，由测试主动停止（137）。正常退出项未标记为 55 秒存活。

| Minecraft | 加载器/实例 | ARM64 Java | 结果 |
|---|---|---:|---|
| 1.16.5 | 原版，LWJGL/JNA/桥接库适配 | 8u504 / Azul | 下载、校验、启动通过 |
| 1.18.2 | 原版，LWJGL/桥接库适配 | 17.0.15 / Mojang | 下载、校验、启动通过 |
| 1.20.1 | 原版 | 17.0.15 / Mojang | 下载、校验、启动通过 |
| 1.21.1 | 原版回归 | 21.0.7 / Mojang | 下载、校验、启动通过 |
| 26.3 | 原版 | 25.0.1 / Mojang | 下载、校验、启动通过 |
| 1.20.1 | Fabric 0.19.5 | 17 | 官方加载器安装、启动通过 |
| 1.20.1 | Forge 47.4.28 | 17 | 官方安装器退出 0，启动通过 |
| 1.20.4 | NeoForge 20.4.253 | 17 | 官方安装器退出 0，启动通过 |
| 26.3 | Fabric 0.19.5 | 25 | 官方加载器安装、启动通过 |
| 1.20.1 | Fabric + Mod Menu 7.2.2 | 17 | 对应版本模组下载、安装、启动通过 |
| 1.20.4 | NeoForge 20.4.253 + JEI 17.3.1.5 | 17 | 对应版本模组下载、安装、启动通过 |
| 1.20.1 | Adrenaline 26.5.1+mc1.20.1.fabric，28 个包内文件 | 17 | .mrpack 下载、校验、安装、启动通过 |

自动匹配编排另对 1.20.1 和 26.3 实际执行，均选中稳定 Fabric 0.19.5 及匹配主版本的 ARM64 Java。版本窗口从实时清单读取 920 项，验证搜索、快照显示/隐藏与 1.20.1 实例标题切换。60/60 自动回归测试通过。ZIP 解压后的自包含应用在没有 DOTNET_ROOT 的环境中打开，签名验证通过；版本窗口、主页、资源/整合包与账户页面均重新渲染。

证据：artifacts/evidence-0.7/acceptance.json 汇总，*-launch*.json 记录各实例进程、Java 与图形初始化结果；*-preflight.json 记录逐项原生库审计；相应 .log 保留实际日志。初次审计/Java 大小误差失败记录保留，成功以 retest 回执为准。UI smoke 的文本筛选需等待 Avalonia TextChanged 事件后检查。scripts/verify-multi-version.py 可顺序重跑矩阵，--loaders 加入固定加载器组合。


## 边界

清单展示不是全版本保证。1.12.2 及以前使用 minecraftArguments/LWJGL 2，尚未适配；其他未列入实测矩阵的历史版本和快照也可能需要新增原生库规则。缺失适配时明确报错，不使用 Intel Java 或自动安装 Rosetta。自动 Fabric 不表示任意模组都兼容 Mac；模组的原生依赖与版本约束仍须分别检查。光影的已配置 Mac 预设继续限定 1.21.1，Iris 官方仍未为 Apple Silicon 提供支持保证。

本轮以离线账户进行启动测试。日志中的图形后端及纹理图集初始化、进程存活可证明游戏启动，不能代替人工进入世界、长时间游玩、多人连接或所有模组组合的验收。未使用微软账号授权，未把旧版的人工世界确认套用到新版本。

## 官方与源码依据

- [Mojang 官方版本清单](https://piston-meta.mojang.com/mc/game/version_manifest_v2.json)：各版本 JSON/SHA-1、Java 主版本、资产和类库信息；原始数据保留于 artifacts/version-research。
- [Mojang Java 运行时清单](https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json)。
- [Azul Metadata API](https://docs.azul.com/core/install/metadata-api)：按 macos/aarch64/Java 主版本查询，使用包详情的 SHA-256。
- [Fabric macOS Java 指南](https://docs.fabricmc.net/players/installing-java/macos)与 [Fabric Meta](https://meta.fabricmc.net/)：按版本获取稳定加载器配置。
- [Forge Maven](https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml)、[NeoForge Maven](https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml)、[NeoForge 用户文档](https://docs.neoforged.net/user/docs/)：使用官方安装器，根据目标 Minecraft 版本选择 Java。
- [NeoForge 1.20.4 模组文件文档](https://docs.neoforged.net/docs/1.20.4/gettingstarted/modfiles/)：按目标版本区分 mods.toml 与 neoforge.mods.toml。
- [XMCL 固定替换表](https://github.com/voxelum/x-minecraft-launcher/blob/029f9ba466c181aebe5884439e4c65b481b175e3/xmcl-runtime/nativeReplacer/natives.json)：仅摘录旧 Mac 库所需条目，MIT 许可随包附带。
