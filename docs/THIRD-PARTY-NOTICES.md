# 来源与许可证

本工程是基于 PCL 的第三方 Mac 移植预览版，与 PCL 官方发布相区分。

- PCL / PCLCS：龙腾猫跃（LTCatt）及贡献者，来源 https://github.com/Meloong-Git/PCL ，提交 6b9a50df911c9d68b69b24c979efba477fb14b67。许可全文见 PCL-LICENCE.txt。
- MeloongCore：Meloong 及贡献者，来源 https://github.com/Meloong-Git/MeloongCore ，提交 98157d65c7627721c816c02e8be96ac4fe61d615；Apache-2.0，全文随包保留。Shared 内的 Mac 移植修改已在工程记录中标注。
- Avalonia 12.1.4：AvaloniaUI contributors，MIT。使用 Avalonia、Avalonia.Desktop 和 Avalonia.Themes.Fluent。
- Newtonsoft.Json 13.0.3：James Newton-King，MIT。
- ThrottleDebounce 2.0.2、PolySharp 1.15.0、Microsoft.Win32.Registry 5.0.0 及传递依赖：保留 NuGet 包中可用的许可证和声明，见 Legal/Dependencies。
- .NET 10：Microsoft / .NET contributors；运行时相关许可与第三方声明随包保留。

Minecraft 由用户运行程序时从 Mojang 官方源下载；Java 17/21/25 优先使用 Mojang 发布的 ARM64 运行时，缺失的主版本（例如 Java 8）从 Azul 官方下载并以其 SHA-256 校验，未包含在应用下载压缩包内。Minecraft 是 Mojang/Microsoft 的产品，本工程与其无关联。


Pcl.Legacy/MinecraftRules.vb 从 PCL 的规则检查逻辑提取并改为显式平台上下文；PCLCS 直接保留原数据模型与 Java 检查实现，进行了框架与 Mac 适配。新的下载/安装服务围绕 PCL 的临时文件、校验及安装流程重新划分边界，并非完整移入所有 ModNet 或全部启动器功能。

0.7 的旧版 Apple Silicon 原生库替换表摘自 XMCL / ci010 及贡献者，固定提交 029f9ba466c181aebe5884439e4c65b481b175e3 的 xmcl-runtime/nativeReplacer/natives.json，MIT 许可全文见 XMCL-LICENSE.txt。JNA 5.12.1 条目来自 Mojang 1.20.1 官方元数据。替换库仍在安装时按来源校验值下载，应用包不内含游戏库或 Java。

0.8 的外置认证按 [authlib-injector 官方规范](https://yushijinhun.github.io/authlib-injector/en/launcher-technical-specification.html)独立实现。认证组件仅在需要时从官方 API 下载未修改的 JAR 并校验 SHA-256；本轮联网核验为 1.2.8 / build 56。组件采用 AGPL-3.0-or-later 附 AUTHLIB-INJECTOR 例外，参见上游 [COPYING.md](https://github.com/yushijinhun/authlib-injector/blob/develop/COPYING.md) 和[完整许可](https://github.com/yushijinhun/authlib-injector/blob/develop/AGPLv3-with-authlib-injector-exception.txt)。组件没有预装进应用源码/运行包；用户下载的运行时组件由其上游许可约束。

0.8 EasyTier：EasyTier contributors，Apache-2.0；按需下载官方 2.6.4 macOS aarch64 二进制，GitHub 发布 SHA-256 为 `4be1882d1aa36d31c1d6ba0596f2cf8a097e371f8da124212324b2e0f8df7e4b`。来源 https://github.com/EasyTier/EasyTier/releases/tag/v2.6.4 。模块不预装在源码或应用包。

0.8 TOML 解析使用 Tomlyn 2.10.1（Alexandre Mutel，BSD-2-Clause），用于 Forge/NeoForge 模组元数据；锁定版本见 packages.lock.json，许可全文见 Tomlyn-LICENSE.txt。

0.10 `MavenComparableVersion.cs` 移植 Apache Maven 3.9.9 的 ComparableVersion（Apache Software Foundation contributors，Apache-2.0）；数值类型合并为 BigInteger，增加 ASCII/512 字符输入限制。原始来源 https://github.com/apache/maven/blob/maven-3.9.9/maven-artifact/src/main/java/org/apache/maven/artifact/versioning/ComparableVersion.java 。许可与 NOTICE 随包保留（Maven-LICENSE.txt、Maven-NOTICE.txt）。

0.11 MC 百科索引继续使用 PCL 基线中未修改的 PCLCS/Resource/WikiEntries.txt，来源和许可同上；百科资料来源 https://www.mcmod.cn/ 。新增检索与安装桥接独立实现，不内置抓取的百科文章、镜像文件或验证码绕过。

## Quilt / FlexVer compatibility (0.14)

Quilt version comparison and range behavior were adapted to C# and checked against the official 0.27.1 JAR using our own driver. Apache 2.0 license and copyright attribution: `Quilt-LICENSE.txt`, `Quilt-NOTICE.txt`. Raw version comparison follows FlexVer under CC0. No Quilt or JarJar runtime binary is redistributed in the launcher; test downloads are excluded from source and app packages.

0.22 内含未修改的 LWJGL Unsafe Agent 2.0（Glavo，Apache-2.0），仅针对 Java 25+ / LWJGL 3.4.0–3.4.1 组合加载；固定发布构件、哈希与来源见 LWJGL-UNSAFE-NOTICE.txt，许可见 LWJGL-UNSAFE-LICENSE.txt。
