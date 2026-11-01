# PCLCS 在本项目中的边界

本目录保留上游 PCLCS 的兼容实现与资源布局，许可范围见仓库根目录 LICENSE.md、PCL-LICENCE.txt 和第三方声明。新增集成文档不改变这些来源与许可。

- `Resource/WikiEntries.txt` 是随基线分发的静态百科索引，不是实时爬虫结果。
- `Resource/WikiEntry.cs` 负责解析该格式；消费者不能改变 `WikiEntry.All` 中的可变对象。
- 一个百科 ID 可能包含多条项目/移植关系，不代表条目内所有下载项目身份相同。
- 统一搜索网络请求、分页、下载版本选择、API 密钥和 Avalonia 控件不进入 PCLCS。

当前集成按职责分开：

| 模块 | 职责 |
|---|---|
| Core/Adapters/Search/BundledProjectIdentity | 读取 PCLCS 并投影为纯规则输入 |
| Domain/Search/ProjectIdentityIndex | 一对一映射、冲突拒绝；不引用 PCLCS、不访问网络 |
| Core/Adapters/Search/UnifiedResourceSearch | 来源调度、原查询/别名扩展、兼容候选检查、聚合 |
| Core/Adapters/Search/UnifiedSearchSession | 有界会话分页、重试及跨页去重 |
| Core/Adapters/Search/UnifiedReleaseResolver | 目标实例筛选、逐来源版本读取、失败提示 |
| Core/Adapters/Pages/WorkspaceSearch | Host 接线、私有选择快照、下载/安装事务 |
| Contract/Workspaces/SearchViews | 不含下载执行器或凭据的公开展示数据 |
| Desktop/UnifiedSearchWindow | 输入、选择与显示，不复制身份/兼容规则 |

原 Pcl.Core 类型命名空间保留以兼容既有调用；目录分层不意味着新增循环项目引用。当前不重写原 PCLCS 解析器，也不引入在线词库同步服务。后续扩展用户词库/映射时，应从适配器提供显式、可追溯的数据，而不是修改 vendor 全局列表。
