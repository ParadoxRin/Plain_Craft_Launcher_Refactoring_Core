# 参考 XMCL 的 macOS 兼容性调整（0.5）

日期：2026-10-23。目标仍是 Minecraft 1.21.1 / Apple Silicon。原有 0.4 ZIP 保留。

## 参考来源和边界

通过 GitHub 连接直接读取 Voxelum/x-minecraft-launcher，固定提交 **029f9ba466c181aebe5884439e4c65b481b175e3**，而非只阅读 README。实际读取六个文件：

- [packages/core/launch.ts](https://github.com/Voxelum/x-minecraft-launcher/blob/029f9ba466c181aebe5884439e4c65b481b175e3/packages/core/launch.ts)：默认启动前检查客户端、库、原生解压；macOS Dock 名称/图标；分离 JVM 和游戏参数。
- [packages/installer/java.ts](https://github.com/Voxelum/x-minecraft-launcher/blob/029f9ba466c181aebe5884439e4c65b481b175e3/packages/installer/java.ts)：版本检测同时读取 stdout/stderr，保留进程诊断，识别只有主版本号的 JVM。
- [xmcl-runtime/java/installDefaultJava.ts](https://github.com/Voxelum/x-minecraft-launcher/blob/029f9ba466c181aebe5884439e4c65b481b175e3/xmcl-runtime/java/installDefaultJava.ts)：区分 mac-os-arm64 与 mac-os 的官方 Java 清单。
- [xmcl-runtime/java/zulu.ts](https://github.com/Voxelum/x-minecraft-launcher/blob/029f9ba466c181aebe5884439e4c65b481b175e3/xmcl-runtime/java/zulu.ts)：验证缓存结构并提供发行版后备，避免损坏缓存阻断选择。
- [xmcl-runtime/nativeReplacer/pluginNativeReplacer.ts](https://github.com/Voxelum/x-minecraft-launcher/blob/029f9ba466c181aebe5884439e4c65b481b175e3/xmcl-runtime/nativeReplacer/pluginNativeReplacer.ts)：按系统、架构、版本范围和用户设置替换旧库。
- [LICENSE](https://github.com/Voxelum/x-minecraft-launcher/blob/029f9ba466c181aebe5884439e4c65b481b175e3/LICENSE)：MIT，版权 ci010。

这些文件作为实现参考，未引入 Electron、TypeScript 运行时或整套 XMCL。新增 C# 实现沿用本项目服务边界；读取的原文和 MIT 许可保存在验收包 xmcl-reference 中。并未审读整个仓库或承诺复制全部行为。

## 本次调整

| 部分 | 旧行为与问题 | 0.5 实现 |
| --- | --- | --- |
| 启动前检查 | 原生库审核是单独命令；实例客户端仅判断存在；缺库可能到 JVM 才失败 | GUI/CLI 统一走 LaunchService.PrepareAsync，在启动前按元数据检查客户端副本和所有游戏库的大小/SHA-1，再检查 Mac 原生架构，写实例 preflight 收据。失败给出修复提示，不在存档实例中静默重装 |
| 内嵌本机库 | 只审核文件名含 natives- 的 JAR，漏掉 JNA、LZ4、Objective-C 桥 | 扫描游戏库 JAR 内的 dylib/jnilib，检查 Mach-O ARM64/Universal；识别 darwin-aarch64、macos/arm64 等实际资源路径 |
| Intel 与调试资源 | 不能按整包包含 Intel 文件断言不兼容 | macos/x64、darwin/x86_64、darwin-x86-64 等资源仍保留并标为不适用；跳过 dSYM 调试包中的同名 DWARF 文件，不将其误判为运行时库 |
| Java 识别 | 依赖旧封装，错误记录直接终止自动安装 | 核心直接启动指定可执行文件，同时读取两个输出流；锚定版本标题行，避免把 OpenJDK 64-Bit 读成版本 64；arm64/aarch64、x86_64/amd64 归一化。非零退出不接受。15 秒超时及用户取消会结束本次检查进程 |
| Java 修复 | JSON 损坏、已删除的可执行文件或执行权限损坏可能让 stamp 先行失败 | 捕获这些管理运行时记录/检查错误，进入官方 ARM64 Java 21 清单和下载校验，恢复 POSIX 执行权限并重写记录。不会把系统 Intel Java 自动替换进 ARM64 游戏 |
| Mac 参数 | 第一线程标志只依赖继承元数据，Dock 显示依赖 JVM 默认行为 | 保留并去重 -XstartOnFirstThread，缺少时补充；增加 Dock 名称；从正确的 asset-index.objects 中选择并校验官方 icns 后传给 JVM。路径含空格/中文仍使用参数数组，无 shell 拼接 |

新增 `java-check [--java <executable>]` 命令：未指定文件时检查/修复管理 Java；指定文件时仅检查该文件，不替换系统 Java。

## 没有照搬的部分

- XMCL 的旧版原生库替换有条件。0.5 阶段仅验证 1.21.1；0.7 针对 1.16.5/1.18.2 摘录固定 XMCL 提交的 ARM64 替换数据，并保留 MIT 许可。现代版本继续保留官方库，不全局替换 LWJGL。
- 0.7 已接入 Azul Zulu Java 8 后备、LWJGL 3.2.1 的 ARM64 替换、JNA/Objective-C 适配及多版本 Java，详见《多版本与Mac兼容性.md》。LWJGL 2、Intel Mac 与 Rosetta 运行路线仍未实现；未适配的原生分类器明确报错。
- 原生扫描覆盖启动元数据中的游戏库，不是对每个模组动态下载的文件做安全或兼容认证。能通过静态架构检查也不代表所有模组/光影均能渲染。
- Iris/Apple Silicon 官方驱动支持限制不变。0.3 用户确认的世界画面只属于当时组合；0.5 运行检查不会替代新的世界内目视验收。
- 未执行关闭 Gatekeeper、去除任意目录隔离标记、修改系统 Java 或开启辅助功能权限的操作。分发仍使用本地 ad-hoc 签名，尚未公证。

## 验收

自动回归为 **46 项**：原 41 项，加上双流/架构别名、真实 JVM 输出格式、检查进程退出和取消、JNA/LZ4 作用域与 dSYM、客户端/库损坏拦截、Dock 图标哈希和第一线程去重。

本轮实机启动对象：原版、Forge 52.1.20（含 Xaero）、NeoForge 21.1.264（含 FerriteCore）、通过目录安装的 Adrenaline 26.5.1 包、实验光影实例。逐项结果以 artifacts/evidence-0.5 下的启动收据与日志为准，尚未出具收据的项目不能据此宣称通过。测试脚本会主动结束自己的进程；137 是测试结束信号，不能当作正常保存退出。未进行本轮世界游玩或正版登录验收。

构建包另外检查 ZIP 完整性、解压后的签名、自包含 UI 启动和中文渲染；完成情况写入 0.5 验收 JSON。
