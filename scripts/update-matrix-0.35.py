# -*- coding: utf-8 -*-
"""Only attach verified 0.35 scope/evidence. Do not change statuses, work kinds or add subprogress."""
from pathlib import Path
import json, hashlib, collections
root = Path(__file__).resolve().parents[1]
path = root / 'docs/data/PCL2完整功能矩阵.json'
matrix = json.loads(path.read_text())
before = [(x['id'], x['status'], x['audit033']['workKind']) for x in matrix['features']]

def evidence(relative, scope):
    p = root / 'artifacts/verification-0.35' / relative
    value = json.loads(p.read_text())
    assert value.get('status', value.get('Status')) == 'passed' or value.get('failed') == 0, relative
    return dict(path=str(p.relative_to(root)), sha256=hashlib.sha256(p.read_bytes()).hexdigest(), scope=scope)

txn = evidence('final-transactions/results.json', '38项真实文件/子进程中断/相关组件与资源包定向用例；不等于世界画面验收')
shader = evidence('final-shaders.json', '真实Iris 1.8.8、Complementary r5.9.3配置撤销/包卸载、3次渲染启动和Host启动阻断；未验收世界效果')
copy = evidence('final-copy-ui.json', '13项原生Avalonia按钮事件：取消、路径剪贴板/Finder、实际创建、来源、偏好与源变更拒绝')
guards = evidence('final-copy-guards.json', '5项Host重开、偏好、路径占用拒绝及新副本真实渲染启动')
changes = {
    'RES-010': ('配置应用/撤销和受管理光影包卸载已使用预写持久化意图、校验备份与恢复检查；配置原子替换，冲突不覆盖，未收尾事务阻止启动。',
                'Iris等光影环境卸载仍未实现；Forge/NeoForge光影后端未自动搭建。本轮未作进入世界后的视觉、保存和重进验收。旧版无备份哈希的激活记录拒绝自动撤销，保留备份。', [txn, shader]),
    'RES-011': ('Host按实际扫描到的Iris/Oculus元数据、MC版本及加载器提供提示；未检测到不推断后端。',
                '仅指定组合有历史人工世界验收，不能推广到任意光影和所有Mac；本轮真实包仍有新版本uniform兼容警告。', [txn, shader]),
    'INST-009': ('Host预览固定副本身份与准确路径，原生确认弹窗支持路径复制/打开、取消、抑制提示；提交重验源指纹和占用，副本持久化来源。',
                 '仍不支持任意组合组件原位修改或模组自动适配。', [copy, guards, txn]),
    'VER-016': ('原生界面实际创建加载器副本并启动；路径占用和源变化被拒绝，提示偏好跨Host重开保留。',
                 '仍为新副本重建组件；不支持任意组合原位修改或模组自动适配。本轮是自动驱动真实UI事件，不是人工逐页验收。', [copy, guards, txn])
}
for feature in matrix['features']:
    if feature['id'] not in changes:
        continue
    summary, remaining, proofs = changes[feature['id']]
    feature['audit035'] = dict(implementationGap=feature['audit033']['implementationGap'], workKind=feature['audit033']['workKind'], implementedThisRelease=summary, nextAction=remaining, evidence=proofs)
    if '0.35：' not in feature.get('implementationSummary', ''):
        feature['implementationSummary'] = feature.get('implementationSummary', '') + ' 0.35：' + summary
    feature['remaining'] = remaining
    for proof in proofs:
        if proof['path'] not in feature['testEvidence']:
            feature['testEvidence'].append(proof['path'])
assert before == [(x['id'], x['status'], x['audit033']['workKind']) for x in matrix['features']]
assert not any(k in json.dumps(matrix) for k in ['subProgress', 'subCapabilities'])
path.write_text(json.dumps(matrix, ensure_ascii=False, indent=2) + '\n')
counts = collections.Counter(x['audit033']['workKind'] for x in matrix['features'])
text = '# PCL Mac 0.35 · 剩余清单\n\n'
text += '沿用0.34的宽口径：42项已覆盖限定范围、35项实现缺口、46项定向验收、9项外部条件验收。本轮不改状态、不改分类、不增加子进度；四行新增可核对的限定范围证据。\n\n'
text += '本轮落地：配置撤销安全事务、受管理光影包事务卸载、Host固定路径和真实副本确认、实际后端说明。Iris等环境卸载单独保留。\n\n'
for kind in ['实现缺口', '定向验收', '外部条件验收']:
    text += '## ' + kind + '\n\n| ID | 功能 | 下一步 |\n|---|---|---|\n'
    for f in matrix['features']:
        if f['audit033']['workKind'] == kind:
            text += '| ' + f['id'] + ' | ' + f['name'] + ' | ' + f['remaining'].replace('|', '／').replace('\n', ' ') + ' |\n'
    text += '\n'
(root / 'docs/【已弃用】0.35-剩余清单.md').write_text(text)
print(dict(counts))
