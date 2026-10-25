# 阶段三第二批：加载器恢复、统一启动受理与独立 Host

日期：2026-10-25（Australia/Sydney）。基线：[架构规范 v1.2](PCL-Mac架构规范.md)。前序：[操作模型与原版续做](阶段三操作模型与测试.md)。

本批是实际实现与执行记录。Avalonia 默认继续使用同进程服务；私有 stdio Host、类型化客户端以及正式 CLI 远程入口已实现并实际运行，不宣称所有界面已整体改为远程调用。

## 1. 加载器安全恢复

`ExistingVersionInstaller` 经 `LoaderOperationInstaller` 复用既有 Fabric/Quilt、Forge/NeoForge、OptiFine、LiteLoader 安装代码，不另写一套下载或依赖求解器。

新的可选 `LoaderPin` 与原版父计划一并保存在 receipt 中，包含：具体加载器家族、精确版本、操作专属实例目录名、原始操作所有者、安装器哈希、规范化元数据哈希、恢复阶段及加载器文件清单。原有 FormatVersion=1 文档可继续读取，旧原版 receipt 不需要删除或改写。

恢复步骤：

1. 固定原尝试选择的版本；`auto` 也保存最终家族，不在恢复时偷偷改用最新版。
2. 重新取得可信父元数据并校验原版计划；重新获取加载器元数据／官方安装器哈希，拒绝 `RESUME_INSTALLER_CHANGED` 或 `RESUME_LOADER_METADATA_CHANGED`。
3. 只复用与本次可信计划大小／SHA-1 相符的缓存文件。遗留 `.failed` 目录不是可信执行 checkpoint。
4. Forge/NeoForge、OptiFine 的不可续接转换步骤从新的工作目录重新运行，重新校验输出；不恢复 Java 安装器的内存状态。
5. 新实例在准备完成后发布。已发布实例的恢复，只替换重新生成并校验的安装器管理文件；复制、哈希和 Flush 在可取消阶段，最后逐文件原子替换，`version.json` 与 `profile.json` 最后发布。
6. 不删除或重建现有实例目录，保留 `saves`、`mods`、配置与自定义显示名。身份不匹配、链接路径或越界路径会拒绝覆盖。

目标目录由加载器、版本和原操作 ID 构成，恢复沿用原目标，新意图使用新操作身份。receipt 的目标目录键与既有 GameProfile.Id 由安装结果映射；启动只接受实例注册表返回的 ID，不将文件夹名普遍当成实例 ID。

OptiFine 没有可用上游哈希时，在新的受控缓存路径重新取得作者 HTTPS 原件并比较已固定 SHA-256。LiteLoader 固定 build，并核对规范化元数据与 Maven 文件哈希。两者本轮已接线、编译，**没有完成真实下载安装／启动验收**；不能把四个主要加载器的运行证据扩展给它们。

路径检查覆盖目标、祖先目录及复制源，提交前再次检查；未宣称完成针对恶意并发目录替换的原生目录句柄隔离或真实断电实验。

## 2. `game.launch` 统一受理

`IGameLaunchOperations` 由 `GameLaunchOperationService` 实现，桌面主启动按钮、CLI 离线／微软启动，以及独立 Host 复用同一受理器。

公开请求只含 idempotencyKey、accountId、instanceId、Detached；不含访问令牌、刷新令牌、Java 路径或完整命令行。执行器内部完成认证、正版权益验证、Java、皮肤、语言、启动前命令与启动计划准备。桌面不再在受理之前等待微软令牌刷新。

- 独立的 `launch-operations-v1` 存储划定数据目录 + `game.launch` 的键空间。
- 受理记录落盘后才返回；同键同参数复用原 operationId，不再创建进程；不同参数拒绝。
- CLI 既有 `--java` 仍为本地受信输入，其选项修订哈希进入受理 fingerprint；RPC 没有任意 Java 路径入口。
- 操作 succeeded 只表示进程已创建，进程运行／退出通过 gameProcessId 单独查询和停止。
- 取消等待不取消操作；取消尚未创建进程的启动操作不等于停止已运行游戏。
- 崩溃后不重放启动来“修复”状态。持久化 spawning 非终态恢复为 interrupted / LAUNCH_OUTCOME_UNKNOWN；同键只返回原记录，不能通过旧 PID 接管进程。
- 创建进程后的诊断收据写入失败不会丢掉已创建句柄或诱发重复启动。发生不确定结果时明确返回 LAUNCH_OUTCOME_UNKNOWN，不假称外部副作用绝对恰好一次。

应用层 `Application/Launch/ExistingGameLaunchExecutor` 负责准入与启动准备编排，继续使用原 AccountManager、IAccountSecrets 和 Keychain 作用域；没有更换 client ID、微软权限、登录策略或注销逻辑。微软刷新按账户串行，缓存支持后台准备与只读界面并发读取。真实现有微软账户的刷新、权益验证及启动已经运行通过。

Avalonia 的显示／隐藏／最小化／退出分支保持原有策略。现阶段使用显式本地兼容桥借用 RunningGame 观察对象，公开 Contract 不暴露该对象；不能据此宣称 GUI 已整体跨进程。CLI 外置认证诊断路径保留旧执行门面，未冒充已迁移到新的启动 Contract。

## 3. Host、客户端与 CLI 的实际接线

新增：

- `src/Pcl.Host`：独立可执行私有 stdio Host。
- `src/Pcl.Client`：持有子进程、读循环及诊断循环的类型化客户端。
- CLI `host-install` / `host-launch`：真正通过客户端启动 Host 并调用契约；父 CLI 不同时组装本地可写服务。

使用本项目 SDK 的例子（在项目目录执行；账户先用既有账户命令创建或登录）：

```sh
./.tooling/dotnet/dotnet src/Pcl.Cli/bin/Release/net10.0/Pcl.Cli.dll host-install --data-root /absolute/isolated-root --game-version 1.21.1 --loader fabric --idempotency-key attempt-001
./.tooling/dotnet/dotnet src/Pcl.Cli/bin/Release/net10.0/Pcl.Cli.dll host-launch --data-root /absolute/isolated-root --account account-id --profile instance-id --detached --idempotency-key launch-001
```

可用 `--host-assembly` 指定 Host DLL，`--dotnet` 指定运行时。`host-install --resume-of <operationId>` 发起新续做尝试。CLI 等待取消／连接关闭不等于 operation.cancel；明确取消使用客户端 CancelAsync。

协议是 **PCL 自定义 RPC v1，UTF-8 NDJSON**，不声明 JSON-RPC 2.0 兼容。握手返回 Host 实例 ID、安装目标、协议版本及能力。请求 ID 在会话内唯一；客户端用独立等待者匹配响应，忽略迟到的已失效响应。当前查询与请求响应可用，尚无通知事件流／重放订阅，未广告这些能力。

stdin 按字节限帧 1 MiB；超限行丢弃到换行，再继续同步下一帧。stdout 仅写协议，库的 Console stdout 被隔离。当前服务端顺序派发请求，因此响应不会交错；客户端有单写锁和独立读循环。没有 socket、任意执行、任意 Java 参数或任意文件读取入口。

## 4. 本批落地的 DEC-003 / DEC-004 / DEC-005

### 锁与退出

Host 和桌面取得 `.launcher.lock` 后才取得操作存储 lease。两个 worker 共用执行 lane：安装与启动准备互斥，游戏已运行时拒绝新的安装执行。旧 CLI 的直接修改命令现在也遵守同一个根 lease，不绕过 Host 写入权。锁文件不删除，不依据 TTL/PID 抢锁。

显式 host.shutdown 取消并排空未完成操作；有托管游戏存活时拒绝直接退出。stdin EOF 是连接消失，排空已受理任务而不隐式取消；托管游戏存活时 Host 继续观察至退出。独立游戏使用原文件重定向策略，Host 退出不杀游戏。资源句柄在断线时立即清空。

旧 Avalonia 的其他修改入口仍按已有界面协调逐步迁移；不宣称所有核心类已获得统一事务锁。未做终端会话信号隔离或 Windows/Linux 锁行为的实测。

### 结构化诊断

operation.log 提供**快照**能力，记录受控状态／阶段码，而非任意正文。级别范围 debug/info/warning/error，目前操作事件使用 info/error；按操作 Sequence 排序，重复阶段合并。每个操作最多 128 条，文件上限 64 KiB，截断在保留记录中标记 Truncated。

正文没有 URL、认证头、凭据、Java 参数或宿主路径字段；不合格式的阶段码替换为 stage-redacted。原始异常不进入响应。诊断写失败不会使已完成安装／启动丢终态。receipt 不作为日志容器；游戏原始日志仍按既有本地策略保存，不通过此资源接口公开。

### 受控资源

使用 `blob.open/read/page/close`，避开原有 resource.search/install 语义。只开放 operations 与 operation-log 两种 Host 选定来源，返回不透明句柄、大小、MIME、SHA-256 和条目数。

资源是冻结快照：read 每块最多 256 KiB、Base64 编码；page 每页最多 50 条，越界明确拒绝，EOF 明确返回。单资源最多 16 MiB，最多 16 个，合计最多 32 MiB；五分钟后拒读，后续请求清理，断线整体回收。没有任意宿主路径参数。所有响应仍受 1 MiB 帧约束，过大响应保留请求 ID 并返回明确错误。

## 5. 本轮执行证据

产物目录：`artifacts/architecture-phase-three-next/`。全部使用隔离缓存副本；微软测试借用原钥匙串作用域进行正常刷新，不注销用户账户。包含账户索引与游戏原始日志的私有测试目录不作为公开导出包。

| 验证 | 实际范围 | 证据 |
|---|---|---|
| Fabric / Quilt / Forge / NeoForge | 运行阶段取消、固定精确版本恢复、检查库完整性、统一启动受理、同键去重、真实游戏图形资源初始化 | `loaders-final.json`（汇合原始运行记录） |
| Forge 提交后恢复 | 从提交后中断证据重跑生成阶段，更新安装器文件，保留隔离 saves 测试标记 | `commit-recovery-live.json` |
| 加载器输入篡改 | 改写 Forge 安装器哈希、Fabric 元数据哈希，被真实恢复执行链拒绝 | `loader-tamper-live.json` |
| 7 项新增定向测试 | 幂等／冲突／重启、等待取消、spawning 不确定记录、字节限帧、日志截断、保留用户文件、链接与身份拒绝 | `final-validated-tests/results.json` |
| 12 项前序操作测试、11 项关闭责任测试 | 本次相关接口与组合根接线，不是全量功能回归 | `installation-tests/results.json`、`lifetime-tests/results.json` |
| 协议与 EOF | 握手、版本拒绝、请求 ID、第二 Host/CLI 根锁冲突、超长帧恢复、禁任意执行/读取；连接消失后真实原版安装继续完成 | `protocol-tests.json` |
| 独立 Host 真实启动 | 类型化客户端创建独立游戏，Host 退出后继续图形初始化，重开 Host 去重且不按 PID 接管，资源分页／分块／边界／关闭 | `host-live.json` |
| 已有微软账户 | 相同钥匙串作用域，刷新、正版权益验证、通过新启动服务创建 1.21.1 并初始化图形 | `microsoft-live.json` |
| 桌面正常/独立模式 | 正式启动按钮、窗口隐藏与恢复、共享作用域保留；应用退出后独立游戏继续运行 | `gui-managed.json`、`gui-detached-verification.json` |
| CLI 主入口 | 离线实际启动，重用同键返回旧记录、不创建或接管第二进程 | `cli-launch.log`、`cli-replay.log` |
| CLI 远程入口 | 正式 host-install / host-launch 调用独立 Host | `remote-cli-install.log`、`remote-cli-launch.log` |

Quilt 初轮取消请求晚于实际完成，未当作取消通过；修正测试时序后，已取得单独的真实取消／恢复证据。早期编译／测试失败日志保留，最终成功数量以对应报告为准。

spawning 崩溃窗口使用持久记录故障注入，不能称为真实游戏进程创建瞬间的断电实验。提交后 saves 证据是隔离文件标记保留，不是本轮进入世界、保存和重进验收。所有游戏启动证据止于图形／材质初始化及测试进程清理；没有本轮人工视觉验收、第二微软账户、首次浏览器授权或 Windows/Linux 实测。

## 6. 编译与剩余范围

Desktop、Host、Client、CLI 均有必要的 Debug/Release 编译记录。保留已知 MainWindow 注入构造引起的 AVLN3001；真实窗口已运行，不添加自行组装服务的无参构造来掩盖警告。

本批不是整个阶段三结束：Avalonia 全面远程化、更多页面/账户 Contract、通知背压与快照订阅衔接、外置认证启动迁移仍待后续。OptiFine/LiteLoader 的真实恢复验收单列，不能靠代码存在认定通过。既有 0.23 发布包未冒充本轮实现。
