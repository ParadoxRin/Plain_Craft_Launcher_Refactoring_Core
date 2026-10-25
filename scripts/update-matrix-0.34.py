# -*- coding: utf-8 -*-
"""Add scope-bounded 0.34 evidence without rewriting historical 0.33 audit decisions."""
from pathlib import Path
import json, hashlib, collections
root = Path(__file__).resolve().parents[1]
out = root / 'artifacts/verification-0.34'
path = root / 'docs/PCL2完整功能矩阵.json'
baseline = out / 'matrix-before-0.34.json'
if not baseline.exists():
    baseline.write_bytes(path.read_bytes())
m = json.loads(baseline.read_text())

def evidence(file, scope, starts=None, level='平台实际运行'):
    """读取通过报告，提取指定断言与适用范围，并保存源文件哈希。"""
    p = out / file
    d = json.loads(p.read_text())
    assert d.get('status', d.get('Status', 'passed' if d.get('failed') == 0 else None)) == 'passed', file
    names = d.get('checks', d.get('Checks', [r['name'] for r in d.get('rows', []) if r['status'] == 'passed']))
    if starts:
        names = [n for n in names if any((n.startswith(prefix) for prefix in starts))]
    assert names, file
    return dict(path=str(p.relative_to(root)), sha256=hashlib.sha256(p.read_bytes()).hexdigest(), assertions=names, scope=scope, level=level)
joint = evidence('focused-integrity/results.json', '真实暂存、回滚和提交；HTTP目录/下载为替身；两个根和两个可变前置、循环、精确锁定冲突、取消。', ['joint_'], '替身运行（文件事务实际执行）')
module = evidence('focused-integrity/results.json', '本地JDK生成并读取的7组模块对照；两类加载器嵌套模块去重、目标Java8/21差异、损坏/重复拒绝。', ['jdk_module_', 'module_descriptor_', 'forge_and_neoforge_'], '平台实际运行（JDK与构造JAR）')
entry = evidence('focused-integrity/results.json', '真实编译Java类、校验缓存和版本文件：缺失、重复、Windows限定入口拒绝，schema2往返；tweaker仅拒绝分支，不等于正向游戏验收。', ['custom_'], '替身运行（真实文件与Java类）')
packs = evidence('final-custom-packs.json', '最终Host导入MultiMC、HMCL，mrpack导出重导；三个真实1.21.1进程执行自定义类并初始化图形。本地编译入口由SHA1缓存供给；非公开入口下载、非世界保存验收。')
resources = evidence('release-resources.json', '公开Modrinth真实Fabric API下载，批量准备/放弃/提交、旧文件保留、启停和会话恢复。')
loaders = evidence('final-loaders.json', 'Forge52.1.20和NeoForge21.1.264的1.21.1渲染启动；NeoForge真实Sodium下载和5个JarJar嵌套选择。渲染后观察5秒，无世界保存验收。')
ms = evidence('final-microsoft.json', '当前已有微软账户的钥匙串恢复、正版验证、只读皮肤、游戏渲染及幂等启动；未使用第二账号。')
browser = evidence('release-browser.json', '离线账户切换、公开玩家皮肤获取/保存/刷新、皮肤导入/导出和重启恢复；并非微软皮肤上传。')
changes = {'RES-007': ('批量更新入口新增多根联合回溯，锁定用户选择的根版本，并用实际JAR校验候选组合。', '仍缺Quilt完整path/pattern/version覆盖、Fabric schema0/依赖覆盖/自定义插件；单项安装入口仍沿用既有规划器，不等于所有入口全局最优求解。', [joint, resources]), 'RES-009': ('JarJar按目标Java读取显式module-info及Multi-Release描述符，支持manifest续行和文件名回退；纯字节读取位于Domain。', '仍缺语言插件、NeoForge provides等全部TOML语义及历史加载器取舍；当前模块读取不是完整JVM class校验器。', [module, loaders]), 'RES-015': ('依赖保护复用改进后的JarJar模块身份；本轮实际启停及现有Quilt回滚分支通过。', '仍受Quilt完整覆盖、Fabric覆盖、语言插件等未实现语义限制。', [module, resources]), 'RES-016': ('批量更新已从逐根选依赖改为联合回溯，在暂存目录验证整套实际JAR，通过后才预览/提交；失败和取消不改现有文件。', '仍缺未实现的Quilt/Fabric覆盖及自定义语言插件语义；候选上限256组/每项目64版/待检查16384组，超限明确未判定，不称无解。', [joint, resources]), 'PACK-002': ('PCL扩展schema2保存自定义入口与tweaker声明；mrpack导出重导后真实1.21.1启动通过。', '整合包锁定清单未接入全根远程补全；Quilt/Fabric覆盖、语言插件和自定义客户端仍有缺口。schema2需0.34+，不声称其他启动器识别PCL扩展。', [entry, packs]), 'PACK-004': ('HMCL允许在官方基础元数据上添加经哈希校验库及自定义mainClass；实际Host安装与1.21.1渲染启动通过。', '自定义客户端、jarmods、外部安装器、任意继承链仍不支持；仅1.21.1构造包做本轮游戏启动，未广泛验证第三方真实整合包。', [entry, packs]), 'PACK-005': ('MultiMC自定义组件新增mainClass与有序+tweakers，入口须存在于Mac适用的校验库；实际安装、mrpack往返和1.21.1渲染启动通过。', '自定义客户端、jarmods、外部安装器、未覆盖patch字段仍缺；+tweakers正向LaunchWrapper游戏未实测；order>=100、校验库等受限语义继续明确拒绝不支持输入。', [entry, packs])}
for f in m['features']:
    ev = []
    if f['id'] in changes:
        (summary, remaining, ev) = changes[f['id']]
        f['implementationSummary'] = f.get('implementationSummary', '') + ' 0.34：' + summary
        f['remaining'] = remaining
        f['audit034'] = dict(implementationGap=True, workKind='实现缺口', implementedThisRelease=summary, nextAction=remaining, evidence=ev)
        if f['id'].startswith('RES-'):
            extra = 'src/Pcl.Core/JointResourcePlanner.cs; src/Pcl.Core/JarModuleIdentity.cs; src/Pcl.Domain/JavaModuleDescriptor.cs'
            f['macImplementation'] += '; ' + extra
    elif f['id'] in ['AUTH-001', 'AUTH-002', 'AUTH-004', 'AUTH-005', 'AUTH-010', 'AUTH-013', 'RES-001', 'RES-006']:
        ev = [ms] if f['id'] in ['AUTH-001', 'AUTH-002'] else [browser]
        if f['id'] == 'AUTH-010':
            ev = [ms, browser]
        f['audit034'] = dict(implementationGap=f['audit033']['implementationGap'], workKind=f['audit033']['workKind'], implementedThisRelease='本轮保持现有行为，未新增此功能范围。', nextAction=f['remaining'], evidence=ev)
    for e in ev:
        if e['path'] not in f['testEvidence']:
            f['testEvidence'].append(e['path'])
m['evidenceReconciliation']['version'] = '0.34'
m['evidenceReconciliation']['completedImplementationThisRelease'] = []
m['evidenceReconciliation']['completedSubcapabilitiesThisRelease'] = ['批量更新多根联合回溯', 'JarJar显式模块与Multi-Release身份', '经校验自定义mainClass及schema2往返']
m['evidenceReconciliation']['currentReleaseTestedFeatures'] = [f['id'] for f in m['features'] if 'audit034' in f]
m['evidenceReconciliation']['note'] = '0.33逐项审查基线保留；0.34补齐3组子能力并记录7个相关部分实现条目，35个宽口径实现缺口仍未整项清零。'
path.write_text(json.dumps(m, ensure_ascii=False, indent=2) + '\n')
(out / 'matrix-final.json').write_bytes(path.read_bytes())
lines = ['# PCL Mac 0.34 剩余清单', '', '本轮补齐三组实际子能力：联合根依赖回溯、JarJar模块身份、自定义整合包入口及往返。7个相关宽口径条目仍有明确子缺口，不把子能力完成算成整项完成。', '', '**42项已覆盖限定范围、35项实现缺口、46项定向验收、9项外部条件验收**。分类沿用0.33逐项审计；本轮新增证据保存在audit034，audit033作为历史原样保留。', '', '## 本轮落实', '', '| 条目 | 实际新增 |', '|---|---|']
for (fid, (summary, _, _)) in changes.items():
    lines.append('| ' + fid + ' | ' + summary + ' |')
for kind in ['实现缺口', '定向验收', '外部条件验收']:
    lines += ['', '## ' + kind, '', '| ID | 功能 | 仍需完成 | 额外条件 |', '|---|---|---|---|']
    for f in m['features']:
        if f['audit033']['workKind'] == kind:
            lines.append('| ' + f['id'] + ' | ' + f['name'] + ' | ' + f['remaining'].replace('|', '／') + ' | ' + ('是' if f['audit033']['requiresExternalCondition'] else '否') + ' |')
lines += ['', '下一步优先：Quilt path/pattern/version覆盖与独立官方对照 → Fabric覆盖/schema0 → 整合包锁定清单的联合补全和自定义客户端边界 → 配置文件三方合并。其余功能不因本轮测试通过自动升级状态。']
(root / 'docs/0.34-剩余清单.md').write_text('\n'.join(lines) + '\n')
print('Updated 7 partial rows; 3 subcapabilities; complete-row counts unchanged.')
