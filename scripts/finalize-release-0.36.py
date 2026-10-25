# -*- coding: utf-8 -*-
"""Gate 0.36 delivery on scoped runtime evidence and verify the signed archive byte for byte."""
from pathlib import Path
import json, hashlib, shutil, subprocess, fcntl, html
root = Path(__file__).resolve().parents[1]
out = root/'artifacts/verification-0.36'
app = root/'artifacts/PCL Mac 0.36 Architecture.app'
mac = app/'Contents/MacOS'
def sha(p):
    result=hashlib.sha256()
    with p.open('rb') as f:
        for block in iter(lambda:f.read(1024*1024),b''): result.update(block)
    return result.hexdigest()
records = [
 ('Iris归属、共享依赖与强制退出恢复', 'final-environment-tests-v3/results.json', '22项；真实文件/ZIP/子进程，HTTP替身'),
 ('相关资源与组件流程', 'final-transactions/results.json', '38项定向测试'),
 ('最终包原生卸载确认', 'final-environment-ui.json', '平台实际运行；真实按钮自动驱动'),
 ('真实共享依赖与卸载后启动', 'final-shared-launch.json', '平台实际运行；Sodium Extra / Sodium'),
 ('独占环境卸载与启动', 'final-exclusive-launch.json', '平台实际运行；3个组件、配置/光影/存档保持'),
 ('光影世界保存及独立进程重进', 'world-session/accepted.json', '早期0.36候选包；两次退出0及用户视觉确认'),
 ('原始存档保持', 'original-world-preserved.json', '22文件哈希与原快照一致'),
 ('公开皮肤和账户', 'final-browser.json', '29项平台实际运行'),
 ('既有微软账户', 'final-microsoft.json', '7项平台实际运行；一个账户'),
 ('托管游戏窗口恢复', 'final-managed.json', '平台实际运行；测试主动停止137'),
 ('游戏独立运行', 'final-detached/packaged-detached-verification.json', '平台实际运行；Host退出后渲染并观察5秒'),
 ('项目引用与符号守卫', 'final-guard.json', '编译与静态边界检查'),
 ('最终Host代码连续性', 'host-continuity.json', '测试后内嵌矩阵更新，Host逐文件一致')
]
results=[]
for title, name, scope in records:
    p=out/name; d=json.loads(p.read_text())
    status=d.get('status', d.get('Status', 'passed' if d.get('failed')==0 else None))
    if name=='final-guard.json': assert not d.get('violations', d.get('Violations', [])), d
    else: assert status=='passed', (name,d)
    results.append(dict(title=title,path=name,scope=scope,assertions=len(d.get('checks',d.get('Checks',d.get('rows',[])))),sha256=sha(p)))
assert len(json.loads((out/'final-environment-tests-v3/results.json').read_text())['rows'])==22
assert json.loads((out/'world-session/accepted.json').read_text())['userVisualAcceptance']=='confirmed-first-and-second'
matrix=json.loads((root/'docs/PCL2完整功能矩阵.json').read_text())
assert len(matrix['features'])==132 and sum(f['audit033']['workKind']=='实现缺口' for f in matrix['features'])==35
assert not any(k in json.dumps(matrix) for k in ['subProgress','subCapabilities'])
for f in matrix['features']:
    for audit in ['audit033','audit034','audit035','audit036']:
        for e in f.get(audit,{}).get('evidence',[]) + f.get(audit,{}).get('newEvidence',[]):
            assert sha(root/e['path'])==e['sha256'],e['path']
locks=[]
for data in [root/'runtime/apple-silicon-1.21.1', *out.glob('*data'),out/'debug-invalid-args']:
    p=data/'.launcher.lock'
    if p.exists():
        with p.open('r+b') as f:
            fcntl.flock(f, fcntl.LOCK_EX|fcntl.LOCK_NB);fcntl.flock(f,fcntl.LOCK_UN)
        locks.append(str(data.relative_to(root)))
active=[x for x in subprocess.check_output(['ps','-axo','pid,ppid,comm'],text=True).splitlines() if 'PCL Mac 0.36 Architecture.app/Contents/MacOS/' in x]
assert not active,active
(out/'release-cleanup.json').write_text(json.dumps(dict(status='passed',rootLocksReacquired=locks,releaseProcessesRemaining=[]),indent=2))
summary=dict(status='passed',version='0.36.0',featureRows=132,implementationGapRows=35,wholeFeatureRowsClosed=0,matrixSubprogressAdded=False,fullParityClaimed=False,tests=results)
(out/'verification-summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2))
review=out/'review';review.mkdir(exist_ok=True)
proofs=''.join('<tr><td>'+html.escape(r['title'])+'</td><td>'+html.escape(r['scope'])+'</td><td><a href="../'+r['path']+'">'+str(r['assertions'] or '记录')+'</a></td></tr>' for r in results)
features=''.join('<tr><td>'+f['id']+'</td><td>'+html.escape(f['name'])+'</td><td>'+html.escape(f['audit033']['workKind'])+'</td><td>'+html.escape(f['remaining'])+'</td></tr>' for f in matrix['features'])
(review/'index.html').write_text('''<!doctype html><html lang="zh"><meta charset="utf-8"><title>PCL Mac 0.36 验收</title><style>body{font:16px/1.65 system-ui;margin:40px auto;max-width:1100px;padding:0 24px;background:#f2f5fc;color:#182434}h1{color:#345ccc}table{border-collapse:collapse;width:100%;background:white}td,th{padding:12px;border:1px solid #dce2ee;text-align:left}img{max-width:760px;width:100%}a{color:#2856b8}</style><h1>PCL Mac 0.36 · Iris 环境卸载</h1><p>真实归属、共享依赖保护、原生预览确认与崩溃续做。132行矩阵保持原分类，35项宽口径实现缺口；不增加子进度。</p><p>指定光影组合两次独立进程正常保存退出；用户确认画面、标记与位置正常。世界验收来自早期0.36候选包，最终包另测卸载/启动/登录/皮肤/独立运行，二进制来源分别留证。</p><h2>真实确认窗口</h2><img src="../iris-confirmation.png" alt="Iris卸载确认，移除Iris及独占API，保留Sodium与Sodium Extra"><h2>实测证据</h2><table><tr><th>项目</th><th>范围</th><th>检查/记录</th></tr>'''+proofs+'''</table><h2>仍有限制</h2><p>旧版和手动环境不追认归属；自定义Fabric依赖覆盖暂拒绝卸载；Oculus/Forge/NeoForge环境卸载未实现。仅本机指定组合获世界验收。包为Apple Silicon自包含、ad-hoc签名，未公证。</p><h2>逐项剩余清单</h2><table><tr><th>ID</th><th>功能</th><th>原分类</th><th>剩余范围</th></tr>'''+features+'</table></html>')
for p in [root/'docs/0.36-Iris环境卸载与世界验收.md',root/'docs/0.36-剩余清单.md',root/'docs/支持范围.md',root/'docs/PCL2完整功能矩阵.json',out/'verification-summary.json']:
    shutil.copy2(p,app/'Contents/Resources'/p.name)
tested={str(p.relative_to(mac)):sha(p) for p in mac.rglob('*') if p.is_file()}
(out/'final-tested-code.json').write_text(json.dumps(tested,indent=2))
before=out/'apphost-before-signing';after=out/'apphost-after-signing'
shutil.copy2(mac/'PCL-MacPort',before)
subprocess.run(['codesign','--remove-signature',str(before)],check=True,capture_output=True)
subprocess.run(['codesign','--force','--sign','-',str(app)],check=True,capture_output=True)
shutil.copy2(mac/'PCL-MacPort',after)
subprocess.run(['codesign','--remove-signature',str(after)],check=True,capture_output=True)
assert before.read_bytes()==after.read_bytes()
assert all(sha(mac/n)==v for n,v in tested.items() if n!='PCL-MacPort')
subprocess.run(['codesign','--verify','--deep','--strict',str(app)],check=True,capture_output=True)
archive=root/'artifacts/PCL-Mac-0.36-arm64.zip'
subprocess.run(['ditto','-c','-k','--sequesterRsrc','--keepParent',str(app),str(archive)],check=True)
extract=out/'delivery-extract';extract.mkdir(exist_ok=True)
subprocess.run(['ditto','-x','-k',str(archive),str(extract)],check=True)
restored=extract/app.name
files=[p for p in app.rglob('*') if p.is_file()]
assert len(files)==sum(p.is_file() for p in restored.rglob('*'))
assert all(sha(p)==sha(restored/p.relative_to(app)) for p in files)
subprocess.run(['codesign','--verify','--deep','--strict',str(restored)],check=True,capture_output=True)
delivery=dict(status='passed',version='0.36.0',zip=str(archive),bytes=archive.stat().st_size,sha256=sha(archive),matchingExtractedFiles=len(files),testedPayloadsPreservedExceptApphostSignature=len(tested)-1,mainApphostUnsignedPayloadIdentical=True,signing='ad-hoc, not notarized')
(out/'delivery.json').write_text(json.dumps(delivery,ensure_ascii=False,indent=2))
print(json.dumps(delivery,ensure_ascii=False))
