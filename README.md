# Plain Craft Launcher Refactoring Core · PCLCS

PCL 原作者：**龙腾猫跃（LTCatt）** · [支持原作者](https://meloong.com/afd/a/LTCat)。本项目是第三方独立重构，并非 PCL 官方版本；继续遵守 [PCL 使用指南](PCL-LICENCE.txt)，保留原作者署名与支持入口。

PCLCS 将 Minecraft 启动器拆为可独立维护的客户端、后台核心、内容包和外部运行环境。当前图形客户端采用 Avalonia；后台通过私有本机协议提供安装、账户、资源、实例和游戏进程能力。

## 当前版本与支持范围

**0.43.0，build 428，macOS ARM64**。标签为 `v0.43.0`，频道为 `stable`。本版引入后台微软登录和按资源保护的并发操作：切页不打断登录，无关浏览不再被长任务锁住。Host 序号为 `43000`，内容序号保持 `42102`；手动升级，不推送自动更新。

| 项目 | 范围 |
|---|---|
| 用户安装包 | Apple Silicon（M 系列），macOS 14 或更高 |
| Windows / Linux | 核心有适配和自动化测试；本版 不提供这些系统的完整桌面产品承诺 |
| Intel Mac | 本版 不提供安装包 |
| .NET | 用户包自带运行时；源码开发需要 .NET 10 SDK，精确版本见 [global.json](global.json) |
| Java | 与 .NET 不同，由 Host 检测或按需获取；需匹配 Minecraft 与加载器版本 |
| 更新方式 | 本版 手动下载升级；不内置生产更新源，也不携带测试策略或测试信任根 |
| macOS 签名 | ad-hoc 签名，未经过 Developer ID 公证 |
| 验收边界 | 自动化验证与真实账户/游戏验收分别说明；以对应版本 Release 记录为准，详见 [发布条件](docs/发布与维护.md) |

完整功能边界见 [支持范围](docs/支持范围.md)。不要把编译通过、核心跨平台或独立 Host 测试理解为所有游戏组合均已支持。

## 下载、安装与手动升级

下载入口：[GitHub Releases](https://github.com/ParadoxRin/Plain_Craft_Launcher_Refactoring_Core/releases)。选择 `v0.43.0` 正式版，校验附件 `SHA256SUMS`。不要从第三方转载包取得信任公钥或账户配置。

- 普通用户选择 **macOS-arm64 应用 ZIP**，解压后将 `.app` 放入“应用程序”。首次启动会启动随包 Host。
- 开发者按需下载 **client.zip 与 host.zip**，分别解压并配置外部 Host；不需要重复下载完整应用。
- SDK 与源码用于开发；百科/映射内容、Java 和联机工具按各自机制管理。

Mac 安全策略可能阻止未经公证的应用。确认来源与校验和后，使用系统提供的“隐私与安全性 → 仍要打开”流程；不要全局关闭 Gatekeeper。若设备管理策略不允许，等待公证版。

手动升级前正常退出客户端与 Host，备份自己的实例配置和世界，将新版应用替换旧版。不要删除数据目录、只换单个 DLL，或在任务进行中替换程序。版本回退只在数据格式兼容且有备份时进行；本版不承诺跨版本数据自动迁移。

## 0.43.0 的操作方式

登录作为后台任务运行，全局保留状态、设备码和取消入口。切换页面不会取消登录；明确点击取消或退出客户端才结束授权。Host 按账户、实例和共享文件资源限制冲突写入，保留执行前复核。资源图标采用有限并发，避免占满任务队列。详见[后台任务与资源并发](docs/后台任务与资源并发.md)。

## 首次使用与限制

1. 选择或创建独立数据目录，确认路径和磁盘空间。不要把源码目录当游戏数据目录。
2. 在账户页按需登录；微软登录需网络与合法授权。离线身份不是正版授权，试用路径遵守游戏规则。
3. 配置兼容 Java，选择 Minecraft 版本与加载器，等待安装任务终态。
4. 启动前阅读授权/支持提示。遇到失败先查看任务错误码，再重试或取消；不要用新的任务重复写入状态未知的目录。

微软登录会在外部浏览器完成。若关闭网页或放弃授权，请返回启动器账户区域点击“取消登录”；等待取消完成后即可切换账户或重新登录，无需重启启动器。切换页面不会取消授权，其他页面仍可使用全局取消入口。

外置认证账户的配置/登录能力不等于支持游戏启动，当前后端会拒绝不支持的 External 启动。部分工作区写任务恢复会要求人工检查，这是防止未知提交状态被盲目重放的保护。验收组合为 Apple Silicon、Minecraft 1.21.1 原版及 Host 管理的 Java 21；CurseForge 在线认证、所有模组组合、皮肤和跨平台游戏图形行为不属于本版 已完整验证的范围。

独立游戏模式允许关闭启动器后继续游戏，日志保护不依赖 Host 存活。它只保护启动器捕获的标准输出/错误日志，不控制模组另行写入的文件。凭据由 Host 的平台凭据适配器管理，客户端不会取得原始微软令牌。

## 五类组件与协作边界

| 分类 | 内容 | 维护与交付方式 |
|---|---|---|
| 客户端 | Desktop、Client、匹配 Contract、Desktop.Updates、界面及原生资源 | 整体构建客户端；独立于 Host 源码编译 |
| Host 核心 | Host、Application、Domain、Ports、Core、Legacy、vendor、匹配 Contract 和运行时 | 按系统/架构整体交付；内部 DLL 不供用户自由混装 |
| 数据内容 | 百科与项目映射 | 有格式、兼容性、签名、序号与回退机制；其他内容不自动视为已外置 |
| 外部运行环境 | Java、EasyTier 等 | 按各自版本、平台及校验信息获取，不打包个人缓存 |
| 开发工具 | tests、scripts、tools、examples、SDK | 随源码或开发附件维护，不把历史测试输出放入用户包 |

完整 `.app` 是面向用户的安装布局组合，client/host 是面向开发与独立部署的组件。发布不再提供与它们重复的 `full.zip`。

`Contract` 是兼容边界，随两端分别携带；新增接口需要能力探测与兼容测试。同一数据根保持单一写入会话，多客户端实现不等于同时写入同一实例。没有通用动态插件加载系统，也没有正式发布的 Swift/Kotlin/Rust SDK。

## 源码架构

| 路径 | 职责 |
|---|---|
| `src/Pcl.Desktop`、`src/Pcl.Native` | Avalonia UI、窗口与平台界面集成 |
| `src/Pcl.Client`、`src/Pcl.Contract` | 请求、订阅、重连与双方 DTO/协议 |
| `src/Pcl.Desktop.Updates` | Mac 客户端替换与回退；不属于通用 Client SDK |
| `src/Pcl.Host` | 服务组合、进程与数据根所有权、会话、协议接线 |
| `src/Pcl.Application` | 操作受理、执行顺序、取消、恢复 |
| `src/Pcl.Domain`、`src/Pcl.Ports` | 纯规则与外部能力接口 |
| `src/Pcl.Core` | 执行实现、来源接入、平台适配和仍在迁移的既有逻辑 |
| `src/Pcl.Legacy`、`vendor/` | 兼容逻辑与保留来源的上游依赖 |
| `src/SharedProtocol` | 两端链接编译的协议实现，不是独立更新包 |
| `src/Pcl.Cli`、`examples/` | 通过协议接入 Host 的 CLI 和样例 |
| `tools/Pcl.CoreDiagnostics` | 直接调用核心的开发诊断工具，不是客户端架构样板 |

客户端表达用户意图；Host 权威校验并执行。Domain 不依赖 I/O；Core 实现 Ports，Host 负责组装。不要为了 UI 便利绕过协议读取凭据或直接构造核心业务服务。

## 开发者从这里开始

| 你的目标 | 第一步 | 后续参考 |
|---|---|---|
| 在核心上搭建自己的客户端 | [开发快速开始](docs/开发快速开始.md)，运行 `python3 dev.py example` | [客户端接入](docs/客户端接入指南.md)、[最小客户端](examples/Pcl.MinimalClient/README.md) |
| 修改或修复核心 | 快速开始后运行 `python3 dev.py test core` | [功能实现导览](docs/功能实现导览.md)、[测试指南](docs/测试指南.md)、[架构规范](docs/architecture/PCL-Mac架构规范.md) |
| 构建和发布产品 | [发布与维护](docs/发布与维护.md) | [分包构建与签名](docs/分包构建与签名.md)、[五类包连接规范](docs/五类包连接规范.md) |

```sh
# 从仓库根目录执行；Python 3.9+。Windows 可用 python 替代 python3。
python3 dev.py setup
python3 dev.py doctor
python3 dev.py test quick
python3 dev.py example
```

最小客户端会构建并启动独立 Host，自动创建新的隔离数据目录。输入 `accounts` 查看公开账户列表，输入 `exit` 正常退出。首次构建需还原 NuGet 包；setup 的自动 SDK 安装仅支持 Apple Silicon，其他平台见快速开始。

统一入口支持 `build host/core/client/sdk/example`、`test quick/core/contract/sources`。测试项目为控制台程序，入口会执行实际断言；`quick` 不包含 .NET 或游戏测试。完整命令、环境限制及按改动选测试的方法见 [测试指南](docs/测试指南.md)。

独立维护解决方案、SDK 导出、Desktop 接线和底层命令继续保留在 [开发指南](docs/开发指南.md)。所有现行文档按角色和用途列在 [文档导航](docs/README.md)。

功能开发从 [功能导航](docs/功能实现导览.md)、[完整扩展示例](docs/扩展示例-搜索来源状态.md) 和 [公共接口边界](docs/公共接口与内部实现.md) 开始。

## 贡献、问题反馈与许可

新功能先明确用户行为及失败/取消边界，复用已有安装事务、搜索、依赖规划和操作状态机；按组件职责实现，扩展 Contract 时同步客户端与 Host 并补兼容测试。分支合并前运行受影响测试，集成后再验证实际包。

请在 [Issues](https://github.com/ParadoxRin/Plain_Craft_Launcher_Refactoring_Core/issues) 提供版本/build、macOS/芯片、复现步骤、预期与实际行为、脱敏错误码。不要提交令牌、账户配置、完整个人路径或未经检查的游戏日志。敏感安全问题不要公开附上秘密。

项目并非所有部分统一 MIT。原创部分、PCL 指南和各上游依赖分别遵守各自条款；见 [LICENSE.md](LICENSE.md)、[PCL-LICENCE.txt](PCL-LICENCE.txt) 与 [第三方声明](docs/THIRD-PARTY-NOTICES.md)。保留 LTCatt 首位署名、支持原作者及正版购买提示；不以捐赠解锁主题或类似功能。
