# 最小客户端与 Host 契约验收

本次在 `main` 增加可独立运行的命令行示例，未修改固定 v0.41.0 安装包或功能矩阵。目标是让接手开发者直接复用 Host，而非依赖旧 CLI 的 Core/Application 直接调用。

## 交付边界

- [最小客户端](../examples/Pcl.MinimalClient/README.md)：交互命令、安装/取消/观察/重连/重启/续做、账户公开快照、离线账户创建、购买/赞助提示及游戏启动/停止。
- 示例只引用 Client + Contract；独立发布目录只包含这两个库和示例本身。Host 单独部署并持有 Core/业务库。
- [验收程序](../tests/Pcl.MinimalClient.Tests/Program.cs)同样不引用 Core/Application/Domain/Ports，不读写内部 receipt；通过真实独立 Host 的公共契约操作。
- CI 增加三系统 Host 契约测试。完整真实下载、Java 和图形游戏验收只在本机 Mac 执行，不冒充三平台游戏可用。

## 已执行的本地测试

首轮 `contract` 通过：程序集依赖、错误码、幂等、取消、订阅重连、Host 重启后查询/去重、离线账户公开快照及根锁重新获取。

首轮 `live` 已通过真实官方 Minecraft 1.21.1 下载链路：

1. 下载产生字节后取消，事件订阅跨同 Host 重连收到 cancelled 终态。
2. 正常关闭并新建 Host，取消记录和幂等键仍存在；新键创建关联续做。
3. 续做已产生下载字节后真正终止本轮 Host；新 Host 将该操作恢复为 interrupted/HOST_INTERRUPTED，原键只返回历史记录，不自动执行。
4. 以新键从 interrupted receipt 安全续做，官方文件校验、Java 准备及安装成功；两个父操作的历史状态保留。
5. 未确认劝导的启动被拒绝。确认后以官方试玩模式分别启动托管和独立 Minecraft，日志确认真实材质 atlas 初始化；重复启动键不生成第二个游戏。
6. 托管游戏在重连附件中恢复并经 Host 停止；独立游戏在 Host 正常退出后仍存活。测试仅结束本轮游戏，不计作世界/保存或正常游戏退出验收。

独立部署也已实测：复制发布目录到仓库外，通过交互命令创建离线账户、重连、重启、恢复账户、正常退出，退出码0。首次使用 macOS `/var` 临时目录别名连接失败；物理路径重测通过。源码确认 Host 存储拒绝符号链接父目录，保留此保护；示例提示检查物理路径，说明书使用 `pwd -P`，不以放宽存储保护让测试通过。

## 最终来源与结果（2026-10-28）

代码来源：`953d7b975c5ba9bdc0543f4b6db016354c89f29b`。后续为交接/证据文档增补，未改变被测可执行源码。三平台 [CI 40650165276](https://github.com/ParadoxRin/Plain_Craft_Launcher_Refactoring_Core/actions/runs/40650165276) 全部成功。逐项检查名、原报告 SHA-256、下载中断观测及范围保存在[脱敏结果](最小客户端与Host契约验收结果.json)，可在临时 Actions 附件到期后继续核对。

| 环境 | 核心套件 | 本次独立客户端 / Host | 范围 |
|---|---:|---:|---|
| macOS 云端 | 235/235 | 28项通过 | 真实 Host 契约 |
| Windows 云端 | 235/235 | 28项通过 | 真实 Host 契约 |
| Ubuntu 云端 | 235/235 | 29项通过 | 额外验证 Linux 缺少出生证明被拒绝 |
| 本机 Apple Silicon Mac | 本轮由三平台 CI 覆盖 | contract 28项；live 46项通过 | 实际下载、崩溃续做、两次试玩图形初始化及独立游戏存活 |
| 仓库外发布目录 | 不加载核心套件 | 6项通过 | 交互式账户、重连、重启及退出；仅3个业务 DLL |

live 第一次取消前已收到220,687字节，崩溃前续做已收到1,017,515字节；两次计划均为3,952文件。不是将未开始下载的排队取消当作文件安全续做。成功路径确认本轮所有 Host 退出，两个游戏仅通过持有的测试进程句柄结束。

最终示例/测试构建为0警告、0错误。现有 Desktop/Host 兼容构建通过，0错误，保留15条警告（12条 NuGet 漏洞信息源不可达及3条已有 Desktop 警告）；没有宣称清除了旧警告或完成在线漏洞审计。现有 Desktop 的编译符号守卫检查3,086个方法、50,679次符号使用，0违规。

## 实测发现并修复的 Linux 问题

首次 [CI 40648970580](https://github.com/ParadoxRin/Plain_Craft_Launcher_Refactoring_Core/actions/runs/40648970580) 的 Mac/Windows 已通过，Ubuntu 在 attach 返回 `CLIENT_IDENTITY_INVALID`。这是实现问题，不是需要跳过的 Linux 预期。

.NET 的 Linux `Process.StartTime` 基于每进程缓存的 boot UTC；底层分别读取 BOOTTIME 与 REALTIME_COARSE 再求差，因此不同进程得到的 UTC 估计不能作为逐位一致的出生证明。依据：[.NET Process.Linux](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.Linux.cs)、[pal_time.c](https://github.com/dotnet/runtime/blob/v10.0.0/src/native/libs/System.Native/pal_time.c)。

Client/Host 现在精确比较 [Linux stat 第22字段](https://man7.org/linux/man-pages/man5/proc_pid_stat.5.html)的原始启动 tick。`ClientIdentity.linuxStartTicks` 在 Linux 必需；`startedUtcTicks` 没有改含义，Mac/Windows 仍精确比较原生 UTC 启动时间。保留 OS 当前用户管道保护、活跃会话拒绝接管与进程退出观察。没有采用时间容差、PID-only 或跳过测试；不能把此会话防误接管机制描述为同用户恶意程序之间的安全隔离。

新增真实管道负例：错误出生证明拒绝、Linux 缺少出生证明拒绝、活跃会话抢占拒绝，拒绝后原客户端仍能恢复同一 Host。第一轮失败保留，三平台复测全部通过。Linux 自定义客户端需配套升级 attach 字段，详见[接入指南](客户端接入指南.md)。

## 保持与未覆盖

本次除 Linux 管道会话出生证明修复及候选进程句柄异常释放外，不修改登录、皮肤、OAuth、钥匙串、Host 组合根或游戏托管/独立运行策略。没有真人微软登录、皮肤外观、世界保存重进、Windows/Linux 凭据存储或图形游戏验收。使用全新隔离根，没有读取个人账户和世界。

`contract` 立即取消安装，不进行完整游戏安装；它可能短暂发起官方元数据请求。其结果只证明当前公共协议与被测生命周期，不证明所有 RPC、未知字段、恶意帧、慢消费者或全部加载器组合已覆盖。原核心235项套件与已有专门协议测试保持各自范围。

没有提供稳定 Swift SDK、跨语言 schema 生成或任意插件系统；既有 CLI 直接 Core 调用仍为历史技术债。下一步可以在此最小客户端之上接入 UI，或完善 Windows/Linux 平台组合根和端到端游戏链路。不要将示例完成自动写成35项产品缺口全部关闭。
