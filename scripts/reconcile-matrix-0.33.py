"""Versioned, per-feature evidence reconciliation. Never promote a row from a passing file alone."""
from pathlib import Path
import json,hashlib,collections
root=Path(__file__).resolve().parent.parent
before=root/'artifacts/verification-0.33/matrix-before.json'
m=json.loads(before.read_text()); fs={f['id']:f for f in m['features']}
mapped=collections.defaultdict(list)
def evidence(path,selector,ids,scope,level='平台实际运行'):
 p=root/path; d=json.loads(p.read_text())
 if isinstance(d,list):
  assert all(x.get('status')=='passed' for x in d); checks=[str(x['family'])+':renderer='+str(x['rendererInitialized'])+',userFilePreserved='+str(x['userFilePreserved']) for x in d]
 else:
  assert d.get('status',d.get('Status'))=='passed',path
  checks=d.get('checks',d.get('Checks',[]))
  if not checks:checks=[f'{k}={v}' for k,v in d.items() if isinstance(v,bool)]
 selected=[x for x in checks if any(t in x for t in selector.split('|'))] if selector else checks
 assert selected,(path,selector)
 for i in ids.split():
  assert i in fs
  mapped[i].append(dict(path=path,sha256=hashlib.sha256(p.read_bytes()).hexdigest(),assertions=selected,level=level,scope=scope))
a='artifacts/architecture-0.32/'
evidence(a+'release-microsoft.json','profile|appearance|preview','AUTH-010','既有微软账户的外观只读查询、规范化 PNG；不是皮肤上传或 3D 预览。')
evidence(a+'release-microsoft.json','refresh-entitlement|renderer','AUTH-004 AUTH-005 VER-003 JAVA-005','既有账户钥匙串恢复、刷新和权益验证，1.21.1 ARM64 Java 21 到渲染；不含新的网页完整授权。')
evidence(a+'release-final-main.json','device-challenge','AUTH-003','实际微软设备码挑战和取消；完整登录成功仍引用既有历史授权，不冒充本轮重新网页登录。')
evidence(a+'release-browser.json','offline-account|offline-accounts|logout-and-remove','AUTH-001 AUTH-002','真实 Host 离线账户创建、选择、注销和移除；不是两个微软账户。')
evidence(a+'release-browser.json','external-','AUTH-007 AUTH-008','仅缺少挑战、一次性密码句柄和不落盘的失败路径，未成功外置认证。')
evidence(a+'release-browser.json','skin|preset','AUTH-010 AUTH-013','公开玩家皮肤获取/保存/刷新、预设、导出、隔离错误保护和重启；非在线皮肤修改。')
evidence(a+'release-browser-ui/report.json','skin|offline|account','AUTH-001 AUTH-002 AUTH-013','真实 Avalonia 离线账户和皮肤页面事件；不等价于游戏内纹理观察。')
evidence(a+'release-final-main.json','catalog|loader-versions','VER-001 VER-002','官方目录、正式版/快照与加载器查询，不是全部版本启动验收。')
evidence(a+'release-final-main.json','selected-fabric|mac-basic','VER-006 VER-013','实际选定 Fabric 安装与基础 Mac 配置；不是任意 Fabric API 组合一步安装。')
evidence(a+'release-final-main.json','optifabric-artifact|optifabric-compatibility','VER-011 VER-014','已审定 OptiFine/OptiFabric 固定组合的真实安装，不泛化其他组合。')
evidence('artifacts/architecture-avalonia-host/legacy-host-live.json','','VER-004 VER-005 VER-011 VER-012 JAVA-002 NET-011','OptiFine/LiteLoader 1.12.2 安装取消、完整 Host 重建续做、用户文件保留及渲染初始化。')
evidence('artifacts/architecture-0.25/legacy-host-live.json' if (root/'artifacts/architecture-0.25/legacy-host-live.json').exists() else 'artifacts/architecture-contract-0.25/legacy-host-live.json','','VER-011 VER-012','0.25 发布 Host 复测同一旧加载器恢复链路；不扩展其他历史构建。')
b='artifacts/architecture-0.28/'
for selector,ids,scope in [
 ('scans-import|opaque-import','INST-001','扫描与复制导入真实完整实例；不含原地注册或任意继承链。'),
 ('copies-instance','INST-005','真实复制、源实例保持不变。'),
 ('preferences-and-hidden','INST-003','Host 保存偏好并能再次寻址隐藏实例；未补齐完整 GUI 重开筛选。'),
 ('setting-inheritance','JAVA-009','全局与实例覆盖继承实际读写。'),
 ('icon-','INST-004','Host 图标受控导出往返和移除；GUI 图片选择仍单列。'),
 ('backup','INST-006','真实备份校验和恢复为新实例；不含跨设备完整共享 Java/assets。'),
 ('trash','INST-007','真实移入回收并恢复选定实例，不触及其他实例。'),
 ('repairs-real','INST-008','官方安装补全修复，不含所有安装器生成文件自动重建。'),
 ('local-mod|removed-resource','RES-014 RES-015 RES-018','真实本地 JAR 扫描、启停与删除备份；不是全部复杂依赖约束。'),
 ('datapack|world-directories','RES-013','真实世界目录安装/启停/移除数据包，未在游戏内验收行为。'),
 ('custom-pack|pack-change|pack-update','PACK-007 PACK-013','本地测试包安装与用户改动检测/保留；不覆盖任意外部分发包。'),
 ('mrpack','PACK-002 PACK-010','实际 mrpack 导出与重导入；复杂依赖和自定义入口仍保留限制。'),
 ('export-options','PACK-012','Host 持久化导出选项。')]:evidence(b+'workspaces2.json',selector,ids,scope)
evidence(b+'release-ui.json','save-button|launch-setting','INST-002 INST-004 JAVA-009','实际实例分组和启动设置按钮保存，不把这条扩展为图标导入。')
evidence(b+'release-ui.json','local-resources|dependency-button','RES-015','真实启停和依赖分析按钮，复杂约束仍未完整。')
evidence(b+'release-ui.json','pack-export-options','PACK-012','真实导出选项保存与读取按钮。')
evidence(b+'release-ui.json','pack-page-install','PACK-007','GUI 安装一个自定义测试包。')
for selector,ids,scope in [
 ('downloads-real-fabric','RES-007 VER-013','公开 Fabric API 及依赖下载并安装；不覆盖任意依赖求解。'),
 ('updates|batch','RES-016','真实更新求解、批量准备/放弃/提交及旧文件保留。'),
 ('resource-export','RES-006','公开源文件受控导出且匹配官方哈希。'),
 ('pack-activation|undo','RES-012','资源包应用写入 options 并撤销/移除；未检查游戏内材质。')]:evidence(b+'release-resources.json',selector,ids,scope)
c='artifacts/architecture-0.27/'
for name,ids,scope in [('final-download/result.json','VER-015 NET-002 NET-008 NET-010','官方 JAR 下载、取消、真实 Host 崩溃续做、分块导出与校验。'),('final-components/result.json','INST-009 VER-006 VER-016 NET-011','Fabric 组件变更崩溃续做、唯一副本/备份、移除加载器和新副本渲染。')]:evidence(c+name,'',ids,scope)
d='artifacts/architecture-0.29/'
for selector,ids,scope in [
 ('fixture-cf','RES-002 PACK-014','固定元数据替身：受限下载拒绝、错误哈希拒绝、已校验本地文件安装；不是官方密钥接口。'),
 ('appearance-save|global-launch|settings-import|settings-export|validate-all|stale-settings','UI-007 JAVA-009 NET-012','真实设置持久化和导入/导出保护；不是代理服务器数据链路。'),
 ('home-preset|custom-preset','UI-004','离线独立主页快照预设保存和删除，不支持任意 WPF。'),
 ('cache-','UI-011','缓存预览变化跳过、可恢复移动和恢复；没有共享文件引用计数 GC。'),
 ('network-probe','NET-013','真实公开网络有限大小探测；不是持续吞吐量。'),
 ('mcmod-index','RES-004 RES-005','本地百科索引搜索，不是 mcmod.cn 全站实时下载或浏览器跳转验收。'),
 ('deduplication|search-back','RES-001 RES-003','同一商店分页去重与缓存返回，不冒充跨商店身份合并。')]:evidence(d+'release-discovery.json',selector,ids,scope,'替身运行' if selector=='fixture-cf' else '平台实际运行')
for selector,ids,scope in [('background-directory','UI-002','实际背景目录选择并应用一张图片，不覆盖所有布局。'),('home-example','UI-004','真实创建主页示例。'),('settings-help|global-settings','UI-006 UI-007','实际设置帮助页保存，不是帮助全文检索验收。'),('cache-','UI-011','真实缓存回收及恢复按钮。')]:evidence(d+'release-ui.json',selector,ids,scope)
for selector,ids,scope in [('live-default-catalog|pagination|filter','RES-001 PACK-001','默认目录模组/光影/资源包/整合包实际搜索过滤分页；不等于每类包下载安装。'),('description|changelog|presentation|icon','RES-006','真实详情、版本、更新日志及图标。')]:evidence(a+'release-browser.json',selector,ids,scope)
evidence(a+'release-browser-ui/report.json','resource-search|resource-detail|file-row|return-to|shader-browser','RES-001 RES-006','真实搜索、详情、文件选择、返回及光影检索 UI；未把检索当成光影效果验收。')
evidence(a+'release-verified-shell.json','paused|resume-and-retry|download-and-network','NET-008 NET-009 NET-010','真实 HTTP 暂停、继续、失败文件重试与哈希校验；历史展示契约通过。')
evidence(a+'release-verified-shell.json','diagnostic','DIAG-003 DIAG-004 DIAG-006','真实日志分析和脱敏 ZIP 导出；不是所有诊断规则命中。')
evidence(a+'release-verified-shell.json','classifies|imports-real','RES-018 UI-013','Host ZIP 分类与实际导入；系统拖放事件不在此测试中。')
evidence(a+'release-verified-shell-ui/report.json','network|diagnostic|pause|resume|drop','NET-008 NET-012 DIAG-006 UI-013','真实 UI 保存/重开、暂停/继续、诊断与拖入确认处理器；网络保存不等于真实代理，导入处理器不等于系统拖放。')
evidence(a+'release-final-cancel.json','','NET-008 NET-011','安装按钮后关闭主窗口，receipt cancelled 且 UI 完成。')
evidence(a+'release-final-resume.json','','NET-011','重开应用、任务中心续做完成；关视图不取消后台任务。')
evidence(a+'release-verified-managed.json','','JAVA-015 DIAG-001 DIAG-002','真实窗口隐藏、渲染和恢复；测试主动停止退出码137，不是正常菜单退出。')
evidence(a+'release-verified-detached/packaged-detached-verification.json','','JAVA-015','UI/Host 退出后游戏继续渲染并存活5秒，随后清理本轮进程。')
evidence(a+'release-final-main.json','easytier|real-tcp|room|multiplayer','LINK-001 LINK-002 LINK-003 LINK-004 LINK-005','两个本机真实节点和 TCP 往返，房间/邀请码/状态/关闭实际运行；不证明远程 NAT 或游戏双端连接。')
evidence(a+'release-main-ui/report.json','room|connection|window-close','LINK-002 LINK-005','真实联机窗口创建/刷新/关闭；没有两名玩家人数验收。')
# Retain per-release observations even where the later release repeats the same bounded behavior.
evidence('artifacts/architecture-0.26/packaged-session.json','game-launch|game-restored|renderer','VER-003 JAVA-015','0.26 实际会话断线重连后恢复同一游戏并完成渲染；不是新账号登录或跨版本兼容证明。')
evidence('artifacts/architecture-0.26/pages-live.json','component-copy|component-backup','INST-009','0.26 实际组件预览、复制和备份；未知提交点仍要求复核。')
evidence('artifacts/architecture-0.26/pages-live.json','official-jar|artifact-cancel','VER-015 NET-008','0.26 官方客户端 JAR 下载与取消，SHA-1 校验；不涵盖全部服务端安装器。')
evidence('artifacts/architecture-0.26/packaged-microsoft-retry.json','profile|refresh-entitlement|renderer','AUTH-004 AUTH-005 AUTH-010','0.26 已有微软账户刷新、只读皮肤资料和图形初始化。')
evidence('artifacts/architecture-0.30/release-final-host.json','skin|preset','AUTH-013','0.30 正式包真实公开皮肤和本地预设往返；不替代游戏内观察。')
evidence('artifacts/architecture-0.30/release-final-host.json','offline-account|offline-accounts|logout-and-remove','AUTH-001 AUTH-002','0.30 Host 离线账户创建、切换、注销和删除。')
evidence('artifacts/architecture-0.30/release-final-ui/report.json','skin|offline-login','AUTH-001 AUTH-013','0.30 Avalonia 真实离线登录和皮肤保存刷新按钮。')
evidence('artifacts/architecture-0.30/release-final-ui/report.json','resource|shader|installs-real','RES-001 RES-006 RES-007','0.30 真实默认资源浏览器与 Fabric API 安装；不代表任意依赖求解。')
evidence('artifacts/architecture-0.31/release-main.json','catalog|loader-versions','VER-001 VER-002','0.31 官方版本及加载器目录查询。')
evidence('artifacts/architecture-0.31/release-main.json','easytier|real-tcp|room','LINK-002 LINK-003 LINK-004 LINK-005','0.31 本机双节点 TCP、邀请码和生命周期；不外推跨网 NAT 或双人游戏。')
evidence('artifacts/architecture-0.31/release-microsoft.json','profile|refresh-entitlement|renderer','AUTH-004 AUTH-005 AUTH-010','0.31 现有微软账户刷新、皮肤读取及正版图形初始化。')
# Explicit per-row gap decisions; a historical limitation is not automatically a missing implementation.
code=set('AUTH-007 AUTH-008 AUTH-009 AUTH-010 INST-001 INST-006 INST-008 INST-009 INST-010 VER-008 VER-011 VER-013 VER-016 NET-005 NET-011 NET-012 NET-014 RES-004 RES-007 RES-009 RES-010 RES-015 RES-016 PACK-002 PACK-004 PACK-005 PACK-006 PACK-008 PACK-010 PACK-013 PACK-014 DIAG-007 UI-004 UI-008 UI-009 UI-011 UI-012 UI-014'.split())
external=set('NET-005 AUTH-006 AUTH-007 AUTH-008 AUTH-011 AUTH-012 RES-002 PACK-003 PACK-014 LINK-006 LINK-007 UI-015'.split())
code.discard('NET-005')
code.discard('PACK-010')
promote=set('INST-005 INST-007 VER-001 VER-002 JAVA-005 NET-008 NET-009 NET-010 NET-013 RES-001 RES-006 RES-018 PACK-012 LINK-002 LINK-003 LINK-004'.split())
updates={
 'AUTH-006':'双账户实现有协议夹具；同一真实微软账户退出重登已有历史证据。缺第二个真人微软账户切换验收，不应写成未实现多账户。',
 'AUTH-007':'外置服务器配置及挑战失败路径已迁入 Host；尚无真实外置账户完整登录验收，当前游戏执行器明确拒绝外置账户启动。',
 'AUTH-008':'角色选择与刷新界面/协议已有实现；真实外置账户成功链路未验收，外置账户游戏启动仍缺。',
 'AUTH-010':'微软资料与规范化二维皮肤读取、离线缓存已有真实证据；完整在线皮肤磁盘缓存及3D人物预览仍缺。',
 'AUTH-013':'公开玩家名获取、保存、刷新、导出、预设及重开恢复已实际运行；游戏内皮肤纹理/模型仍待观察。',
 'INST-003':'Host 已验证保存偏好、隐藏后可寻址；仍需完整 GUI 收藏/隐藏/恢复和重开筛选的定向验收。',
 'INST-004':'真实 Host 图标导出/移除和 GUI 名称/分组保存已有证据；图片选择、更换与重开视觉仍需定向验收。',
 'NET-005':'来源优先级与回退已有实现，Modrinth 实际链路已有历史验证；剩余是给定 CurseForge 密钥/镜像后的真实回退验收，不能把未实测列为代码缺口。',
 'PACK-010':'合法 mrpack 导出、哈希记录和重导入已有真实证据；当前采用 overrides 打包本地文件，带自定义组件依赖 PCL 扩展。需补其他启动器导入兼容验收，不把模组依赖求解缺口复制到导出项。',
 'PACK-002':'已支持标准 mrpack、可选文件和受控覆盖。整合包内模组的全根联合择版、Quilt覆盖和JarJar完整模块身份仍受依赖求解器能力限制；不代表标准清单解析未实现。',
 'PACK-013':'已有升级为新副本、保留用户修改/删除及源实例回滚；尚无针对用户与新包同时修改同一配置的语义三方合并，自定义入口限制仍适用。',
 'RES-007':'必需前置下载已实际通过；待补多根联合择版、Quilt完整path/pattern/version覆盖、Fabric schema0/覆盖及自定义插件。现有来源下载测试不能证明这些约束完整。',
 'RES-009':'待补JarJar完整module-info身份、语言插件、NeoForge provides等全部TOML语义及历史取舍规则；已有范围求交和声明校验，不代表完整解析所有加载器语义。',
 'RES-015':'真实批量启停/备份和依赖分析入口已运行；约束保护仍受完整Quilt覆盖、Fabric覆盖和JarJar模块身份能力限制。',
 'RES-016':'真实更新查询、批量准备/提交/放弃和旧文件保留已运行；待补多根联合择版与上述复杂加载器约束，不能把一次更新成功外推任意组合。',
 'INST-010':'离线身份已在后续版本恢复，但旧 ExportOfflineScriptAsync 仍直接拒绝。缺可实际执行的 Mac .command 导出，不沿用0.12的过期产品取舍作为完成理由。',
 'VER-012':'1.12.2 LiteLoader 已完成真实 Host 重建恢复、启动及用户文件保留；其他历史构建需逐版验收，不存在本轮已证实的通用代码缺口。',
 'VER-013':'默认浏览器已自动准备 Java/Fabric，Fabric API 下载与依赖安装实际通过；任意版本组合的一步组件安装尚未实现。',
 'VER-015':'官方 JAR 下载取消、崩溃续做、分块保存已实测。Quilt 安装器上游元数据哈希冲突仍保留拒绝，其他类型不因单一 JAR 通过而全部升级状态。',
 'NET-014':'固定并发分段、完整块复用和设置持久化已有实现；不等于 Windows 原版完整动态分片调度，需要明确实现/补测调度行为。',
 'RES-001':'Modrinth 默认目录和多源页面已完成真实检索/过滤/分页及 GUI 操作；不保证任意第三方内容兼容。',
 'RES-005':'本地百科索引和关联查询已实际运行；mcmod.cn 浏览器跳转及站点实时下载不能从本地索引测试推定。',
 'RES-006':'Modrinth 详情、版本、文件选择和官方哈希导出已实测；CurseForge 详情仍以网页和密钥条件为界。',
 'PACK-001':'Modrinth 整合包目录查询已实测；仍缺本轮最终 GUI 直接下载公开整合包的专门记录，不能用模组下载安装代替。',
 'PACK-012':'真实 Host 持久化及 GUI 保存/读取导出选项已通过；不保证任意用户配置含密内容均可自动脱敏。',
 'UI-013':'真实 ZIP 分类、导入确认按钮和安装处理器已通过；尚未手动测试 macOS 系统拖放事件。',
}
for i,text in updates.items():fs[i]['remaining']=text
for f in fs.values():
 i=f['id']; old=f['status']; entries=mapped[i]
 if i in promote:
  assert entries and any(e['level']=='平台实际运行' for e in entries)
  f['status']='已验收'
 if i=='AUTH-006':f['status']='已实现待验收'
 if i=='NET-014':f['status']='部分实现'
 gap=i in code or old=='未实现'
 covered=not gap and f['status'] in ('已验收','平台适配实现')
 work='实现缺口' if gap else ('外部条件验收' if i in external else ('已覆盖限定范围' if covered else '定向验收'))
 f['audit033']={'reviewed':True,'baselineStatus':old,'evidenceRange':'0.24–0.32','evidence':entries,'evidenceState':'平台实际运行' if any(e['level']=='平台实际运行' for e in entries) else '本范围未新增直接证据','implementationGap':gap,'requiresExternalCondition':i in external,'workKind':work,'acceptanceScope':[e['scope'] for e in entries],'nextAction':('保留已验收的明确范围；不外推所有版本、组合或平台。' if covered else f['remaining']),'note':'无本范围证据时保留历史结论；矩阵状态不等同当前包逐项重测。'}
 for e in entries:
  ref=e['path']+'#'+','.join(e['assertions'])
  if ref not in f['testEvidence']:f['testEvidence'].append(ref)
 # Only remove explicitly superseded acceptance TODOs when this exact behavior has a direct observation.
 if i in promote:
  f['remaining']='已回填限定范围实测；适用范围见 audit033.acceptanceScope。'+f['remaining'] if i in ('RES-006',) else ('本项限定范围已实测；不外推所有第三方内容或平台。' if i not in ('NET-010','LINK-002','LINK-003','LINK-004') else ('文件下载续传已实测；不代表任意安装器都支持断点续做。' if i=='NET-010' else '本机房间、邀请码与连接链路已实测；跨互联网 NAT 和双玩家游戏未验收。'))
count=collections.Counter(f['audit033']['workKind'] for f in fs.values())
m['evidenceReconciliation']={'version':'0.33','reviewedFeatures':len(fs),'mappedFeatures':len([x for x in mapped.values() if x]),'workKinds':dict(count),'statusCounts':dict(collections.Counter(f['status'] for f in fs.values())),'note':'逐项范围审查；基于真实记录引用，不用测试文件通过推定其中未覆盖的功能。'}
(root/'docs/PCL2完整功能矩阵.json').write_text(json.dumps(m,ensure_ascii=False,indent=2)+'\n')
(root/'artifacts/verification-0.33/matrix-reconciled-before-implementation.json').write_text(json.dumps(m,ensure_ascii=False,indent=2)+'\n')
lines=['# 0.33 功能矩阵逐项证据与剩余清单','',f'审查 {len(fs)} 项；0.24–0.32 的直接新增证据覆盖 {m["evidenceReconciliation"]["mappedFeatures"]} 项。以下分类互斥；外部条件另有独立标记，不将部分实现与待验收混为一谈。','',str(dict(count)),'','这份文件先于本轮功能实现生成；本轮新增证据由最终报告及矩阵 audit033.newEvidence 记录。历史证据不代表0.33重新测试。','']
for kind in ('实现缺口','外部条件验收','定向验收','已覆盖限定范围'):
 lines+=['## '+kind,'','| ID | 功能 | 下一步 / 边界 | 证据数 |','|---|---|---|---|']
 for f in fs.values():
  if f['audit033']['workKind']==kind:lines.append('| '+f['id']+' | '+f['name']+' | '+f['audit033']['nextAction'].replace('|','／')+' | '+str(len(f['audit033']['evidence']))+' |')
 lines+=['']
(root/'docs/0.33-证据回填与剩余清单.md').write_text('\n'.join(lines))
print(json.dumps(m['evidenceReconciliation'],ensure_ascii=False,indent=2))
