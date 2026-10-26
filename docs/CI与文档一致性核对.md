# 0.41.0 发布后的文档核对与三平台 CI

日期：2026-10-27（Australia/Sydney）。这是 `main` 后续维护记录，不覆盖 `v0.41.0` 标签、安装包或原发布验收结果；没有重做真人登录、皮肤外观或游戏世界验收。

## 文档修正

| 原问题 | 本次校正 | 源码或依据 |
|---|---|---|
| 同时要求“三平台原生 UI”与“允许任意 UI” | 共享核心和可替换客户端为现行目标，SwiftUI 保留参考方向，DEC-008 修订 DEC-007；不要求先完成三套 UI | 用户现行目标；规范 §1/§14/§17 |
| “尚未验收”容易被理解为已有 SwiftUI/Windows/Linux 实现 | 明写新客户端尚未实现、平台 Adapter/组合根仍未补齐 | 当前 src 项目、HostRuntime 的 KeychainAccountSecrets 绑定 |
| Shell/Client“仅引用 Contract”及目标图被当成实际 csproj | Desktop 引用 Client+Contract；记录 Core 对 Contract 的兼容依赖及 CLI 直接 Core/Application 的遗留事实 | 各项目 ProjectReference |
| stdio 被写成默认传输、只要求先 hello | Desktop 私有管道先 attach 再 hello，stdio 是直接进程/测试入口；补充 ordered IDs | HostClient、PrivatePipeServer、SessionOwner、RequestIdentityTracker |
| 安装参数扁平、事件与错误字段凭设计示例推导 | 按 InstallCommand 嵌套 installation；事件为 event.changed/HostEvent；当前错误只承诺 code | Contract DTO 与 Host/Program.cs |
| 所有用例被描述为统一操作状态/重放、所有秘密“从不经过 UI” | 区分账户登录、外观任务和安装状态；第三方秘密仅经专用私有输入临时入 Host，微软令牌不返回 Shell | HostAccountService、WorkspaceExecutionAdapter |
| 功能矩阵被误当完整三平台计划、历史步骤像新任务 | 矩阵口径与平台/SDK技术债分开；阶段一至三明确为历史 | 剩余清单、技术债、规范 §13–15 |
| main、发布包及旧证据混用 | main 后续改动与固定 Release 分开；待办和历史缺失证据不被本轮自动关闭 | README、交接 Prompt、发布记录 |

现行规范修订为 **v1.5.1**，README、架构设计、客户端指南、开发验证、技术债和交接 Prompt 同步。本轮未修改功能矩阵状态或新增“子进度”。

## 许可与数据布局

根目录新增标准 MIT `LICENSE`，与 `LICENSE-MIT.txt` 字节相同，保留后者供现有打包引用；GitHub API 本轮已识别为 MIT。适用范围继续由 [LICENSE.md](../LICENSE.md) 明确，PCL 衍生与第三方许可没有变更。GitHub 的 [许可识别说明](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository)指出多个许可或复杂内容也可能影响识别，不能把原 NOASSERTION 单纯归因于没有无后缀文件。

三个大型 JSON 移入 [docs/data](data/README.md)，原始字节保留。状态覆盖输入不是可删除缓存，功能矩阵仍以同一 `feature-matrix.json` 资源名嵌入应用。普通构建不重生成；历史生成器无上游参考树时拒绝写入。路径调整同步到相关生成/归档脚本。

## 首轮实际 CI

工作流：[Core CI](../.github/workflows/core-ci.yml)，固定官方 Action 提交、`contents: read`，不使用用户账号/密钥；PR 不使用 pull_request_target。三个系统独立执行、fail-fast=false、无 continue-on-error。测试程序是控制台应用，实际执行 DLL，而不是用 `dotnet test` 退出0冒充运行。仅上传结果 JSON，保留14天，不上传隔离运行目录。

1. 第一次上传被 GitHub 拒绝，用户补充 workflow 授权后成功上传。
2. 第一个 workflow 因 job env 中使用不可用 runner 上下文未启动；已修正，不记为测试执行。
3. [首轮三系统作业](https://github.com/ParadoxRin/Plain_Craft_Launcher_Refactoring_Core/actions/runs/40375180213)中 Mac 通过，Linux 构建发现资源 Include 大小写与实际文件不符，Windows 出现6个失败。
4. 修正 `vendor/PCLCS/PCLCS.csproj` 的 Include 为 `OptifineHashes.json`，保留原 `LogicalName`。这是大小写敏感文件系统上的构建修复，不更改上游资源内容或许可。
5. [修复后运行](https://github.com/ParadoxRin/Plain_Craft_Launcher_Refactoring_Core/actions/runs/40375648827)，提交 `4dbf74f`：

| runner | 构建 | 套件报告 | 结论 |
|---|---|---|---|
| macos-latest | 通过 | 231/231 | 核心套件报告通过 |
| ubuntu-latest | 通过 | 231/231 | 资源大小写阻断已解除，核心套件报告通过 |
| windows-latest | 通过 | 225/231，6失败 | **未通过**，失败仍为 CI 门禁 |

机器可读汇总与原始报告哈希见 [CI首次三平台结果.json](CI首次三平台结果.json)。既有测试中存在平台条件提前返回，因此231是套件报告行数，不等于每系统执行了231个原生场景。没有检查或声称云端完整 Host/UI、Keychain、游戏图形、世界保存或互联网联机可用。

### Windows 待办：不隐去或泛化为六个产品缺陷

| 失败用例 | 已定位的检查范围 | 后续验证 |
|---|---|---|
| settings_appearance_persistence_and_recovery | 导入导出后的 BackgroundFit/TitleMode 断言 | 核对 Windows 许可约束下的规范化预期；不为绿灯放松蓝色/无解锁限制 |
| unix_absolute_path_and_root | 无条件使用 Unix 路径及大小写语义断言 | 按宿主卷能力写明确的 Windows 用例；不能把 macOS/Unix 字符串预期当跨平台规格 |
| directory_move_and_symlink_unlink | 移动目录后目标 keep.txt 不存在 | 核查 Windows 路径/移动实现和文件系统行为，保留对源内容和链接目标的保护断言 |
| multi_version_metadata_java_and_arm_native_adapters | Windows 上对显式 Mac 目标生成库路径时，以正斜线字符串匹配 JNA 路径 | 区分宿主路径分隔符与目标平台 natives 选择；不能跳过目标匹配保护 |
| temporary_cache_excludes_active_recent_shared_and_symlink_files | 候选清单不符合“只有旧闲置临时文件”预期 | 核查路径比较/枚举/活动任务/符号链接保护，使用真实 Windows 隔离目录 |
| temporary_cache_rechecks_preview_and_restores_without_overwrite | 可恢复移动断言失败 | 核查回收、恢复与拒绝覆盖，不删除失败用例 |

上述为源码定位和实际失败，尚未完成根因修复；部分可能是平台专用测试预期，部分可能是实现问题。没有把这些失败改为跳过，也不从 Mac/Linux 通过推导 Windows 通过。先解决 Windows 套件与对应路径语义，再扩展平台 Host/凭据/Java/native/游戏验收，是具体可接手的下一步；当前35项产品缺口保持原状态。

## 本地验证范围

移动嵌入资源后实际编译 Desktop/Host，架构守卫检查3497个方法、57426个符号使用，0违规；保留已有警告。初次沙箱还原有 NuGet 漏洞信息网络警告且编译服务未完成，结束该进程后关闭共享编译重新构建成功。没有把这个中断构建写成通过，也没有把还原造成的锁文件波动提交。

本轮只改文档、CI、追踪数据路径及 Linux 构建资源大小写；不更改登录、皮肤、游戏独立运行或安装事务策略。最终包重打包不在本次范围内。

另行核对：97个现行文档相对链接存在；三个迁移 JSON 与迁移前 Git 字节一致，131项/68+35+20+8口径未变；实际编译 DLL 中仍包含完整原矩阵字节。修改的 Python 脚本语法通过，缺参考树的独立副本实际拒绝生成且未写出空数据；LICENSE 与兼容副本相同。以上是文档、构建与布局验证，不是新的游戏验收。
