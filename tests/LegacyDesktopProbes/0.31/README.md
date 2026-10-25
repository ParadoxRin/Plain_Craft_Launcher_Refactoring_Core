# 0.31 旧桌面探针归档

本目录保存迁移前的同进程组合根与旧 UI 探针源码，仅供历史取证，不参与生产构建。它们引用旧 Core 接口，不能代表 0.32 验收。

0.32 的替代证据来自 Pcl.ShellBoundary.Tests（只引用 Contract/Client）、Pcl.Sessions.Tests、MainLinkSmoke、BrowserAccountSmoke、ShellBoundarySmoke、RemoteDesktopSmoke 和最终包独立游戏运行脚本。

生产 App 不再支持 --in-process；显式使用该内部调试参数会显示错误，不会悄悄回退到 Core。
