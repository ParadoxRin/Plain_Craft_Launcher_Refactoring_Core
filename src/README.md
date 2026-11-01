# 源码组件索引

源码保持按职责分层。交付包组合、跨分类资产和新功能流程见[仓库结构与更新分块](../docs/仓库结构与更新分块.md)，编译依赖见[架构说明](../docs/架构与设计取舍.md)。

| 分类 | 目录 | 职责 |
|---|---|---|
| 客户端 | [Pcl.Desktop](Pcl.Desktop/) | 当前 Avalonia 参考 UI |
| 客户端 | [Pcl.Client](Pcl.Client/) | 通信、订阅、重连及可选 Host 更新部署 SDK |
| 客户端/Host 兼容边界 | [Pcl.Contract](Pcl.Contract/) | 公开数据、接口、能力和错误；随两端配套携带 |
| Host 核心 | [Pcl.Host](Pcl.Host/) | 后台入口、组合根、会话与根锁 |
| Host 核心 | [Pcl.Application](Pcl.Application/) | 用例编排、受理、任务生命周期 |
| Host 核心 | [Pcl.Domain](Pcl.Domain/) | 纯业务规则与约束 |
| Host 核心 | [Pcl.Ports](Pcl.Ports/) | 外部能力接口 |
| Host 核心 | [Pcl.Core](Pcl.Core/) | 适配器与既有安装、下载、资源、文件和进程实现 |
| Host 兼容依赖 | [Pcl.Legacy](Pcl.Legacy/) | 仍在使用的旧规则代码 |
| 原生运行依赖 | [Pcl.Native](Pcl.Native/) | macOS 窗口桥接；当前构建产物可随 Desktop/Host 复制 |
| 共享协议源码 | [SharedProtocol](SharedProtocol/) | 按项目文件链接编译，不是独立更新包 |
| 开发/命令行入口 | [Pcl.Cli](Pcl.Cli/) | 既有 CLI；尚有 Core/Application 直接引用 |

数据与外部运行环境没有同名顶层项目：百科索引目前在 vendor，兼容表位于 Core，部分布局/矩阵位于 docs；Java/EasyTier 的获取执行仍在 Core。分类不意味着这些内容已外置或已经可单独热更新。

新客户端优先参考 [examples/Pcl.MinimalClient](../examples/Pcl.MinimalClient/README.md)，仅通过 Client/Contract 或等价协议接入。新增纯规则放 Domain、外部 I/O 放适配器；不要为了展示入口给 Desktop 增加 Core/Application 运行时引用。
