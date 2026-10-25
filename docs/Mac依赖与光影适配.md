# Mac 依赖与光影适配

核查日期：2026-10-23。目标：Apple Silicon、Minecraft Java 1.21.1、原生 macOS；不将 Windows 兼容层作为默认依赖。官方手册的支持承诺、包内版本声明、本机运行结果分开记录。

## 依赖支持与选择

| 层 / 组件 | 官方依据与边界 | 本项目处理 |
| --- | --- | --- |
| .NET / Avalonia | [.NET macOS](https://learn.microsoft.com/en-us/dotnet/core/install/macos)、[Avalonia 支持平台](https://docs.avaloniaui.net/docs/supported-platforms)提供 ARM64 路线。它们负责启动器界面，与游戏的 Java/OpenGL 分开。 | 自包含 osx-arm64 包；不要求系统安装 SDK。当前宿主已运行验证，不据此推断所有旧 macOS 版本均通过。 |
| Java | [Fabric macOS Java 文档](https://docs.fabricmc.net/players/installing-java/macos)要求按芯片选择 aarch64/x64。该文档默认已切到新 Minecraft，不应把其中 Java 25 套给 1.21.1；[NeoForge 用户手册](https://docs.neoforged.net/user/docs/)及本地 Mojang 1.21.1 元数据的 javaVersion.majorVersion 都对应 Java 21。 | 使用已校验的 Mojang mac-os-arm64 Java 21，启动前检查真实主版本与架构。保留进程参数 -XstartOnFirstThread。 |
| LWJGL / GLFW / OpenAL / FreeType | [LWJGL 官方原生库目录](https://www.lwjgl.org/browse/stable/bin/lwjgl)提供 macos-arm64 分类器；[GLFW macOS 说明](https://www.glfw.org/docs/latest/compat_guide.html)描述原生 OpenGL 的限制。 | 采用 Mojang 此版本的库版本与规则，未随意升到最新 LWJGL。0.5 在每次真实启动前统一检查游戏库中适用 dylib/jnilib 的 Mach-O ARM64，包括 JNA、LZ4 与 Objective-C 桥。官方 macOS FreeType 补丁含 macos/x64 路径，ARM64 不加载该路径，保留并标记为不适用，不能因文件名含 Intel 就删除。 |
| Fabric / Fabric API | [Fabric 安装手册](https://fabricmc.net/wiki/install)提供跨平台 Java 安装方式。Fabric API 是公共模组接口，不是光影或图形驱动。 | 基础配置固定 Loader 0.19.5、Fabric API 0.116.17+1.21.1，安装到独立实例。 |
| Sodium | [官方 README](https://github.com/CaffeineMC/sodium)的正式显卡支持要求比 Minecraft 最低要求高；旧驱动可能工作但不获保证。不能把 macOS OpenGL 4.1 写成满足其 OpenGL 4.5 支持承诺。 | 只在实验光影实例采用已启动测试的 0.6.13；不放进基础配置，也不自动跳到最新 0.8.x。 |
| Iris | [1.21.1 分支驱动手册](https://github.com/IrisShaders/Iris/blob/1.21.1/docs/usage/drivers.md)明确不为 Apple Silicon 驱动提供支持；Intel Mac 也属弃用路线。 | 固定 1.8.8，标记“实验”。前次本机启动成功是具体组合的证据，不改写上游支持结论。根据包内 depends.sodium=0.6.x 选择 0.6.13。 |
| 光影所需 OpenGL 功能 | [Iris macOS 手册](https://shaders.properties/current/reference/miscellaneous/macos/)列出原生 4.1 缺少 compute shaders、custom images、SSBO 等；[功能标志手册](https://shaders.properties/current/reference/shadersproperties/flags/)区分 required 与 optional。 | 应用前检查 required 中的 COMPUTE_SHADERS、CUSTOM_IMAGES、SSBO，发现明确要求便拦截。optional、条件分支或存在 .csh 不自动等于整包不可用；检查不充分或通过均需世界内测试。 |
| Complementary Reimagined | [发布者官网](https://www.complementary.dev/shaders/)说明需要光影加载器；[源码](https://github.com/ComplementaryDevelopment/ComplementaryReimagined)中有 MC_OS_MAC 回退。实际 r5.9.3 的 common.glsl 与 shaders.properties 会在 Mac 关闭高级彩色光照。 | 采用 r5.9.3 自带 MEDIUM 预设。COLORED_LIGHTING=0、WORLD_SPACE_REFLECTIONS=-1；写入外部 zip.txt 配置，保持光影 zip 原样。加载和识别成功不能替代世界渲染效果验收。 |
| Forge / NeoForge / OptiFine | [Forge 手册](https://docs.minecraftforge.net/en/latest/gettingstarted/)、[NeoForge 手册](https://docs.neoforged.net/user/docs/)使用对应 Java；[OptiFine 下载表](https://optifine.net/downloads)有 1.21.1 J1。Java 分发不能被直接归为 Windows 专用，但也不代表任意模组支持 Mac。 | 0.4 已迁移 Forge/NeoForge 官方客户端处理器并实际启动；OptiFine 尚未迁移。各加载器实例独立，不混入 Fabric/Iris 配置。 |
| VulkanMod / Beryl / MGL / 新 Metal 渲染器 | [VulkanMod README](https://github.com/xCollateral/VulkanMod)将 Beryl 列为其光影路线；Iris 驱动手册提到 MGL 作为未来探索。 | 图形后端不同，需另建实例并核对 Minecraft/原生库版本，当前没有完成这条路线的实际验收。不会往 Iris 实例叠装图形驱动替换层。 |

## 已实现的两个配置

“Mac 配置”入口和 CLI `mac-setup` 创建 `instances/mac-base-1.21.1`：原生 Java 21、原版官方资源、固定 Fabric、Fabric API，不自动启用第三方渲染器。

`mac-setup --shaders` 创建 `instances/mac-shaders-1.21.1`：基础依赖 + Iris/Sodium + Complementary MEDIUM。这是实验配置。初次 defaults 为渲染距离 8、模拟距离 6、60 FPS 上限，已有游戏选项不被这些默认值覆盖；这是保守起点，不是性能承诺。

两者均不写入既有原版或普通 Fabric 实例，不复制账号或存档；共享官方 Java、游戏客户端、资源和库。已评估资源固定项目 ID、版本 ID、大小与 SHA-1；元数据变化会停止配置，等待重新评估。旧光影选项文件在重新应用预设前备份到 `.pcl-backups`。

## 静态检查的范围

检查器不是完整 GLSL 预处理器。已知 MC_OS_MAC 条件可以判断，其他未知条件只记录待运行验证；不靠扫描到“SSBO”或“#version 430”字样就删除整个光影。声明、回退源码、驱动行为、世界内效果是不同证据层。

## 复核命令

```bash
./.tooling/dotnet/dotnet src/Pcl.Cli/bin/Release/net10.0/Pcl.Cli.dll mac-setup --data-root runtime/apple-silicon-1.21.1
./.tooling/dotnet/dotnet src/Pcl.Cli/bin/Release/net10.0/Pcl.Cli.dll mac-setup --shaders --data-root runtime/apple-silicon-1.21.1
./.tooling/dotnet/dotnet src/Pcl.Cli/bin/Release/net10.0/Pcl.Cli.dll launch --profile mac-shaders --demo --data-root runtime/apple-silicon-1.21.1
./.tooling/dotnet/dotnet src/Pcl.Cli/bin/Release/net10.0/Pcl.Cli.dll mac-shader-inspect --file <光影.zip>
```

`artifacts/evidence/dependency-docs` 保存部分已读取的官方原文快照及来源 URL。运行结果和测试结果另存，不以本文替代。

## 本轮实际验收

Mac 光影实例已加载 59 个模组条目，识别 Complementary r5.9.3，创建主世界光影管线并启动内置服务器。用户进入试玩世界、观察画面并反馈“已进入世界，画面正常”，随后正常保存退出，进程返回 0。

日志仍记录新版本光影变量（BIOME_PALE_GARDEN、BIOME_SULFUR_CAVES、endFlashIntensity）解析警告、部分未使用属性的链接警告和 Apple 驱动 gldCopyBufferSubData 诊断。这些未阻止本次世界加载和用户视觉验收；本轮没有通过修改第三方光影源码掩盖它们，也不据此作长期稳定性、帧率或所有维度均通过的承诺。

31 项回归测试通过；适用 ARM64 原生二进制与按架构路径隔离的 x64 资源分别记录在 mac-native-audit.json。配置记录见 mac-base-setup.json / mac-shaders-setup.json，原始游戏日志单独保留。
