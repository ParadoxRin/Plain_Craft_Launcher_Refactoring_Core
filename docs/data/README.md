# 功能追踪数据

这些文件继续纳入 Git，搬目录不会减少仓库体积。普通编译不需要重新生成；不要删除它们后从空状态推导验收结果。

| 文件 | 类型与用途 | 为什么保留 |
|---|---|---|
| [source-ui-inventory.json](source-ui-inventory.json) | 从固定 PCL 2.99.0.0 参考树提取的页面、设置和过程符号快照 | 上游参考树不随当前源码包交付，保留可审查的来源定位 |
| [feature-status-overrides.json](feature-status-overrides.json) | 人工评审与版本核对写入的状态、范围、证据覆盖输入 | **不是可丢弃的生成缓存**；不能单凭源码存在或测试数量重建 |
| [PCL2完整功能矩阵.json](PCL2完整功能矩阵.json) | 基础清单、覆盖输入与历史核对合成的机器可读矩阵 | Desktop 以 `feature-matrix.json` 嵌入，供应用内查询；同时保留审核记录 |

当前三文件合计约 1.46 MiB。功能阅读入口是[矩阵摘要](../PCL2完整功能矩阵.md)、[剩余清单](../剩余清单.md)与[支持范围](../支持范围.md)。本次移动保留三个 JSON 的原始字节，不修改验收状态。

## 更新与生成

1. 先取得对应实现、测试范围与来源证据，再审核修改 `feature-status-overrides.json`；不靠修改状态关闭缺口。
2. `scripts/build-feature-matrix.py` 仍需外部固定参考树 `../pcl-download-fix/PCL-2.99.0.0/Plain Craft Launcher 2`（相对仓库根）。这是历史生成器的输入约定，不是构建前置条件。缺少参考树时脚本拒绝写出，不产生空 inventory。
3. 如需重建，在有正确参考输入的工作副本运行 `python3 scripts/build-feature-matrix.py`，逐项审查三个 JSON 与 Markdown 的差异、来源身份、状态和证据路径。生成器包含历史来源声明，不重新查询上游，也不提供新的验收结论。
4. `update-matrix-*`、`reconcile-matrix-*`、`finalize-*`、`audit-matrix-0.40.py` 是特定版本的数据维护脚本，可能回写历史口径或需要已不随仓库提供的运行产物。它们不是接手者应顺次重跑的初始化步骤。

历史证据里的旧 `docs/*.json` 路径按同名文件迁移到 `docs/data/`；原始回执与哈希保持原样，不为改目录重写历史证明。
