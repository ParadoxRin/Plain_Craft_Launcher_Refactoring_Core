# Plain Craft Launcher Refactoring Core · PCLCS

PCL 原作者：**龙腾猫跃（LTCatt）** · [支持原作者](https://meloong.com/afd/a/LTCat)。本项目是第三方独立重构，并非 PCL 官方版本。

PCLCS 是面向客户端开发与核心扩展的 Minecraft 启动器项目。Host 提供版本安装、账户、资源搜索、实例管理和游戏进程能力；客户端通过 SDK 或本机协议接入。仓库包含 Avalonia 参考客户端、CLI 和最小接入示例。

[开发快速开始](docs/开发快速开始.md) · [文档导航](docs/README.md) · [Releases](https://github.com/ParadoxRin/Plain_Craft_Launcher_Refactoring_Core/releases) · [Issues](https://github.com/ParadoxRin/Plain_Craft_Launcher_Refactoring_Core/issues)

## 快速开始

需要 Python 3.9+、[global.json](global.json) 指定的 .NET SDK；macOS/Linux 构建 Host 还需要 C11 编译器。

```sh
# 在仓库根目录执行；Windows 可用 python 替代 python3。
python3 dev.py setup
python3 dev.py doctor
python3 dev.py example
```

`setup` 优先复用兼容 SDK，自动安装仅支持 Apple Silicon。`example` 构建最小客户端与 Host，并创建独立数据目录；输入 `accounts` 查看账户列表，输入 `exit` 退出。首次构建需要还原 NuGet 依赖。

## 构建与测试

```sh
python3 dev.py build host     # 后台入口
python3 dev.py build client   # 参考客户端
python3 dev.py build sdk      # Client / Contract
python3 dev.py test quick    # 文档、模块边界与打包检查
python3 dev.py test core     # 核心规则与执行逻辑
python3 dev.py test contract # 客户端与 Host 契约
python3 dev.py test sources  # 来源状态与错误隔离
```

也支持 `build core`、`build example`。独立解决方案为 `PCLCS.Core.slnx`、`PCLCS.Clients.slnx`、`PCLCS.ClientSdk.slnx`；底层命令与测试选择见[开发指南](docs/开发指南.md)和[测试指南](docs/测试指南.md)。

## 组件与职责

| 组件 | 职责 |
|---|---|
| `Pcl.Desktop`、`Pcl.Native`、`Pcl.Desktop.Updates` | Avalonia 界面、平台窗口集成与客户端更新 |
| `Pcl.Client`、`Pcl.Contract`、`SharedProtocol` | SDK、数据约定与通信实现 |
| `Pcl.Host` | 服务组装、会话、数据根与协议入口 |
| `Pcl.Application`、`Pcl.Domain`、`Pcl.Ports` | 操作编排、纯业务规则与外部能力接口 |
| `Pcl.Core`、`Pcl.Legacy`、`vendor/` | 执行实现、平台适配与上游兼容逻辑 |
| `Pcl.Cli`、`examples/`、`tests/`、`tools/` | 接入示例、测试与开发工具 |

客户端通过 Client/Contract 调用 Host；Host 负责权威校验与实际执行。新客户端复用协议，不直接依赖 Core。详见[架构说明](docs/架构与设计取舍.md)和[公共接口边界](docs/公共接口与内部实现.md)。

部署分为客户端、Host 核心、数据内容、外部运行环境、开发工具五类。客户端与 Host 各自携带匹配的 Contract；Host 内部依赖整体交付。独立客户端通过 `PCL_HOST_EXECUTABLE` 指向 Host 可执行文件。组件、SDK 与源码附件见 Releases，连接方式见[客户端接入](docs/客户端接入指南.md)和[五类包连接规范](docs/五类包连接规范.md)。

## 扩展与贡献

- 新建客户端：从[最小客户端](examples/Pcl.MinimalClient/README.md)开始，通过 SDK 或 NDJSON 协议连接 Host。
- 新增功能：通过[功能导航](docs/功能实现导览.md)定位组件，参考[搜索来源状态扩展示例](docs/扩展示例-搜索来源状态.md)。
- 修改协议：同步 Contract、Host 与 Client，维护能力探测和向后兼容。
- 提交改动：运行受影响测试；问题反馈附复现步骤、版本、平台及脱敏错误信息。

构建分发流程见[分包构建与签名](docs/分包构建与签名.md)。

## 许可

本仓库不同部分适用各自条款，并非统一 MIT。请遵守 [LICENSE.md](LICENSE.md)、[PCL 使用指南](PCL-LICENCE.txt)及[第三方声明](docs/THIRD-PARTY-NOTICES.md)，保留原作者署名与支持入口。
