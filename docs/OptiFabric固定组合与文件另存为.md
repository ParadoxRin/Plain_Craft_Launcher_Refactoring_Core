# OptiFabric 固定组合及独立文件下载（0.8）

更新：2026-10-23。本文件区分作者声明、程序实现、网络下载和实际运行证据。

## 已验证的 OptiFabric 组合

Minecraft **1.20.1** + Fabric **0.16.14** + OptiFabric **1.14.3** + OptiFine **HD U I6**，Mojang Java 17 ARM64。独立测试实例运行 45 秒，OptiFine、图形后端和纹理图集日志均出现，进程存活后由测试停止。初次组合探测证据：`artifacts/evidence-0.8-compat/optifabric/probe-result.json`。产品 `OptiFabricService.InstallAsync(localOptiFabric)` 又创建全新实例，经启动前 `ValidatePreset` 后实际运行 45 秒并通过相同判据，最终回执为 `artifacts/evidence-0.8-compat/optifabric/service-result.json`。没有进行新一轮世界游玩、光影效果、多人或微软真实登录验收。

来源与限制：

- [Chocohead 作者仓库](https://github.com/Chocohead/OptiFabric)，固定提交 `d9a13f3b8dec9396c239315db646781bba53d6cf`。README 要求 OptiFabric 和 OptiFine 安装器同时放入 `mods`；项目本身并非 Fabric 或 OptiFine 团队提供的支持。
- [作者发布文件 5025647](https://www.curseforge.com/minecraft/mc-mods/optifabric/files/5025647) 是 1.14.3 beta，列出 1.20.1，更新说明支持 Fabric Loader 0.15+。`fabric.mod.json` 声明 Sodium 冲突，内置 Fabric-ASM 与 optifabric-libs。
- [OptiFine 作者目录](https://optifine.net/downloads) 列出 1.20.1 I6。该版本二进制只由运行时从作者网站取得，不加入软件或源码发布包。
- 组合探测时，CurseForge 网页直取返回 403，无已配置 API Key，也无可用浏览器会话。作者发布页列出的 `curse.maven:optifabric-322385:5025647` 坐标能从第三方 Curse Maven 获取，因而用此文件做了兼容探测。它不是 CurseForge API 的官方校验回执；测试 SHA-256 是 `96ed4d1c33a61c4793825d244f0d1f853d432ef5b6c3be4b5e5aa0e69567c9ad`，来源记录在 `optifabric/download-receipt.json`。
- **产品安装服务没有 Curse Maven 下载路径。** 用户可以选择从作者页取得的相同文件，或者输入自己的 CurseForge API Key。API 的 `allowModDistribution=false` 或空 `downloadUrl` 会明确转为手动下载，不猜 CDN 地址。API Key 只在当次内存使用。

`OptiFabricService` 只创建新实例；版本组合和两个 JAR 均匹配测试指纹，变动核心版本/文件会拒绝启动。安装预检和启动预检识别 Iris、Sodium、Indium、作者声明的不兼容模组以及符号链接，失败不会覆盖既有实例。其他 Minecraft / OptiFabric / OptiFine 组合尚未启用，不能把这一例推广成万能兼容。

## 文件另存为

`ArtifactDownloadService` 与“下载文件”窗口提供原版客户端、服务端和加载器文件另存为，读取官方版本来源和校验值，临时下载、JAR 检查后原子保存，拒绝覆盖已有目标；服务端和安装器均不执行。Fabric / Quilt 是通用安装器；LiteLoader 导出的是加载器 JAR。

实际联网测试结果：客户端 1.20.1、服务端 1.20.1、Forge 47.4.28、NeoForge 47.1.106、Fabric installer 1.1.2、OptiFine I6、LiteLoader 1.12.2 均保存通过。检查确认未创建游戏、Java 或实例目录。Minecraft 1.20.1 官方更新日志也从 [Mojang 内容服务](https://launchercontent.mojang.com/v2/javaPatchNotes.json) 读取并转为纯文本。回执：`artifacts/evidence-0.8-compat/download-only-smoke/downloads/result.json` 和 `official-notes.json`。

**Quilt installer 上游校验冲突保留为失败。** 0.15.1 的官方 Meta 目录 SHA-1 为 `a6ea7a9e08e6f5ca399ea12101fbe56195a445e3`，但同一官方 Maven URL 的实际文件及 `.sha1` 为 `e4c4e80c91c94d999ffe3468ec2043792b841051`；SHA-512 同样分歧，文件大小均为 8,734,533 字节。检查最近八个版本均有 Meta / Maven 差异。应用在下载前交叉检查两个官方校验值并显示上游不一致，不静默切换预期哈希。最终预检实测在0.09秒内拒绝，目标文件不存在（`quilt-preflight-result.json`）。原始核对见 `download-only-smoke/quilt-official-metadata-check.json` 与 `quilt-hash-crosscheck.json`。此问题只涉及“另存 Quilt installer”，不影响通过另一路官方 Meta / Maven 安装且已实测的 Quilt Loader 0.30.1 实例。
