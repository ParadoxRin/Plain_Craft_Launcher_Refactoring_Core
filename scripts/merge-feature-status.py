"""Merge reviewed domain status, preserving explicit gaps and evidence boundaries."""
from pathlib import Path
import json,collections
root=Path(__file__).resolve().parent.parent; docs=root/'docs'; merged={}
for name in ['accounts-instances','resources-packs','network-cache','segmented-download','compatibility']:
 data=json.loads((docs/f'status-{name}.json').read_text())
 if 'items' in data:
  data={x['id']:{'status':x['status'],'implementationSummary':x['implementation'],'testEvidence':x['evidence'],'remaining':x['boundary']} for x in data['items']}
 merged.update(data)
reg='artifacts/evidence-0.8/full-regression-7/test-results.json'
ui='artifacts/ui-0.8-final/evidence/workspace-ui-result.json'
world='artifacts/gui-install-clean-0.8/evidence/user-world-acceptance.json'
game='artifacts/gui-install-clean-0.8/evidence/gui-install-launch.json'
link='artifacts/evidence-0.8/link-live-data/evidence/easytier-smoke.json'
def row(ids, status, implementation, evidence, remaining='', entry=None):
 for id in ids.split():
  merged[id]={'status':status,'macImplementation':implementation,'testEvidence':evidence,'remaining':remaining,'externalConditions':[]}
  if entry:merged[id]['ui']=entry
row('VER-001 VER-002','已实现待验收','src/Pcl.Core/VersionCatalog.cs; src/Pcl.Desktop/VersionManager.cs',[reg,'artifacts/evidence-0.7/acceptance.json'],'正式版、快照与历史过滤；目录可见不等于所有版本可启动。')
row('VER-003','已验收','src/Pcl.Core/MinecraftService.cs; src/Pcl.Core/LaunchService.cs',[game,world],'已验收本轮1.21.1；其他版本按兼容表分别列出。')
row('JAVA-001 JAVA-004 JAVA-005 JAVA-006','已实现待验收','src/Pcl.Core/JavaService.cs; src/Pcl.Core/MacCompatibility.cs',[reg,'artifacts/evidence-0.7/acceptance.json',game],'Java按元数据/架构选择，保留来源回执；0.7历史与本轮结果分别列出，不推定全部游戏适配。')
row('JAVA-010 JAVA-011 JAVA-012 JAVA-013','已实现待验收','src/Pcl.Core/LauncherSettings.cs; src/Pcl.Core/LaunchService.cs',[reg,ui],'配置持久化和参数合并已验证；尚未逐个游戏内观察窗口/服务器/GC效果。')
row('JAVA-014','已验收','src/Pcl.Core/LaunchHooks.cs; src/Pcl.Desktop/SettingsHelpWindow.cs; src/Pcl.Cli/Program.cs',[reg],'显式本地程序；字面参数、等待/失败/超时与子进程生命周期已回归。导入内容不得自动设置执行命令。')
row('JAVA-015','已实现待验收','src/Pcl.Desktop/MainWindow.axaml.cs; src/Pcl.Core/AppearanceService.cs',[ui,game],'隐藏/游戏退出恢复已实现；关闭/隐藏/Dock全部组合尚待手动验收。')
row('JAVA-016','已实现待验收','src/Pcl.Legacy/MinecraftRules.vb; src/Pcl.Core/MacNativeAudit.cs; src/Pcl.Core/MacCompatibility.cs',[reg,'artifacts/evidence-0.8-compat/compatibility-results.json'],'ARM64实际Mach-O检查；Intel/Rosetta未验收，无库可用时明确拒绝。')
row('NET-001 NET-002 NET-003 NET-004 NET-005 NET-006 NET-007','已验收','src/Pcl.Core/DownloadService.cs; src/Pcl.Core/DownloadTasks.cs',[reg,game,'artifacts/evidence-complete/segmented-live-1/segmented-live-result.json'],'HTTP夹具覆盖异常，真实联网校验；极端断电/实际磁盘耗尽尚未注入。')
row('NET-008 NET-009 NET-010','已实现待验收','src/Pcl.Core/DownloadTasks.cs; src/Pcl.Desktop/TaskCenterWindow.cs',[reg,ui],'暂停/继续/取消/重启任务历史已测试；任务列表上限300，历史上限1000，不等于完整安装检查点。')
row('NET-011','部分实现','src/Pcl.Core/InstallOperations.cs; src/Pcl.Desktop/TaskCenterWindow.cs',[reg,game],'原版及支持加载器安装日志可恢复，重新校验并重执行安装器；整合包/资源任务需重新发起，不是任意指令级断点。')
row('NET-012','已实现待验收','src/Pcl.Core/DownloadTasks.cs; src/Pcl.Desktop/TaskCenterWindow.cs',[reg,ui],'文件下载服务支持系统/显式HTTP代理；并非所有第三方元数据请求的统一代理，真实代理链路未测试。')
row('DIAG-001 DIAG-002','已验收','src/Pcl.Core/LaunchService.cs; src/Pcl.Desktop/MainWindow.axaml.cs',[game,'artifacts/evidence-0.7/acceptance.json'],'自然退出、崩溃、用户停止分别显示；测试主动停止的137不作为正常退出。')
row('DIAG-003 DIAG-004 DIAG-005 DIAG-006','已验收','src/Pcl.Core/DiagnosticsService.cs; src/Pcl.Desktop/TaskCenterWindow.cs',[reg],'夹具验证错误导入/分析/脱敏；诊断是建议，不自动删除存档/配置。')
row('DIAG-007','部分实现','src/Pcl.Core/DiagnosticsService.cs; docs/crash-rule-source-map.json',[reg,'docs/crash-rule-source-map.json'],'逐项映射公开46个CrashReason；保留13类通用规则和Mac等效规则。未逐字复刻原优先级及所有日志格式，堆栈猜测标低置信度；未逐规则取得真实崩溃。')
row('LINK-001 LINK-002 LINK-003 LINK-004 LINK-005','已实现待验收','src/Pcl.Core/EasyTierService.cs; src/Pcl.Desktop/MultiplayerWindow.cs',[link,ui],'官方ARM64模块、无root/TUN本地双进程TCP转发通过；房间/节点/状态入口有GUI，跨网NAT与真实游戏双端待验收。')
row('LINK-006','外部条件阻塞','src/Pcl.Desktop/MultiplayerWindow.cs',[link],'尚无双端Minecraft玩家连接实测；TCP echo不是多人游戏。')
merged['LINK-006']['externalConditions']=['第二个Minecraft客户端/玩家及双方可连通节点']
row('LINK-007','外部条件阻塞','src/Pcl.Core/EasyTierService.cs',[],'现使用独立PCLMAC1邀请码；原私有服务/节点协议未获得，不能声称互通。')
merged['LINK-007']['externalConditions']=['原版私有节点协议与授权服务']
row('UI-001','已验收','src/Pcl.Desktop/AppearanceWorkspace.cs; src/Pcl.Desktop/SettingsHelpWindow.cs',[ui],'深浅色截图与中文可读；未做完整辅助功能审计。')
row('UI-002 UI-003','已实现待验收','src/Pcl.Desktop/AppearanceWorkspace.cs; src/Pcl.Core/AppearanceService.cs',[reg,ui],'本地图片/afplay音乐与游戏跟随已接入，播放听感/全部生命周期组合未人工验收。')
row('UI-004 UI-012','部分实现','src/Pcl.Core/AppearanceService.cs; src/Pcl.Desktop/AppearanceWorkspace.cs',[reg],'安全JSON主页支持文字、网页、复制、消息、导航、启动、刷新和加入房间；不兼容原XAML布局或全部变量/事件。')
row('UI-005 UI-006 UI-007','已实现待验收','src/Pcl.Desktop/SettingsHelpWindow.cs; src/Pcl.Core/AppearanceService.cs',[reg,ui],'导航隐藏恢复、帮助检索、设置备份/净化导入导出实现；设置不携带凭据或执行项。重置当前外观，不删除用户世界。')
row('UI-008','未实现','src/Pcl.Desktop/MainWindow.axaml.cs',[],'本地交付，无线上版本源/更新包签名验证或应用替换器；仅公开开发说明，不伪装自动更新成功。')
row('UI-009','部分实现','src/Pcl.Desktop/MainWindow.axaml.cs; docs/【已弃用】使用说明.md',[ui],'关于/本地更新说明存在；原远程公告服务及活动页面未复刻。')
row('UI-010','已验收','src/Pcl.Core/AppearanceService.cs; src/Pcl.Desktop/SettingsHelpWindow.cs',[reg],'原PCL2系统设置实际可用项为首次游戏中文；尊重已有lang，1.10及以前使用zh_CN。原多语言UI选择代码已注释，不计为已启用功能。')
merged['UI-010']['name']='首次游戏中文与界面语言基线'
merged['UI-010']['sourceEntry']='Pages/PageSetup/PageSetupSystem.xaml:103-104,167-184'
row('UI-013','已实现待验收','src/Pcl.Desktop/DropWorkspace.cs',[reg],'本地JAR/litemod和ZIP分类预览；文件拖入的实际系统事件尚未人工回归。')
row('UI-014','已实现待验收','src/Pcl.Cli/Program.cs; src/Pcl.Desktop/Program.cs',[reg,'artifacts/evidence-0.8-compat/compatibility-results.json'],'CLI与GUI使用同一Core；CLI未覆盖所有GUI操作，原Windows命令入口不逐字兼容。')
# Latest integration results augment, rather than erase, domain-specific evidence.
for k,v in merged.items():
 v.setdefault('evidenceLevels',{})['automatedRegression']=reg
 v.setdefault('implementationSummary','')
 v.setdefault('sourceEvidence','公开源入口与符号清单；逐行为链路仍有未核实项，见来源与缺口说明')
 if 'testEvidence' in v: v['testEvidence']=list(dict.fromkeys(v['testEvidence']))
merged['VER-011']['remaining']='独立1.12.2 G5/1.20.1 I6与固定1.20.1 OptiFabric组合通过；Forge+OptiFine任意组合尚未适配，二进制不再分发。'
merged['VER-014']['testEvidence'].append('artifacts/evidence-0.8-compat/optifabric/service-result.json')
merged['VER-014']['remaining']='产品服务创建1.20.1/Fabric0.16.14/OptiFabric1.14.3/OptiFineI6独立实例并图形初始化45秒；未知版本不启用，未做世界内验收。'
merged['NET-014']['testEvidence'].append(ui)
merged['NET-014']['remaining']='最终GUI已增加保存并重读设置验收；仅复用完整块，未完成块重下，默认关闭。'
merged['NET-013']['testEvidence'].append('artifacts/network-probe-0.8/evidence/network-probe.json')
merged['NET-013']['remaining']='真实官方清单小样本请求结果单独记录；64KiB上限及10秒超时，小样本不是持续带宽。'
# Later release evidence overrides only the explicitly verified rows.
if (docs/'status-0.9.json').exists(): merged.update(json.loads((docs/'status-0.9.json').read_text()))
if (docs/'status-0.10.json').exists(): merged.update(json.loads((docs/'status-0.10.json').read_text()))
if (docs/'status-0.11.json').exists(): merged.update(json.loads((docs/'status-0.11.json').read_text()))
if (docs/'status-0.12.json').exists(): merged.update(json.loads((docs/'status-0.12.json').read_text()))
if (docs/'status-0.13.json').exists(): merged.update(json.loads((docs/'status-0.13.json').read_text()))
if (docs/'status-0.14.json').exists(): merged.update(json.loads((docs/'status-0.14.json').read_text()))
if (docs/'status-0.15.json').exists(): merged.update(json.loads((docs/'status-0.15.json').read_text()))
if (docs/'status-0.16.json').exists(): merged.update(json.loads((docs/'status-0.16.json').read_text()))
if (docs/'status-0.17.json').exists(): merged.update(json.loads((docs/'status-0.17.json').read_text()))
if (docs/'status-0.18.json').exists(): merged.update(json.loads((docs/'status-0.18.json').read_text()))
if (docs/'status-0.18.1.json').exists(): merged.update(json.loads((docs/'status-0.18.1.json').read_text()))
if (docs/'status-0.19.json').exists(): merged.update(json.loads((docs/'status-0.19.json').read_text()))
(docs/'feature-status-overrides.json').write_text(json.dumps(merged,ensure_ascii=False,indent=2)+'\n')
print(json.dumps(dict(collections.Counter(x['status'] for x in merged.values())),ensure_ascii=False))
