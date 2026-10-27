# Windows CI 六项失败：修复与三平台验收

日期：2026-10-27（Australia/Sydney）。这是 `main` 的维护改动，未重打包，也未修改 `v0.41.0` 标签或 Release 附件。前一轮失败记录继续保留于 [CI与文档一致性核对](CI与文档一致性核对.md) 和 [首次三平台结果](CI首次三平台结果.json)。

## 六项逐项归因

六项中 **2 项是实现缺陷，4 项是测试使用了错误的平台预期**。没有删除用例、提前返回、增加跳过条件、放宽保护断言或修改 CI 的失败策略。

| 原失败用例 | 归因 | 修复及保留的断言 |
|---|---|---|
| `settings_appearance_persistence_and_recovery` | 平台预期：Windows 按许可将标题模式规范化为 default，原测试无条件期待 text | 保存与导入后都断言：Windows 蓝色 `#0875D1`、static、default、空标题图；其他宿主保留选定颜色、rainbow 和 text。背景 fit、非法值拒绝等原断言保留。未修改外观实现或许可限制。 |
| `unix_absolute_path_and_root` | 平台预期：Unix 根目录、反斜线字面量及比较语义被无条件用于 Windows | 改名为 `host_paths_roots_and_platform_separators`；保留 Unix 断言，增加 Windows 盘符根、长路径前缀往返、分隔符及大小写比较断言。两边均检查路径原大小写、根目录父子关系及相似前缀不能冒充父目录。这里只验证 PathUtils 的词法契约，不推断实际卷的大小写能力。 |
| `directory_move_and_symlink_unlink` | **实现缺陷**：移动已成功；删除所选目录链接时，仅 Unix 提前 unlink，Windows 枚举并删除了真实目标的内容 | 所选链接在 Windows 也只执行非递归删除；回收站失败后的永久删除同样在枚举前识别链接；链接父目录的递归删除拒绝扩展到 Windows。原目标 keep.txt 必须保留，增加源目录已消失的断言。 |
| `multi_version_metadata_java_and_arm_native_adapters` | 平台预期：用正斜线匹配 Windows 宿主生成的本地库路径 | 用 `Path.Combine` 表达本地路径片段。测试目标仍明确为 Mac ARM64，继续断言 JNA 版本、旧 ObjC 桥排除、Mac natives 选择及 Windows/Linux natives 排除，未用宿主目标替换 Mac 目标。 |
| `temporary_cache_excludes_active_recent_shared_and_symlink_files` | 平台预期：相对路径 DTO 由 `Path.GetRelativePath` 产生，Windows 使用反斜线 | 精确期待 `Path.Combine("cache", "a.jar.part")`；候选必须恰好为 1，活动下载、近期文件、已验证文件、共享库/资源/运行时及符号链接仍不得入选。未改变 DTO 或回收路径格式。 |
| `temporary_cache_rechecks_preview_and_restores_without_overwrite` | **实现缺陷**：回收和恢复均在持有 `FileShare.None` 句柄时重命名文件，Windows 拒绝该重命名 | Windows 句柄只共享 Delete，使重命名可执行，仍不共享读写；校验句柄保持到移动完成。Unix 继续使用原来的 None。过期预览、活动下载、24 小时条件、receipt、目标存在时拒绝覆盖及精确字节恢复均保留。 |

实现入口：[DirectoryUtils](../vendor/MeloongCore/Shared/Utils/DirectoryUtils.cs)、[TemporaryDownloadCache](../src/Pcl.Core/TemporaryDownloadCache.cs)。测试入口：[Program](../tests/Pcl.Tests/Program.cs)、[AccountNetworkDiagnosticsTests](../tests/Pcl.Tests/AccountNetworkDiagnosticsTests.cs)。

Windows 共享模式的依据是 [FileShare.Delete 的定义](https://learn.microsoft.com/en-us/dotnet/api/system.io.fileshare?view=net-10.0) 及 [Windows 重命名需要 DELETE 共享的说明](https://devblogs.microsoft.com/oldnewthing/20211022-00/?p=105822)。允许 Delete 不等于共享 ReadWrite，也不承诺防御任意外部进程恶意替换路径。继续保留应用回收锁、路径校验和先 receipt 后移动的顺序。

## 新增的四项保护测试

这些测试在三个 runner 上均执行真实隔离文件操作，没有按操作系统跳过。

1. `directory_delete_rejects_symlink_parent`：通过链接父目录递归删除必须抛 IOException；链接和真实子目录文件均保留，之后直接移除链接也不能伤及目标。
2. `directory_delete_preserves_nested_symlink_target`：删除包含嵌套链接的普通目录，目录自身消失、外部目标内容完整保留。
3. `temporary_cache_occupied_move_and_restore_retain_bytes_and_receipt`：分别使用独占写入者和允许 ReadWrite/Delete 共享的写入者；回收必须拒绝，恢复必须保留 receipt 和原字节；释放句柄后同一文件成功移动、恢复并清理 receipt。共享写入者用例可发现错误地开放读写共享的问题。
4. `temporary_cache_rechecks_download_queued_after_preview`：预览后才加入队列的下载仍不能被回收，源文件保持不变，不创建有效回收批次。

## 执行记录

- 基线：`c29c231965ecb226db14bf991e17331dc30ce03c`，Windows 225/231，六项失败。
- 实现与初始保护断言：`a181da2e4458c82f5a51699e34fb43899b47410b`，[第一次三平台运行](https://github.com/ParadoxRin/Plain_Craft_Launcher_Refactoring_Core/actions/runs/40444813881)通过。
- 最终测试提交：`497cdd16959fa680f752666eb53e26d67e426734`，进一步加入允许共享的既有写入者。最终结果以 [三平台运行](https://github.com/ParadoxRin/Plain_Craft_Launcher_Refactoring_Core/actions/runs/40444961793) 和下方结果表为准。

| 最终 runner | 构建 | 套件报告 | 原六项及新增四项 |
|---|---|---|---|
| macos-latest | 通过 | 235/235 | 10/10 通过 |
| windows-latest | 通过 | 235/235 | 10/10 通过 |
| ubuntu-latest | 通过 | 235/235 | 10/10 通过 |

已下载三个 runner 的实际结果，核对名称唯一、235 行全部 passed，重点十项逐项通过。作业链接、运行提交、平台数据、逐项结果及原始报告 SHA-256 见 [机器可读验收记录](CI-Windows修复三平台结果.json)。Actions 原始 JSON 附件保留14天；本地原始报告位于未入库的 `artifacts/ci-windows-fix-cloud/`，本地 Mac 报告为 `artifacts/ci-windows-fix-final-local/test-results.json`。

本地 Apple Silicon Mac 使用固定 SDK 编译核心测试并实际执行控制台 DLL，最终 **235/235**；Desktop（含 Host 构建依赖）编译成功，0 错误。保留已有 NU1900 漏洞数据网络警告、Bitmap.Save 弃用警告和 Avalonia 构造器资源警告，不把它们描述为零警告构建。

复现命令（输出目录必须全新）：

```sh
dotnet build tests/Pcl.Tests/Pcl.Tests.csproj -c Release -m:1 -nr:false -p:UseSharedCompilation=false
dotnet tests/Pcl.Tests/bin/Release/net10.0/Pcl.Tests.dll artifacts/ci-core-new
```

CI 仍使用原三系统 matrix，编译后直接执行控制台 DLL；未改用不会执行本套件的 `dotnet test`，也没有添加 continue-on-error。原来 231 行加四个测试变为 235 行；既有其他用例仍有历史平台条件，因此套件报告数量不是每个平台全部原生能力的验收数量。

## 保持的行为与剩余工作

本轮没有修改 IAccountSecrets、钥匙串身份、OAuth 配置、账户/皮肤入口、Host 关闭、进程托管或游戏独立运行逻辑。Mac 的外观选择、Unix 路径及缓存 None 共享模式保持；本地和云端 Mac 套件均用于检出这些行为的回归。没有重新执行真人微软登录、皮肤图形或游戏进入世界/保存验收，也没有验证 Windows 回收站交互或任意链接类型/竞态。

三平台核心 CI 通过，只解除这六项失败和已覆盖的保护条件。Windows/Linux 平台组合根、凭据库、真实进程脱离、Java/natives、客户端与游戏实机验收仍待实施，见[技术债](architecture/技术债.md)。功能矩阵的 131 项、68/35/20/8 口径与35项产品缺口不因此改变；没有新增子进度或靠状态关闭缺口。接手开发者应从上述平台边界选定下一条端到端用例，而不是重复修复已通过的六项。
