from pathlib import Path
import re,json,hashlib,datetime
root=Path(__file__).resolve().parent.parent
ref=root.parent/'pcl-download-fix/PCL-2.99.0.0'
source=ref/'Plain Craft Launcher 2'
docs=root/'docs'
if not source.is_dir() or not (source/'Pages').is_dir():
    raise SystemExit('PCL reference tree missing; retain docs/data snapshots. See docs/data/README.md.')
(docs/'data').mkdir(exist_ok=True)
# Each row is a user-visible capability; status is deliberately separate from evidence.
groups=[
('AUTH','账户','Pages/PageLaunch','Accounts.cs','首页账户','离线名称与稳定UUID;多离线账户切换和删除;微软设备码授权;微软刷新和Keychain;微软权益与档案;多微软账户与退出;外置认证服务器配置;外置角色选择与刷新;统一通行证预设;皮肤展示与缓存;皮肤更换与清除;披风选择;离线皮肤'),
('INST','实例','Pages/PageInstance','InstanceManager.cs','实例管理','多个游戏目录扫描导入;实例搜索与分类;收藏隐藏与恢复;名称描述与图标;复制实例;实例备份恢复;可恢复删除;文件补全和修复;已有组件增删改;离线启动脚本;目录快捷入口;共享缓存安全复用'),
('VER','游戏与加载器','Pages/PageDownload','VersionInstaller.cs','下载版本 / 加载器','官方正式版目录;快照与历史目录;原版安装与启动;旧minecraftArguments适配;LWJGL2 ARM64;Fabric安装;Forge现代安装;Forge旧安装器;NeoForge现代安装;NeoForge1.20.1坐标;OptiFine安装;LiteLoader安装;Fabric API前置;OptiFabric兼容组合;客户端服务端安装器另存为;已有实例修改与回滚'),
('JAVA','Java与启动','Modules/Minecraft','LaunchService.cs','实例管理 → 启动设置','元数据Java自动匹配;Java8ARM64;Java16ARM64;Java17ARM64;Java21ARM64;Java25ARM64;手动选择与检查Java;系统Java发现排序;全局与实例覆盖;内存与GC参数;窗口分辨率全屏;自定义JVM游戏参数;自动进入服务器;启动前命令等待;启动器可见性策略;多平台规则和原生库审核'),
('NET','下载','Modules/Base','DownloadService.cs','任务与诊断','完整性校验与原子提交;Range续传;非Range回退;并发与目标去重;源优先级及镜像回退;HTTP重试与RetryAfter;总限速;暂停继续与取消;文件任务历史;重启中断文件重试;完整安装任务重启恢复;代理设置;测速;多分片动态调度'),
('RES','社区资源','Modules/Resource','ResourceCatalog.cs','模组 / 光影 / 资源包 / 数据包','Modrinth检索筛选分页;CurseForge检索;跨平台结果去重;中文别名词库;百科关联;项目详情版本与另存为;模组必需前置;Fabric语义版本与冲突;ForgeNeoForge完整TOML约束;光影下载应用撤销;Mac光影兼容说明;资源包下载应用;数据包按世界安装;本地外部资源扫描;批量启停与依赖保护;模组检查更新与更新;忽略特定更新;本地文件导入'),
('PACK','整合包','Modules/Minecraft','PortableModpackService.cs','整合包 / 整合包管理','Modrinth搜索下载;mrpack安装;CurseForge包安装;HMCL包安装;MultiMC包安装;MCBBS包安装;普通ZIP安装;含启动器包识别;可选文件选择;mrpack导出;导出包含项与隐私;导出配置保存读取;整合包升级合并用户改动;缺失受限资源手动匹配'),
('DIAG','诊断','Modules/Minecraft','DiagnosticsService.cs','任务与诊断 → 日志诊断','实时启动输出;进程退出与崩溃区分;错误报告导入;Java内存原生库诊断;模组依赖图形网络诊断;脱敏诊断包;原版完整崩溃规则集'),
('LINK','联机','Pages/PageLink','EasyTierService.cs','联机','EasyTierMac模块安装;房间创建加入;邀请码与自定义节点;本地端口转发;连接状态延迟与人数;双端Minecraft联机;PCL原版节点和邀请码互通'),
('UI','个性化与系统','Pages/PageSetup','AppearanceService.cs','设置与帮助','深浅主题配色;背景图片;背景音乐与游戏跟随;自定义主页;功能隐藏恢复;帮助检索;设置导入导出重置;启动器自动更新;公告;语言切换;缓存清理;自定义事件;拖入安装;命令行入口')]
features=[]
for prefix,domain,src,mac,ui,names in groups:
    for i,name in enumerate(names.split(';'),1):
        features.append(dict(id=f'{prefix}-{i:03}',domain=domain,name=name,sourceRoot=str(source.relative_to(root.parent)),sourceEntry=src,sourceEvidence='公开源码入口；细节见source-ui-inventory.json，删节功能单列',macImplementation=f'src/Pcl.Core/{mac}',ui=ui,status='部分实现' if (root/'src/Pcl.Core'/mac).exists() else '未实现',testEvidence=[],remaining='逐项接入、回归与真实GUI验收；不从同域功能推定本项完成',externalConditions=[]))
# Read evidence overrides after implementation, never derive runtime success from file existence.
overrides=docs / 'data' / 'feature-status-overrides.json'
if overrides.exists():
    updates=json.loads(overrides.read_text())
    for f in features:f.update(updates.get(f['id'],{}))
inventory=[]
for p in sorted(source.rglob('*')):
    if p.suffix.lower() not in ('.xaml','.vb'):continue
    for number,line in enumerate(p.read_text(errors='replace').splitlines(),1):
        labels=re.findall(r'(?:Content|Text|Title|ToolTip)="([^"{}][^"]*)"',line) if p.suffix=='.xaml' else []
        settings=re.findall(r'New Setting\("([^"]+)"',line)
        handlers=re.findall(r'(?:Public|Private|Friend) (?:Shared )?(?:Sub|Function) ([\w]+)',line)
        if labels or settings or handlers: inventory.append(dict(path=str(p.relative_to(ref)),line=number,labels=labels,settings=settings,symbols=handlers))
archive=Path.home() / 'Downloads' / 'PCL-2.99.0.0.zip'
provenance=dict(baseline='PCL 2.99.0.0',upstreamCommit='6b9a50df911c9d68b69b24c979efba477fb14b67',upstreamHeadVerified='2026-10-23',upstreamDelta='当前API默认分支最新提交与基线一致',sourceIsPatched=True,originalZipSha256=hashlib.sha256(archive.read_bytes()).hexdigest() if archive.exists() else None)
(docs / 'data' / 'source-ui-inventory.json').write_text(json.dumps(inventory,ensure_ascii=False,indent=2))
# 重建条目时保留历史核对和当前统计，不能删除已审查的版本依据。
matrix_path=docs / 'data' / 'PCL2完整功能矩阵.json'
previous=json.loads(matrix_path.read_text()) if matrix_path.exists() else {}
previous.update(provenance=provenance,features=features)
if 'currentMatrixReview' in previous:
    from collections import Counter
    previous['currentMatrixReview'].update(activeFeatures=len(features),statusCounts=dict(Counter(f['status'] for f in features)),workKinds=dict(Counter(f['audit033']['workKind'] for f in features)))
matrix_path.write_text(json.dumps(previous,ensure_ascii=False,indent=2)+'\n')
lines=['# PCL2 完整功能矩阵','', '固定基线：PCL 2.99.0.0 / `6b9a50df911c9d68b69b24c979efba477fb14b67`。2026-10-23 查询上游默认分支，最新提交仍为此基线。参考树含本地下载修复，原档哈希另存 JSON。','',f'本表登记 {len(features)} 项用户能力；源码入口清单包含 {len(inventory)} 条页面、设置和过程记录。此为追踪范围，不是已实现数量，也不代表发行版未公开代码已获得。','', '公开删节能力不能算成已复用。按用户要求取消百宝箱（原UI-015），不纳入当前功能计划，也不计为已完成；历史资料保留原始范围。','', '| ID | 功能 | 状态 | Mac 实现 / 界面 | 验收 / 剩余 |','|---|---|---|---|---|']
for f in features:lines.append(f"| {f['id']} | {f['domain']} · {f['name']} | {f['status']} | `{f['macImplementation']}` / {f['ui']} | {'; '.join(f['testEvidence'])} {f['remaining']} |")
lines+=['','机器可读记录：data/PCL2完整功能矩阵.json；页面/设置/事件符号：data/source-ui-inventory.json。状态修改保存在data/feature-status-overrides.json，由scripts/build-feature-matrix.py复现。']
(docs/'PCL2完整功能矩阵.md').write_text('\n'.join(lines)+'\n')
print(json.dumps({'features':len(features),'sourceEntries':len(inventory)},ensure_ascii=False))
