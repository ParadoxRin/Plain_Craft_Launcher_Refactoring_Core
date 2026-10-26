"""Apply only completed 0.33 observations to the immutable historical reconciliation snapshot."""
from pathlib import Path
import json,hashlib,collections
root=Path(__file__).resolve().parent.parent
m=json.loads((root/'artifacts/verification-0.33/matrix-reconciled-before-implementation.json').read_text())
features={f['id']:f for f in m['features']}
def attach(name,ids,scope):
 p=root/'artifacts/verification-0.33'/name;d=json.loads(p.read_text());assert d.get('status',d.get('Status'))=='passed'
 e=dict(path=str(p.relative_to(root)),sha256=hashlib.sha256(p.read_bytes()).hexdigest(),assertions=d.get('checks',d.get('Checks',[])),level='平台实际运行',scope=scope)
 for i in ids.split():
  features[i]['audit033'].setdefault('newEvidence',[]).append(e)
  features[i]['testEvidence'].append(e['path'])
attach('release-skin-script-tests.json','INST-010','真实 shell 转义、Host 导出、文件保护、离线身份和 Alex 模型准备；身份拒绝部分使用测试输入。')
attach('release-script-ui/ui.json','INST-010','0.33 实际 Avalonia 实例页和导出按钮，受控导出保留可执行位。')
attach('release-script-ui/standalone.json','INST-010','UI/Host 退出后脚本直接运行真实原版1.21.1，渲染及额外5秒存活；测试主动结束，不冒充正常菜单退出或世界保存。')
attach('release-microsoft.json','AUTH-004 AUTH-005 AUTH-010','0.33 新 Host 钥匙串访问获用户允许后，恢复既有微软账户、刷新权益、只读皮肤和游戏渲染；没有第二账号切换或新的网页登录。')
attach('release-browser.json','AUTH-001 AUTH-002 AUTH-013 RES-001 RES-006','0.33 真实 Host 的离线账户、公开皮肤获取保存刷新、目录查询、详情图标和重连恢复；外置认证只有拒绝路径。')
f=features['INST-010'];f.update(status='已验收',ui='实例管理 → 导出离线启动脚本',macImplementation='src/Pcl.Core/InstanceManager.cs; src/Pcl.Core/OfflineLaunchScript.cs; src/Pcl.Core/Adapters/Pages/WorkspaceExecutionAdapter.cs; src/Pcl.Desktop/OfflineScriptWindow.cs',implementationSummary='通过 Contract/Host 复用已安装版本、Java 匹配与 LaunchService，导出逐参数引用的 POSIX .command；仅离线身份，不读取微软凭据。Client 校验哈希、原子发布并授予仅所有者可执行权限。',remaining='本机原版1.21.1实际渲染通过；脚本依赖原实例、Java和缓存位置，不是跨机整合包。自定义前置命令不导出；其他版本和加载器的脚本路径尚未逐一验收。')
f['audit033'].update(implementationGap=False,workKind='已覆盖限定范围',evidenceState='平台实际运行',nextAction=f['remaining'],note='0.12 禁止导出的历史记录已由0.33实际实现替代；旧testEvidence仅保留变更沿革。')
counts=collections.Counter(x['audit033']['workKind'] for x in features.values())
m['evidenceReconciliation']['workKinds']=dict(counts)
m['evidenceReconciliation']['statusCounts']=dict(collections.Counter(x['status'] for x in features.values()))
m['evidenceReconciliation']['completedImplementationThisRelease']=['INST-010']
m['evidenceReconciliation']['historicalEvidenceVersionCoverage']=['0.24','0.25','0.26','0.27','0.28','0.29','0.30','0.31','0.32']
m['evidenceReconciliation']['currentReleaseTestedFeatures']=[x['id'] for x in features.values() if x['audit033'].get('newEvidence')]
(root/'docs/data/PCL2完整功能矩阵.json').write_text(json.dumps(m,ensure_ascii=False,indent=2)+'\n')
(root/'artifacts/verification-0.33/matrix-final.json').write_text(json.dumps(m,ensure_ascii=False,indent=2)+'\n')
lines=['# 0.33 功能矩阵逐项证据与剩余清单','','已审查132项；77项直接关联0.24–0.32的具体测试断言、文件和SHA-256。其余保留既有证据和适用范围，不以缺少新记录推定没有实现。','','本轮实际补齐 **INST-010 离线启动脚本**，不是状态调整。新增测试范围见 `audit033.newEvidence` 以及 `0.33-架构实现与验收.md`。','','| 类别（互斥） | 项数 | 含义 |','|---|---:|---|',f'| 已覆盖限定范围 | {counts["已覆盖限定范围"]} | 已有证据覆盖声明范围，不代表任意组合等价 |',f'| 实现缺口 | {counts["实现缺口"]} | 有明确缺失或未完成的行为 |',f'| 定向验收 | {counts["定向验收"]} | 已有实现，仍缺指定场景的直接验证 |',f'| 外部条件验收 | {counts["外部条件验收"]} | 需要额外账户、密钥、双端环境或不可得信息 |','','外部条件还可能与代码缺口交叉；完整标记在 requiresExternalCondition。没有把46项待验收都算成缺少功能，也没有把一个报告中的断言数量算成已完成功能数量。','','优先顺序：不需要密钥的明确实现缺口 → 同类用例的定向验收 → 具备外部条件后补测。UI和远程服务未覆盖范围在逐项表中保留。','']
for kind in ('实现缺口','定向验收','外部条件验收','已覆盖限定范围'):
 lines+=['## '+kind,'','| ID | 功能 | 下一步 / 适用边界 | 额外条件 | 历史证据数 |','|---|---|---|---|---:|']
 for f in features.values():
  a=f['audit033']
  if a['workKind']==kind:
   text=f['remaining'] if kind=='已覆盖限定范围' else a['nextAction']
   lines.append('| '+f['id']+' | '+f['name']+' | '+text.replace('|','／')+' | '+('是' if a['requiresExternalCondition'] else '否')+' | '+str(len(a['evidence']))+' |')
 lines+=['']
(root/'docs/0.33-证据回填与剩余清单.md').write_text('\n'.join(lines))
print(json.dumps(m['evidenceReconciliation'],ensure_ascii=False,indent=2))
