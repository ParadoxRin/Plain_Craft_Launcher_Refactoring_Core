# -*- coding: utf-8 -*-
"""Attach bounded 0.36 evidence; preserve row statuses and historical work classifications."""
from pathlib import Path
import json, hashlib, collections
root = Path(__file__).resolve().parents[1]
path = root / 'docs/PCL2完整功能矩阵.json'
matrix = json.loads(path.read_text())
before = [(f['id'], f['status'], f['audit033']['workKind']) for f in matrix['features']]
def proof(name, scope):
    p = root / 'artifacts/verification-0.36' / name
    d = json.loads(p.read_text())
    assert d.get('status', d.get('Status')) == 'passed' or d.get('failed') == 0, name
    return dict(path=str(p.relative_to(root)), sha256=hashlib.sha256(p.read_bytes()).hexdigest(), scope=scope)
proofs = [
    proof('final-environment-tests-v3/results.json', '22项文件、真实ZIP元数据与进程强制退出恢复定向测试；HTTP下载为替身'),
    proof('final-environment-ui.json', '11项真实Avalonia按钮、取消/确认；真实Sodium Extra保留共享Sodium；配置/包/存档逐文件不变'),
    proof('final-shared-launch.json', '真实共享环境卸载后启动、渲染、运行中互斥和测试进程清理7项'),
    proof('final-exclusive-launch.json', '真实独占环境三组件卸载、保留用户数据与启动渲染10项'),
    proof('world-session/accepted.json', '本轮早期0.36候选包两次独立游戏进程进入世界、正常保存退出18项；用户确认光影正常、标记和位置保留；测试时二进制哈希另存')
]
changes = {
 'RES-010': ('新增Fabric/Iris环境归属账本、实际依赖与用途保护、预览确认及可恢复卸载；光影世界两进程保存重进获用户确认。', 'Iris环境卸载仅限有可信安装前记录且文件未被修改的环境；旧版/手动安装不自动追认。自定义Fabric依赖覆盖暂拒绝卸载。Forge/NeoForge光影后端自动搭建与Oculus环境卸载仍未实现。'),
 'RES-011': ('Iris 1.8.8、Fabric 0.19.5、Minecraft 1.21.1、Complementary r5.9.3在Apple Silicon上完成进入世界、保存、独立进程重进及用户视觉确认。', '验收仅覆盖指定组合与本机，不推广至任意光影、加载器或所有Mac；保留新生物群系/uniform日志警告及长期稳定性待验收。')
}
for f in matrix['features']:
    if f['id'] not in changes: continue
    summary, remaining = changes[f['id']]
    f['audit036'] = dict(workKind=f['audit033']['workKind'], implementationGap=f['audit033']['implementationGap'], implementedThisRelease=summary, nextAction=remaining, evidence=proofs)
    if '0.36：' not in f.get('implementationSummary', ''):
        f['implementationSummary'] = f.get('implementationSummary', '') + ' 0.36：' + summary
    f['remaining'] = remaining
    for p in proofs:
        if p['path'] not in f['testEvidence']: f['testEvidence'].append(p['path'])
assert before == [(f['id'], f['status'], f['audit033']['workKind']) for f in matrix['features']]
assert not any(k in json.dumps(matrix) for k in ['subProgress', 'subCapabilities'])
path.write_text(json.dumps(matrix, ensure_ascii=False, indent=2)+'\n')
text = '# PCL Mac 0.36 · 剩余清单\n\n保持132行、42项已覆盖限定范围、35项实现缺口、46项定向验收、9项外部条件验收。没有新增矩阵子进度，也没有通过改状态或分类减少缺口。\n\n本轮真正完成：有安装归属的Fabric/Iris环境安全卸载、共享组件保留、原生预览确认及指定光影组合的世界保存重进。RES-010/011记录限定范围证据；Oculus/Forge/NeoForge光影、旧环境追认与依赖覆盖配置继续待办。\n\n'
for kind in ['实现缺口', '定向验收', '外部条件验收']:
    text += '## '+kind+'\n\n| ID | 功能 | 下一步 |\n|---|---|---|\n'
    for f in matrix['features']:
        if f['audit033']['workKind'] == kind:
            text += '| '+f['id']+' | '+f['name']+' | '+f['remaining'].replace('|','／').replace('\n',' ')+' |\n'
    text += '\n'
(root/'docs/0.36-剩余清单.md').write_text(text)
print(dict(collections.Counter(f['audit033']['workKind'] for f in matrix['features'])))
