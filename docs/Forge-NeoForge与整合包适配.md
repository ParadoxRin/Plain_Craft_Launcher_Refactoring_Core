# Forge、NeoForge 与整合包：Mac 适配和验收

> **0.8 更新说明**：本文正文保留 0.4 历史实现与验收。当前已经增加多版本、Quilt、CurseForge/HMCL/MultiMC/MCBBS/普通 ZIP、导出、更新副本和世界数据包；具体格式边界与分层证据见《整合包资源扩展与验收》。正文旧限制和旧测试数量不代表当前版本。

日期：2026-10-23。版本：0.4。目标：Minecraft 1.21.1 / Apple Silicon；第三方移植，尚未达到 PCL2 全功能一致。

## 官方要求与实现

[Forge 1.21.x 文档](https://docs.minecraftforge.net/en/1.21.x/gettingstarted/)要求 Java 21 和 64 位 JVM。[NeoForge 用户手册](https://docs.neoforged.net/user/docs/)列出 macOS 安装步骤，1.20.5 以后要求 Java 21。[客户端说明](https://docs.neoforged.net/user/docs/client/)采用官方安装器。它们没有承诺每个第三方模组、光影或整合包都兼容 Apple GPU / ARM64。

本版复用已下载的 Mojang macOS ARM64 Java 21，未要求修改系统 Java。分别从官方 Maven 列举 1.21.1 的版本，下载并校验安装器 SHA-1：

- Forge：`https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml`
- NeoForge：`https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml`

使用[官方 Forge Installer](https://github.com/MinecraftForge/Installer)的 `--installClient` 和 NeoForge 的 `--install-client` 客户端流程，保留官方处理器、补丁和输出校验。执行前按安装器清单预取并校验依赖；没有将 installer.jar 当作普通模组，也没有用服务器安装代替客户端。

每个实例单独保存库与客户端副本。NeoForge 的模块路径、冒号分隔符、ignoreList/version_name 由元数据生成，避免覆盖原版与 Fabric。生成的补丁文件无远程 URL 时标为本地产物；记录 SHA-1，丢失后要求重装，不能伪造下载地址。安装失败保留隐藏工作目录和处理器日志；取消会结束本次安装器进程。已有不完整实例需换新名称安装，不静默覆盖用户存档。

## 整合包

依据 [Modrinth .mrpack 官方格式](https://support.modrinth.com/en/articles/8802351-modrinth-modpack-format-mrpack)实现：

- Modrinth 搜索、版本选择、下载；本地 `.mrpack` 导入。
- 当前支持 Minecraft 1.21.1，依赖 Fabric、Forge 或 NeoForge 中的一个；使用包指定的加载器版本。其他游戏版本与 Quilt 明确提示未支持。
- 跳过 client=unsupported 文件；默认不选 optional，可勾选“包含可选文件”（当前为全部选入，尚无逐文件选择）。
- 文件大小、必需 SHA-1 / SHA-512 校验；HTTPS 来源限定为格式允许的 Modrinth CDN、GitHub、raw.githubusercontent.com、GitLab。
- 先下载文件，再解压 overrides，最后 client-overrides；server-overrides 不应用。
- 检查路径越界、保留启动器文件、符号链接、大小写冲突和覆盖大小。独立临时实例全部完成后才发布到版本列表；失败不会覆盖既有实例。
- 包内模组、光影和资源包登记到“已安装”，可以启停、移除。其身份标为整合包本地文件，尚不支持整包升级或依据原平台 ID 自动更新包内文件；也没有推导完整的包内前置图，改动模组可能破坏整合包。

这些包包含可执行 Java 模组；应选择信任的作者。没有实现 CurseForge ZIP/API、HMCL/MultiMC 包、导出、服务器包或任意外部实例迁移。

## 模组和光影边界

模组搜索和版本按所选实例的 Fabric / Forge / NeoForge 筛选，必需 API 前置跟随同一加载器。安装检查各自 fabric.mod.json、META-INF/mods.toml、META-INF/neoforge.mods.toml，避免明显装错加载器。Fabric 保留包内 depends/breaks 解析；Forge/NeoForge 的 TOML 版本约束尚未完整迁移，仍由实际加载器作最终检查，不能称为 PCL 完整冲突解析。

当前光影配置仍走 Fabric/Iris。Forge/NeoForge 的 Oculus、其他 Iris 构建及 OptiFine 组合尚未提供自动适配。Iris 官方 Apple Silicon 支持限制仍适用，详见《Mac依赖与光影适配.md》。不能把 Java 加载器启动成功泛化为所有光影兼容。

## 本机实测

主机 Apple M4 Pro，原生 aarch64 Java 21.0.7。证据位于 `artifacts/evidence`、`artifacts/*-game.log` 和数据目录 evidence。

| 实例 | 下载与安装 | 实际启动 |
| --- | --- | --- |
| Forge 52.1.20 | 官方安装器成功；补丁 SHA-1 与官方元数据一致 | 40 秒运行，图形、OpenAL、纹理图集初始化 |
| NeoForge 21.1.264 | 官方客户端处理器完成；库校验通过 | 40 秒运行，图形、OpenAL、纹理图集初始化 |
| Adrenaline 26.5.1+mc1.21.1.fabric | 真实 Modrinth 包，Fabric 0.19.5，30 个清单文件，覆盖配置 | 40 秒运行，Apple M4 Pro / OpenGL 4.1，渲染和纹理初始化 |

另通过真实 Modrinth 下载路径安装并启动 Forge 版 Xaero’s Minimap forge-1.21.1-26.6.0、NeoForge 版 FerriteCore 7.0.3-neoforge。两者各运行 40 秒并完成渲染初始化。Xaero 赞助检查出现网络超时警告；FerriteCore 有 refmap 警告，均未阻止本次启动。

还生成了两个最小 .mrpack 集成夹具，分别固定上述 Forge/NeoForge 和对应真实模组。二者均经“临时安装器 → 下载模组 → 重命名客户端和实例 → 发布 → 启动”链路完成，并各运行 35 秒进入渲染初始化。它们是本地测试夹具，不能当作已验收大型第三方 Forge/NeoForge 整合包的证据。

以上均使用官方试玩模式；测试脚本在检查后主动结束自己的进程，退出码 137 不当作正常游戏退出或崩溃证据。本轮没有用户目视验收或进入世界游玩，正版登录仍待用户 OAuth client ID。0.3 光影世界画面曾由用户确认，但不能代替本轮 Forge/NeoForge/整合包的世界验收。

三个实例的游戏库完成 Mach-O 检查：适用的原生库含 ARM64；官方保留的 macos/x64 作用域资源没有误删。该检查不覆盖所有模组动态下载/自带的本机库。Adrenaline 日志明确禁用 Apple GPU 的 fast buffer upload，部分纹理优化因 OpenGL 4.1 不可用；仍有非致命资源/着色器警告。并非零警告或全功能兼容承诺。

自动测试 41/41 通过，包含原下载回归、整合包环境/双哈希/路径/覆盖顺序/符号链接、加载器元数据、私有模块路径、本地产物不可回退网络、实例隐藏与资源管理。运行包另做 ZIP 完整性、解压后签名和自包含界面检查。
