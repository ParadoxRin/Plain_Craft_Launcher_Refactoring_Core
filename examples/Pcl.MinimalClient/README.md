# Plain Craft Launcher Refactoring · 最小客户端

原作者：龙腾猫跃 · [赞助原作者](https://afdian.com/a/LTCat)。这是第三方基于 PCL 独立二次创作的接入示例，许可范围见仓库根目录 [LICENSE.md](../../LICENSE.md)。

这是一个可直接使用的交互式 .NET 命令行客户端。只引用 **Pcl.Client + Pcl.Contract**，不加载 Core、Application、Domain、Ports 或上游业务库。Host 是独立进程，包含业务实现；“客户端不依赖 Core”不代表 Host 也没有 Core。

## 构建与独立运行

仓库根目录使用 `global.json` 指定的 .NET 10 SDK：

```sh
dotnet build src/Pcl.Host/Pcl.Host.csproj -c Release
dotnet publish examples/Pcl.MinimalClient/Pcl.MinimalClient.csproj -c Release -p:DebugType=None -p:DebugSymbols=false -o artifacts/minimal-client-publish
```

发布目录应只含三个业务程序集：`Pcl.MinimalClient.dll`、`Pcl.Client.dll`、`Pcl.Contract.dll`，另有 .NET 运行配置和可选 apphost。整个目录可以复制到仓库外运行，需要 .NET 10 运行时；不能只复制其中一个 DLL。Host 使用单独构建目录或应用包内完整 `host/` 目录，不能只复制 Host DLL。

从任意目录运行，用绝对路径指定 Host 和新的隔离数据目录：

```sh
dotnet /absolute/client/Pcl.MinimalClient.dll \
  --host-dll /absolute/repository/src/Pcl.Host/bin/Release/net10.0/Pcl.Host.dll \
  --dotnet /absolute/dotnet \
  --data-root /absolute/isolated-data
```

如果 Host 已发布为自包含可执行文件：

```sh
dotnet /absolute/client/Pcl.MinimalClient.dll \
  --host /absolute/application/host/Pcl.Host \
  --data-root /absolute/isolated-data
```

使用本仓库自带 SDK 时，`dotnet` 可换为 `.tooling/dotnet/dotnet` 的绝对路径。不要把现有个人实例目录用于验收。数据根及父目录必须符合 Host 现有的链接/权限保护；在 Mac 上 `/tmp`、`/var` 可能是别名，先进入选定目录用 `pwd -P` 获取物理路径。客户端不会绕过 Host 的路径保护或删除遗留锁文件。

Windows PowerShell 使用同样参数并将命令写成一行；文件名按发布的 Host apphost 使用 `.exe`。三平台契约 CI 只证明本次覆盖的 Host 接入，不证明 Windows/Linux 真人凭据或游戏运行已适配。

## 使用顺序

程序启动后完成当前用户私有管道 attach + hello，并显示 Host 身份和能力。命令参数用空格分开；Minecraft 名称和幂等键不含空格。

```text
install 1.21.1 vanilla install-demo-001
operations
get op-从受理结果复制的ID
watch op-从受理结果复制的ID
cancel op-从受理结果复制的ID
reconnect
get op-从受理结果复制的ID
restart
resume op-从受理结果复制的ID resume-demo-001
watch op-续做返回的新ID
accounts
launch 安装成功结果中的instanceId trial launch-demo-001 detached
yes
exit
```

- `install` 立即返回 operationId。`watch` 通过订阅/快照显示进展，观察到终态后返回；Ctrl+C 仅停止本次等待，不等于取消任务。要在观察期间取消，先 Ctrl+C 再执行 `cancel`。
- 同一安装参数重试时使用原幂等键，会得到同一操作；改变参数却复用键会被拒绝。超时或连接中断时先查 `operations/get`，不要换键盲重试。
- `reconnect` 只重建连接，保留当前 Host 身份及后台操作。`restart` 显式关闭 Host 并启动新 Host，当前任务会按既有退出策略取消；它不模拟崩溃。
- `resume` 从旧公开快照取安装参数，以新幂等键提交新的关联尝试；Host 校验 receipt 和下载文件。取消发生在计划生成之前时，可能返回 `RESUME_UNSUPPORTED`，必须如实处理，不自动退化成全新安装。已成功任务不可续做。
- `launch` 展示 Host 提供的购买/赞助劝导，输入 `yes` 后才提交。`trial` 明确使用官方试玩模式；也可选择 `accounts` 中的已有账户 ID。微软登录、皮肤管理未在本示例扩写，继续由现有 Host/Avalonia 接口处理。
- `offline <name> <key>` 经 Host 创建离线账户，不写账户文件。离线账户与微软正版身份保持区别。
- `launch-get <operationId>` 查询启动准备结果；`game <gameProcessId>` 查询游戏进程；`stop <gameProcessId>` 请求 Host 停止其持有的游戏。
- `exit`/输入结束：请求正常关闭 Host。独立游戏按原策略继续运行；托管游戏按 Host 关闭策略处理。`leave`：仅关闭客户端连接，Host/后台任务可继续，之后新客户端可在原进程结束后接管会话。
- 遇到错误显示稳定错误码，原始 Host stderr 被 Client 抑制，示例不输出微软令牌或读取钥匙串。

一个数据根同时只由一个客户端进程持有会话。不要同时启动 Avalonia 和本示例连接同一个根，`SESSION_IN_USE` 不应被绕过。

## 接入你自己的 UI

`MinimalLauncher.cs` 展示最小接线，`Program.cs` 是可替换的控制台界面：

```text
你的控件 / 页面
      ↓
MinimalLauncher（展示侧接线）
      ↓
Pcl.Client + Pcl.Contract
      ↓ 当前用户私有管道
独立 Host → Application / Core → 系统与网络
```

UI 只保留 operationId、幂等键、实例/账户 ID 和公开快照。不要从示例中添加 Core 引用来直接调用安装器，也不要解析 receipt 或读写账户/实例注册表。非 .NET 客户端需实现相同协议语义，见 [客户端接入指南](../../docs/客户端接入指南.md)；本示例不宣称已经提供 Swift SDK 或插件框架。

## 契约与真实下载验收

```sh
dotnet build tests/Pcl.MinimalClient.Tests/Pcl.MinimalClient.Tests.csproj -c Release
dotnet tests/Pcl.MinimalClient.Tests/bin/Release/net10.0/Pcl.MinimalClient.Tests.dll \
  /absolute/dotnet /absolute/host/Pcl.Host.dll \
  /absolute/new-contract-root /absolute/contract-result.json contract
```

测试独立进程 Host：程序集边界、握手/错误、幂等冲突、取消终态、订阅重连、Host 重启后快照与键恢复、离线账户持久化、根锁释放。`contract` 不完成游戏安装，立即取消操作；可能短暂请求官方元数据，不需要账号或 API 密钥。

将最后一个参数改为 `live` 并换一个全新目录，将真实下载 Minecraft 1.21.1/Java，收到文件字节后取消，再模拟 Host 崩溃和安全续做，运行两次官方试玩（托管和独立模式）。需网络、磁盘空间及图形会话。它会结束自己创建的测试游戏进程，**只验收图形/材质初始化，不计作世界进入、保存或正常退出验收**。不得使用个人数据根或重要存档。

验收代码同样不引用业务程序集；它为模拟崩溃和收尾持有本轮测试进程句柄，这些操作不属于客户端的业务实现。实际结果见 [最小客户端与 Host 契约验收](../../docs/最小客户端与Host契约验收.md)。
