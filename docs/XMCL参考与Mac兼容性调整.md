# XMCL 参考来源与兼容映射

## 参考来源和边界

实现参考 Voxelum/x-minecraft-launcher 的固定提交 **029f9ba466c181aebe5884439e4c65b481b175e3**。来源文件：

- [packages/core/launch.ts](https://github.com/Voxelum/x-minecraft-launcher/blob/029f9ba466c181aebe5884439e4c65b481b175e3/packages/core/launch.ts)：默认启动前检查客户端、库、原生解压；macOS Dock 名称/图标；分离 JVM 和游戏参数。
- [packages/installer/java.ts](https://github.com/Voxelum/x-minecraft-launcher/blob/029f9ba466c181aebe5884439e4c65b481b175e3/packages/installer/java.ts)：版本检测同时读取 stdout/stderr，保留进程诊断，识别只有主版本号的 JVM。
- [xmcl-runtime/java/installDefaultJava.ts](https://github.com/Voxelum/x-minecraft-launcher/blob/029f9ba466c181aebe5884439e4c65b481b175e3/xmcl-runtime/java/installDefaultJava.ts)：区分 mac-os-arm64 与 mac-os 的官方 Java 清单。
- [xmcl-runtime/java/zulu.ts](https://github.com/Voxelum/x-minecraft-launcher/blob/029f9ba466c181aebe5884439e4c65b481b175e3/xmcl-runtime/java/zulu.ts)：验证缓存结构并提供发行版后备，避免损坏缓存阻断选择。
- [xmcl-runtime/nativeReplacer/pluginNativeReplacer.ts](https://github.com/Voxelum/x-minecraft-launcher/blob/029f9ba466c181aebe5884439e4c65b481b175e3/xmcl-runtime/nativeReplacer/pluginNativeReplacer.ts)：按系统、架构、版本范围和用户设置替换旧库。
- [LICENSE](https://github.com/Voxelum/x-minecraft-launcher/blob/029f9ba466c181aebe5884439e4c65b481b175e3/LICENSE)：MIT，版权 ci010。

这些文件作为实现参考，未引入 Electron、TypeScript 运行时或整套 XMCL。兼容映射摘录自同一提交的 [natives.json](https://github.com/voxelum/x-minecraft-launcher/blob/029f9ba466c181aebe5884439e4c65b481b175e3/xmcl-runtime/nativeReplacer/natives.json)，适配实现位于 `LegacyMacLibraries` 等核心服务。

现代版本继续使用官方元数据，旧库按系统、架构与版本范围选择固定映射，不全局替换 LWJGL，也不以 Intel Java 替代 ARM64 Java。完整的接入和运行限制见[支持范围](支持范围.md)。

再分发须保留 [XMCL MIT 许可](XMCL-LICENSE.txt)及[第三方归属说明](THIRD-PARTY-NOTICES.md)。修改来源映射时同步来源记录及对应回归测试。
