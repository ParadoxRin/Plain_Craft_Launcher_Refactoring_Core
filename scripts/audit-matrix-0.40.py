# -*- coding: utf-8 -*-
"""核对已有记录，不运行产品测试，不把缺口升级为完成。"""
import collections
import hashlib
import json
from pathlib import Path
import re

root = Path(__file__).resolve().parent.parent
docs = root / 'docs'
path = docs / 'PCL2完整功能矩阵.json'
matrix = json.loads(path.read_text())
features = matrix['features']
md_path = docs / 'PCL2完整功能矩阵.md'
old_md = md_path.read_text()
md_states = {p[1].strip(): p[3].strip() for line in old_md.splitlines()
             if re.match(r'\| [A-Z]+-\d+', line) for p in [line.split('|')]}
overrides_path = docs / 'feature-status-overrides.json'
overrides = json.loads(overrides_path.read_text())
before = dict(collections.Counter(f['status'] for f in features))
md_drift = [f['id'] for f in features if md_states.get(f['id']) != f['status']]
override_drift = [f['id'] for f in features if overrides.get(f['id'], {}).get('status') != f['status']]
corrections = []
for feature in features:
    desired = feature['status']
    reason = feature['remaining']
    if feature['id'] in ('NET-012', 'RES-004', 'RES-010', 'UI-014'):
        assert feature['audit033']['implementationGap']
        desired = '部分实现'
    if feature['id'] == 'AUTH-013':
        desired = '已实现待验收'
        reason = '归一过时的本轮功能运行标签；保留实际证据，不升级完整验收。'
    if desired != feature['status']:
        corrections.append(dict(id=feature['id'], before=feature['status'], after=desired, reason=reason))
        feature['status'] = desired

counts = dict(collections.Counter(f['status'] for f in features))
kinds = dict(collections.Counter(f['audit033']['workKind'] for f in features))
references = set()
hashes = {}
rows = []
for feature in features:
    paths = set()
    for value in feature.get('testEvidence', []):
        # 老记录把路径与中文说明混在一起；不可把说明误算为文件名。
        match = re.match(r'^((?:artifacts|docs|tests|src)/.*?\.(?:json|md|cs|vb|png|txt))(?:$|[#：:（\s])', value)
        if match:
            paths.add(match[1])
    for key, value in feature.items():
        if key.startswith('audit') and isinstance(value, dict):
            for item in value.get('evidence', []) + value.get('newEvidence', []):
                if isinstance(item, dict) and item.get('path'):
                    paths.add(item['path'])
                    if item.get('sha256'):
                        hashes[item['path']] = item['sha256']
    references.update(paths)
    rows.append(dict(id=feature['id'], name=feature['name'], status=feature['status'],
                     workKind=feature['audit033']['workKind'], remaining=feature['remaining'],
                     existingEvidencePaths=sorted(p for p in paths if (root / p).is_file()),
                     missingEvidencePaths=sorted(p for p in paths if not (root / p).is_file())))
missing = sorted(p for p in references if not (root / p).is_file())
available_hashes = {p: h for p, h in hashes.items() if (root / p).is_file()}
mismatches = [p for p, h in available_hashes.items() if hashlib.sha256((root / p).read_bytes()).hexdigest() != h]
review = dict(date='2026-10-27', sourceVersion='0.40',
              reviewKind='记录、口径和引用核对，不是新增运行验收',
              activeFeatures=len(features), statusCounts=counts, workKinds=kinds,
              report='docs/0.40-功能矩阵核对.md')
matrix['currentMatrixReview'] = review
path.write_text(json.dumps(matrix, ensure_ascii=False, indent=2) + '\n')
for feature in features:
    overrides.setdefault(feature['id'], {}).update({k: v for k, v in feature.items()
        if k not in ('id', 'domain', 'name', 'sourceRoot', 'sourceEntry')})
overrides.pop('UI-015', None)
overrides_path.write_text(json.dumps(overrides, ensure_ascii=False, indent=2) + '\n')

def cell(value):
    return str(value).replace('|', '／').replace('\n', ' ')

header = old_md.split('| ID |')[0].rstrip()
notice = '当前标签已与JSON同步；历史限定验收不等于0.40全量复测。详见[统计核对](0.40-功能矩阵核对.md)。'
if notice not in header:
    header += '\n\n' + notice
lines = ['| ID | 功能 | 状态 | Mac 实现 / 界面 | 验收 / 剩余 |', '|---|---|---|---|---|']
for feature in features:
    lines.append('| ' + ' | '.join(map(cell, [feature['id'], feature['domain'] + ' · ' + feature['name'],
        feature['status'], feature['macImplementation'] + ' / ' + feature['ui'],
        '; '.join(feature['testEvidence']) + ' ' + feature['remaining']])) + ' |')
md_path.write_text(header + '\n\n' + '\n'.join(lines) + '\n')

audit = dict(review=review, beforeStatusCounts=before, statusCorrections=corrections,
             markdownStatusDriftFixed=md_drift, overrideStatusDriftFixed=override_drift,
             references=dict(total=len(references), existing=len(references)-len(missing),
                 missing=len(missing), missingPaths=missing, existingHashedFilesChecked=len(available_hashes),
                 hashMismatches=mismatches), features=rows)
# 保留首次核对的变更依据；重复执行不覆盖成“无变更”的历史。
audit_path = docs / '0.40-功能矩阵核对.json'
if audit_path.exists():
    previous = json.loads(audit_path.read_text())
    for key in ('beforeStatusCounts', 'statusCorrections', 'markdownStatusDriftFixed', 'overrideStatusDriftFixed'):
        audit[key] = previous[key]
audit_path.write_text(json.dumps(audit, ensure_ascii=False, indent=2) + '\n')
print(json.dumps(dict(statusCounts=counts, workKinds=kinds, references=audit['references'] | {'missingPaths':len(missing)}), ensure_ascii=False))
