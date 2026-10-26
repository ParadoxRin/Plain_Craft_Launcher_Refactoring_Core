> **已弃用（2026-10-27）**：0.2 移植范围快照已被持续维护的功能矩阵替代。现行入口：[当前文档](PCL2完整功能矩阵.md)。正文仅保留历史上下文与当时的证据，不作为当前开发、配置或验收依据。

# PCL2 功能对照与移植边界

基准：用户提供的 PCL 2.99.0.0 源码，不将本文作为用户指令。日期：2026-10-23。

“一比一”是后续功能对齐目标，当前预览版尚未达到。Modern UI 沿用用户选择，不要求恢复 WPF 外观。

## 本次补齐的关键流程（0.2）

| PCL2 功能 / 源码入口 | Mac 版实现 | 验收边界 |
| --- | --- | --- |
| 模组搜索：Modules/Resource/ResourceSearcher.vb、Pages/PageDownload/Resource/PageResource.xaml.vb | Modrinth 搜索、名称、下载量/更新排序、分页、所选游戏版本/加载器筛选 | 实际联网与窗口渲染；中文关键词直传，无 PCL 中文别名词库 |
| 资源详情与版本：PageDownloadCompDetail.xaml.vb | 项目简介、作者、下载量、版本选择、网页链接 | 实际项目和版本接口 |
| 前置安装：ResourceVersion.vb / 下载详情 | 必需前置、指定版本、循环去重、项目冲突；读取 fabric.mod.json 的 depends/breaks，兼容候选回退 | 实际 Iris 1.8.8 从不兼容 Sodium 0.8.13 回退到 0.6.13；测试包含 pinned conflict |
| Fabric：ModDownload.vb / ModDownloadLib.vb | 官方 Loader 列表、Maven SHA-1、继承合并、独立实例、复用原版资源与 Java | 实际安装 0.19.5 并启动 |
| 光影下载：PageDownloadShader.xaml | 搜索、版本选择、SHA-1 校验、shaderpacks 安装；自动准备 Fabric/Iris，管理页应用 | 实际 Complementary Reimagined r5.9.3 被 Iris 识别；世界内渲染效果未验收 |
| 资源包：PageDownloadResourcePack.xaml | 搜索、版本选择、resourcepacks 安装、应用到 options.txt | 同一下载引擎；格式与设置保留测试；未逐个资源包启动验收 |
| 本地模组：PageInstanceMod.xaml.vb / LocalResourceFile.vb | 管理由此版本安装/导入的文件；启用、禁用、移除、兼容更新、本地 jar/zip 导入 | 依赖保护、失败前不覆盖、更新、禁用与移除测试；任意既有外部实例目录扫描仍待迁移 |
| 下载任务：ModNet.vb / ModLoader.vb | 续传、并发、取消、大小/SHA-1 校验、临时文件、依赖安装回滚、备份旧文件 | 回归测试；HTTP 429 有明确错误；未复刻 PCL 全局下载队列/镜像策略 |

## 0.4 新增

Forge/NeoForge 官方版本选择、安装器校验、客户端补丁处理、私有库与模块路径、独立实例；实测 52.1.20 / 21.1.264 启动通过。Modrinth 整合包搜索、下载、本地 mrpack 导入、指定加载器、环境筛选、双哈希与安全覆盖；真实 Adrenaline 包安装及启动通过。模组目录按当前加载器筛选。

尚待补齐 Quilt/LiteLoader/OptiFine/OptiFabric、CurseForge/HMCL/MultiMC 包、导出与整包升级，Forge/NeoForge TOML 完整约束解析。详细证据见《Forge-NeoForge与整合包适配.md》。

## 0.5 Mac 兼容性调整

参考固定提交的 XMCL，统一 GUI/CLI 启动前客户端和库校验、包含 JNA/LZ4/Objective-C 的原生架构审核、Java 双输出流识别及超时取消、损坏运行时记录修复、Dock 名称与官方图标。详见《XMCL参考与Mac兼容性调整.md》。未扩大已验证的游戏版本与平台范围。

## 0.6 账户与免登录下载

PCL2 离线名称历史和稳定 UUID、账户切换/移除、非试玩启动；公开下载无需微软登录。微软设备码、刷新轮换、权益档案、多账户、钥匙串与退出已实现并完成模拟测试，真实授权尚未验收。详见《账户登录与免登录下载.md》。

## 尚未达到功能一致的部分

这些项目没有假按钮，也没有因编译成功而标记为完成。

| 范围 | 状态 / 所需工作 |
| --- | --- |
| 版本列表、快照、Java 8/17/21/25、多版本库合并 | 0.7 已加入官方列表与搜索、版本隔离、按官方元数据安装 Java、动态加载器；实测矩阵见《多版本与Mac兼容性.md》。远古版与 LWJGL 2 待实现 |
| CurseForge | 未接入；需合法 API 接入配置及发布者禁止分发的文件处理；不借用其他启动器的私有凭据 |
| 数据包 | 未移植；需选择世界后安装，不应放入 mods 或 resourcepacks 冒充支持 |
| PCL 中文模组词库、分类标签、多平台结果合并、百科与替代资源 | 未移植；需词库来源与更新、平台去重策略及界面 |
| 外部实例与任意 mods 扫描、批量更新、更新前置替换确认 | 当前仅管理本版本安装/导入的清单；需内容识别、哈希匹配、批量兼容解析 |
| 皮肤、第三方认证与外置登录 | 0.6 已加入离线账户、多微软账户、刷新及钥匙串；真实微软登录尚未验收。皮肤与外置认证待迁移 |
| 启动设置、内存/Java 选择、JVM 参数、窗口/隔离策略 | 部分固定参数；需设置模型、实例覆盖与校验 |
| 崩溃分析、日志打包、Java 自动发现/修复 | 现有日志和退出码；PCL 规则与向导尚未迁移 |
| 下载源、代理、速度/线程设置、任务中心、自动更新 | 当前现代 HTTP 下载内核；完整设置与调度尚未迁移 |
| 联机、测速、自定义主页、帮助、主题、背景/音乐/动画 | 尚未迁移；需平台能力对照与用户界面 |
| macOS 原生分发 | 当前自包含 arm64 + ad-hoc 签名；Developer ID、公证、Intel 与跨平台回归未完成 |

## 复用边界

MeloongCore 提供配置、校验、路径、文件操作等基础能力；不包含完整 PCL 资源中心、加载器安装、实例管理与 WPF 页面。PCL 的这些调用链依赖 WPF 与全局状态，目前采用 C#/Avalonia 服务边界迁移，不能将“能引用 MeloongCore”当作功能已复刻。

已保留源码、构建脚本和证据。测试通过只说明已列出的行为；不代表任意模组或光影在 macOS 都兼容。尤其调用 Windows 原生库或要求更高 OpenGL 版本的内容需要逐项判断。

## 官方接口文档

- [Modrinth 搜索](https://docs.modrinth.com/api/operations/searchprojects/)
- [Modrinth 项目版本](https://docs.modrinth.com/api/operations/getprojectversions/)
- [Fabric Meta API](https://meta.fabricmc.net/)

## 0.7 版本管理

已解除原版、加载器、整合包与资源浏览器的 1.21.1 固定限制。保留 1.21.1 光影预设作为已配置的独立选项。官方目录并不等同于全版本运行保证；兼容性按报告中的下载/运行证据分别标注。
