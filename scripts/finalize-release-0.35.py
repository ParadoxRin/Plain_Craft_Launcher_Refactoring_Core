# -*- coding: utf-8 -*-
"""Gate delivery on scoped evidence, preserve matrix classifications, and verify the signed ZIP payload."""
from pathlib import Path
import json, hashlib, shutil, subprocess, fcntl, html
root = Path(__file__).resolve().parents[1]
out = root / 'artifacts/verification-0.35'
app = root / 'artifacts/PCL Mac 0.35 Architecture.app'
macos = app / 'Contents/MacOS'

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

records = [
    ('事务/强制退出恢复及受影响资源包与组件', 'final-transactions/results.json', '真实文件及子进程；部分安装器为替身'),
    ('Iris配置撤销、包卸载、启动阻断及渲染', 'final-shaders.json', '平台实际运行；未作世界视觉验收'),
    ('原生副本确认及实际复制', 'final-copy-ui.json', '平台实际运行；真实UI事件自动驱动'),
    ('Host重开、占用保护及新副本启动', 'final-copy-guards.json', '平台实际运行'),
    ('公开皮肤/账户保存刷新重开', 'final-browser.json', '平台实际运行'),
    ('微软账户、正版验证、只读皮肤与启动', 'final-microsoft.json', '平台实际运行；现有一个账户'),
    ('最终包资源/皮肤/账户UI及无后端提示', 'final-browser-ui.json', '平台实际运行'),
    ('托管游戏与窗口恢复', 'final-managed.json', '平台实际运行；主动停止退出137'),
    ('窗口和Host退出后的独立游戏', 'final-detached/packaged-detached-verification.json', '平台实际运行；渲染后观察5秒'),
    ('最终Host代码连续性', 'host-continuity.json', '逐文件SHA-256核对'),
    ('项目引用与符号守卫', 'final-guard.json', '编译与静态边界检查')
]
results = []
for title, name, state in records:
    path = out / name
    value = json.loads(path.read_text())
    status = value.get('status', value.get('Status', 'passed' if value.get('failed') == 0 else None))
    if name == 'final-guard.json':
        assert value.get('violations') == [] or value.get('Violations') == [] or status == 'passed', value
    else:
        assert status == 'passed', (name, status)
    results.append(dict(title=title, path=name, state=state, assertions=len(value.get('checks', value.get('Checks', value.get('rows', [])))), sha256=sha(path)))

matrix = json.loads((root / 'docs/PCL2完整功能矩阵.json').read_text())
assert len(matrix['features']) == 132
assert sum(x['audit033']['workKind'] == '实现缺口' for x in matrix['features']) == 35
for feature in matrix['features']:
    for audit in ['audit033', 'audit034', 'audit035']:
        for item in feature.get(audit, {}).get('evidence', []) + feature.get(audit, {}).get('newEvidence', []):
            assert sha(root / item['path']) == item['sha256'], item['path']

# Only test-owned roots and this release's processes are part of cleanup; do not terminate unrelated user programs.
locks = []
for data in [root / 'runtime/apple-silicon-1.21.1', *out.glob('*data')]:
    path = data / '.launcher.lock'
    if path.exists():
        with path.open('r+b') as stream:
            fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
            fcntl.flock(stream, fcntl.LOCK_UN)
        locks.append(str(data.relative_to(root)))
active = [line for line in subprocess.check_output(['ps', '-axo', 'pid,ppid,comm'], text=True).splitlines() if 'PCL Mac 0.35 Architecture.app/Contents/MacOS/' in line]
assert not active, active
(out / 'release-cleanup.json').write_text(json.dumps(dict(status='passed', rootLocksReacquired=locks, releaseProcessesRemaining=[]), indent=2))
summary = dict(status='passed', version='0.35.0', featureRows=132, implementationGapRows=35, wholeFeatureRowsClosed=0, matrixSubprogressAdded=False, fullParityClaimed=False, tests=results)
(out / 'verification-summary.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2))

rows = '\n'.join('| ' + x['title'] + ' | ' + x['state'] + ' | ' + str(x['assertions'] or '见原记录') + ' | `' + x['path'] + '` |' for x in results)
doc = root / 'docs/0.35-光影事务与副本确认验收.md'
doc.write_text('''# PCL Mac 0.35 · 光影事务与副本确认

## 实际交付

- **A 撤销应用配置**：ActivationTransaction 在修改前保存原配置/新配置、哈希及操作身份，预写提交意图后以同目录原子替换提交。撤销也有独立意图与完成回执，进程重开可续做；当前内容不匹配前态或后态时拒绝恢复。仅处理 iris.properties/options.txt，不卸载包或环境组件。
- **B 卸载受管理光影包**：ShaderRemovalTransaction 在动文件前检查整批所有权和哈希；备份包与前后清单并刷盘后才提交。最多128包，恢复时先检查整批冲突，再处理文件；不删除目录、不移除 Iris、不删除用户新增文件。混合资源批次需单独选择光影包。
- **启动保护**：LaunchService 准备阶段先处理未收尾事务，冲突直接使 game.launch 失败；Host 原运行中互斥继续阻止配置/文件变更。提交前可取消，提交后只完成小范围收尾；恢复记录独立于尽力写入的 ResourceHistory。
- **副本确认**：Host 预览阶段固定目标身份，回传原目录/准确目标路径。界面提供路径复制、打开原目录和目标父目录；确认后复用原组件安装、数据复制、指纹与占用校验。取消不创建目标实例、不修改源实例、不开始安装；保留预览记录和实例容器目录。偏好只略过说明，不绕过校验。
- **副本来源与提示**：副本内保留来源记录并在实例详情显示；成功后可打开实际目标。Host 按实际 Iris/Oculus 元数据、MC版本和加载器返回说明，未检测到不推断；不统一要求移除 Oculus。

## 本轮证据

证据目录：`artifacts/verification-0.35/`。摘要及每个报告的SHA-256见 verification-summary.json。

| 项目 | 证据层级 | 检查数 | 原始报告 |
|---|---|---:|---|
''' + rows + '''

- 38项定向测试包含真实子进程在预写意图、目标修改、回执写入三个阶段强制退出；随后新进程恢复，另测批量冲突、备份损坏、取消、原配置不存在和用户新增文件。复用资源包和组件迁移用例通过。没有模拟硬件断电或文件系统损坏。
- 真实Iris 1.8.8 / Minecraft 1.21.1 / ComplementaryReimagined r5.9.3：配置应用/撤销逐字节恢复，包卸载保留 Iris 与用户文件；三次游戏渲染初始化和短时存活通过。游戏改写配置后撤销拒绝覆盖；未解决事务在真实 Host 阻止启动。
- 真实原生UI确认流程13项，以及Host重开/占用保护/副本启动5项通过。取消和确认后均校验源目录文件哈希不变。Finder路径动作、剪贴板、实际目标路径、来源与不再提示均已执行。
- 既有微软账户7项、公开皮肤与账户29项通过；没有新增第二个微软账户证据。钥匙串仍只由Host读取，凭据未复制到包、回执或报告。
- 托管模式实际安装/启动、隐藏与停止后窗口恢复；退出137是测试主动停止。独立模式关闭UI与Host后，确认游戏已渲染并继续运行5秒，再清理测试进程。
- 本轮发现并修复确认按钮被长路径挤出默认视口、全新数据目录不存在目标父目录两项问题。最终资源浏览页同时消除无后端时的固定猜测提示。初轮失败/调试记录不作为通过证据。
- 最后只重编Desktop提示和内嵌矩阵，Host/Client/Contract及其他代码文件逐文件一致；最终包另运行原生资源/皮肤UI。保留既有AVLN3001构造注入编译提示，真实窗口已运行。

## 明确保留的限制

本轮没有人工进入世界、保存及重进验收，不把渲染日志当成光影效果验收。真实光影日志有较新生物群系和uniform变量警告，保留兼容限制。旧版激活回执缺少备份哈希，不能自动补造信任；拒绝自动覆盖并保留原备份。

C“卸载光影环境”、任意Forge/NeoForge光影搭建、任意组件组合原位修改仍未实现。不新增子进度，不通过状态或支持范围调整关闭缺口；矩阵仍为35项宽口径实现缺口。详见 [剩余清单](0.35-剩余清单.md) 与 [支持范围](支持范围.md)。

应用为Apple Silicon自包含构建，ad-hoc签名，不是Developer ID公证版本。ZIP解压逐文件哈希和签名复验记录见 delivery.json；原版本及数据目录保留。
''')

review = out / 'review'
review.mkdir(exist_ok=True)
proofs = ''.join('<tr><td>' + html.escape(x['title']) + '</td><td>' + html.escape(x['state']) + '</td><td><a href="../' + x['path'] + '">原始报告</a></td></tr>' for x in results)
features = ''
for f in matrix['features']:
    entries = f.get('audit035', {}).get('evidence', [])
    links = ''.join('<p><a href="' + (root / e['path']).as_uri() + '">' + html.escape(e['scope']) + '</a></p>' for e in entries)
    features += '<tr><td>' + f['id'] + '</td><td>' + html.escape(f['name']) + '</td><td>' + f['audit033']['workKind'] + '</td><td>' + html.escape(f['remaining']) + links + '</td></tr>'
(review / 'index.html').write_text('''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>PCL Mac 0.35</title>
<style>body{font:16px system-ui;max-width:1250px;margin:35px auto;padding:24px;background:#eef3f8;color:#21324a}section{padding:25px;background:white;border-radius:14px;margin:20px 0}table{border-collapse:collapse;width:100%}td,th{border-bottom:1px solid #ddd;padding:12px;text-align:left;vertical-align:top}a{color:#1769b4}img{max-width:760px;width:100%}</style>
<h1>PCL Mac 0.35</h1><p>光影配置/包卸载安全事务 · 真实副本确认 · 现有登录与独立运行保留</p>
<section><strong>35项宽口径实现缺口仍保留；本轮没有新增矩阵子进度或通过分类关闭缺口。</strong><p>38项文件/进程定向检查；真实Iris流程26项；原生副本UI13项与Host重开/新副本启动5项。未作人工世界视觉验收；Iris环境卸载仍未实现。</p><img src="../copy-confirmation.png" alt="真实副本确认弹窗"></section>
<section><h2>本轮证据</h2><table><tr><th>项目</th><th>层级与限制</th><th>证据</th></tr>''' + proofs + '</table></section><section><h2>132项准确清单</h2><table><tr><th>ID</th><th>功能</th><th>原分类</th><th>剩余与本轮证据</th></tr>' + features + '</table></section></html>')

for p in [doc, root / 'docs/0.35-剩余清单.md', root / 'docs/支持范围.md', root / 'docs/PCL2完整功能矩阵.json', out / 'verification-summary.json']:
    shutil.copy2(p, app / 'Contents/Resources' / p.name)
# Attach docs, then sign. Only apphost signing bytes may change; all managed/native payloads remain tested bytes.
tested = {str(p.relative_to(macos)): sha(p) for p in macos.rglob('*') if p.is_file()}
(out / 'final-tested-code.json').write_text(json.dumps(tested, indent=2))
unsigned_before = out / 'apphost-before-signing'
unsigned_after = out / 'apphost-after-signing'
shutil.copy2(macos / 'PCL-MacPort', unsigned_before)
subprocess.run(['codesign', '--remove-signature', str(unsigned_before)], check=True, capture_output=True)
subprocess.run(['codesign', '--force', '--sign', '-', str(app)], check=True, capture_output=True)
shutil.copy2(macos / 'PCL-MacPort', unsigned_after)
subprocess.run(['codesign', '--remove-signature', str(unsigned_after)], check=True, capture_output=True)
assert unsigned_before.read_bytes() == unsigned_after.read_bytes()
for name, expected in tested.items():
    if name != 'PCL-MacPort':
        assert sha(macos / name) == expected, name
subprocess.run(['codesign', '--verify', '--deep', '--strict', str(app)], check=True, capture_output=True)
archive = root / 'artifacts/PCL-Mac-0.35-arm64.zip'
subprocess.run(['ditto', '-c', '-k', '--sequesterRsrc', '--keepParent', str(app), str(archive)], check=True)
extract = out / 'delivery-extract'
extract.mkdir(exist_ok=True)
subprocess.run(['ditto', '-x', '-k', str(archive), str(extract)], check=True)
restored = extract / app.name
files = [p for p in app.rglob('*') if p.is_file()]
assert len(files) == sum(p.is_file() for p in restored.rglob('*'))
assert all(sha(p) == sha(restored / p.relative_to(app)) for p in files)
subprocess.run(['codesign', '--verify', '--deep', '--strict', str(restored)], check=True, capture_output=True)
delivery = dict(status='passed', version='0.35.0', zip=str(archive), bytes=archive.stat().st_size, sha256=sha(archive), matchingExtractedFiles=len(files), testedPayloadsPreservedExceptApphostSignature=len(tested)-1, mainApphostUnsignedPayloadIdentical=True, signing='ad-hoc, not notarized')
(out / 'delivery.json').write_text(json.dumps(delivery, ensure_ascii=False, indent=2))
print(json.dumps(delivery, ensure_ascii=False))
