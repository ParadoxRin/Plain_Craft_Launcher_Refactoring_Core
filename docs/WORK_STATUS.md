# 0.21 实用设置与辅助窗口

真实设置绑定、14 个辅助窗口统一外壳、原版 121 控件逐项状态见《0.21实现与验收.md》《0.21设置逐项核对.md》。核心 219 项通过；游戏 1.21.1/Java21 G1 与 26.3/Java25 ZGC 已观测渲染初始化。后者由 smoke 主动停止，未做本轮人工世界验收。最终压缩包回执以 artifacts/release-verification-0.21.json 为准。不得将原版未完成项或替代入口写成完整复刻。

# 0.20 Windows 界面对照版

本版按 Windows PCL2 2.99.0.0 的导航、侧栏、下载列表与独立详情重排界面，保留 0.19 成品。设置页原版控件目前为明确标注的待绑定对照占位，已有功能通过工作台访问；不能认定为完整或像素一致移植。功能矩阵仍为 132 项，不因 UI 占位改变完成状态。详见《0.20界面对照版.md》。

215 项核心回归通过；最终打包应用的窗口实测结果见 artifacts/verification-0.20/final-live-2/evidence/windows-layout-ui.json。本轮不重新验收游戏进入世界。0.19 的离线登录与此前微软审核/登录成果继续保留，以下旧版本记录中的禁止离线启动、等待审核描述仅为当时状态。

# 0.14 工作状态与继续入口

本轮代码、官方算法对照、真实资源及界面安装测试见《0.14实现与验收.md》，最终包状态以 artifacts/release-verification-0.14.json 为准。Quilt 插件/覆盖/JSON5、官方求解器完全等价、JarJar module-info 及全部TOML、自定义入口和客户端仍待补齐；不要把无账号安装结果写成游戏启动成功。未经权益验证启动继续禁止。

# 0.13 无账号功能续建

已补齐 RES-003 跨页身份去重、查询快照、缓存翻页、失败来源重试及文字变动即时重置；INST-006 增加 schema 3 逐文件 SHA-256、离线校验入口、临时恢复目录与原子设置提交。真实 Modrinth 资源下载、界面操作和新实例恢复已通过；不含游戏启动或世界验收。详情见《0.13实现与验收.md》，最终包回执见 artifacts/release-verification-0.13.json。

Quilt 官方规范与实现已核对，完整约束求解、Forge JarJar、自定义包组件及跨设备离线恢复仍未完成。微软审核仍按 0.12 提交记录等待，本轮未重新查询审核。不要恢复未验证启动。原始归档、旧版包、用户 runtime 均保留。

# 0.12 微软登录接入候选（未完成审核）

已在用户目录注册 PCL_Mac，内置自己的公开 client ID，启用公共客户端流程。真人 OAuth/Xbox/XSTS 通过，Minecraft login_with_xbox 返回403；审核申请已提交并取得网页回执，尚未取得批准。用户已确认取消未验证启动与离线脚本导出，保留免登录下载。详情见《微软应用接入与审核.md》。0.11及以前游戏启动证据属于历史版本，不能用来证明0.12的正版登录或启动。

# 0.11 MC 百科接入

新增内置百科索引、中文/ID/链接检索、MR兼容发布安装及百科网页/本地JAR导入。实时抓取与验证码下载未实测；详情见 0.11-MC百科接入.md，最终结果见 artifacts/release-verification-0.11.json。

# 0.10 工作状态与继续入口

继续在0.9主工程上完成多源搜索、Maven比较器/TOML属性处理、HMCL pack.json及官方等效MultiMC补丁转换。矩阵对应RES-003、RES-009、PACK-004/005保持部分实现，缺口没有删除；PACK-002新增真实Adrenaline验收。详见0.10实现与验收.md。

主工程与源码包的最终测试、最终ZIP自包含运行、四实例GUI启动结果以 artifacts/release-verification-0.10.json 及其中回执为准。首次候选失败是异步进度覆盖安装成功文字，已修复且保留证据；新测试均使用独立复制的缓存与实例，未使用用户原游戏目录。

下一批：跨商店可信映射/跨页去重、JarJar完整选择与Quilt约束、自定义客户端/第三方组件元数据、更多实际GUI/世界验收。没有CF/MS密钥，不重复索取。第三方语言加载器只提示由游戏检查，不能声称全部TOML语义已验收。

保留0.8/0.9包。源码包含Maven对照TSV，排除下载Java、游戏、测试运行数据；Maven Apache许可和NOTICE随包。不要遗漏普通/RID两套NuGet锁文件；重新生成矩阵需外部PCL参考树，构建不需要。

## 历史记录

# 0.9 工作状态与继续入口

本版补齐 JAVA-009、PACK-009：逐字段设置继承、整合包逐文件可选项；并修复真实 Fabric 安装遇到的元数据超时。146 项回归通过，候选与最终包实际 GUI/游戏回执分别保存于 optional-gui-retry-0.9、final-optional-0.9。打包/源码复建状态见 artifacts/release-verification-0.9.json；细节见 docs/0.9实现与验收.md。

保留 0.8 应用与源码包。0.9 设置写入迁移 schema 2，备份在使用的数据目录 settings-backups；不要同时用 0.8/0.9 操作同一目录。真实世界存档反馈仍仅对应用户已确认的 0.8 测试。

继续优先：多源搜索合并、完整 Maven/TOML 约束、复杂包组件、逐项 GUI 验收。无账号或密钥工作继续推进；未取得条件的账户/私有服务/双端联机保持外部阻塞。不能声称完整版已完成，不从矩阵移除缺项。

## 0.8 历史记录

# 0.8 工作状态与继续入口

本轮交付是0.8阶段版本，不是PCL2完整对齐。当前源码在此目录；不重置原附件、0.7运行包或独立SwiftUI0.5工程。

- 141项回归通过：artifacts/evidence-0.8/full-regression-7/test-results.json。
- 12窗口GUI检查：artifacts/ui-0.8-final/evidence/workspace-ui-result.json；最终ZIP自身复验另见release-verification-0.8.json。
- 10个原有/当前版本与加载器组合复测：artifacts/evidence-0.8/final-runtime-summary.json；旧版/OptiFabric等另见evidence-0.8-compat。
- 用户明确确认0.8候选1.21.1进入、保存、重新进入正常，证据已单列，不能推广到其他版本。
- 完整矩阵132项，状态由scripts/merge-feature-status.py与build-feature-matrix.py生成；详读remaining和evidenceLevels，禁止按文件存在认定验收。

后续优先：真实GUI逐项操作验收与复杂实例/整包兼容；多源资源合并检索及可选文件选择；逐字段设置继承；统一通行证专属流程；有授权发布源后再做签名更新器。不能将缺失功能移出矩阵分母。微软/CF/外置账户/双人联机需真实外部条件，暂不重复索取已有拒绝的client ID。没有GitHub或公网发布授权。

重建：私有SDK bootstrap →普通锁文件restore/build/test →build-macos.sh使用packages.osx-arm64.lock.json发布 →package-source.py。两种锁文件分开保留。0.8包为本地ad-hoc，未Developer ID签名或公证。

保留失败证据：早期回归5的诊断fixture缩进错误、首次元数据补丁编译类型错误、发布锁运行时不一致、1.18.2清单超时。对应修复与后续通过记录均在artifacts；不覆盖历史失败json。
