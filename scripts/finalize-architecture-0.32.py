"""Collect bounded evidence, sign the outer bundle, and verify an extracted delivery copy."""
from pathlib import Path
import fcntl, hashlib, html, json, shutil, subprocess
root=Path(__file__).resolve().parent.parent
r=root/'artifacts/architecture-0.32'
app=root/'artifacts/PCL Mac 0.32 Architecture.app'
records=[
 ('Contract-only Host 用例与自动重连','release-verified-shell.json','平台实际运行'),
 ('任务中心、网络保存与拖入 UI','release-verified-shell-ui/report.json','平台实际运行'),
 ('版本/加载器、设备码取消与本机联机','release-final-main.json','平台实际运行'),
 ('下载、加载器与联机 UI','release-main-ui/report.json','平台实际运行'),
 ('资源、离线账户与公开玩家皮肤','release-browser.json','平台实际运行'),
 ('资源和皮肤实际 UI','release-browser-ui/report.json','平台实际运行'),
 ('事件订阅与游戏重连','release-sessions.json','平台实际运行'),
 ('停止旧会话后重新启动 Host','release-shutdown.json','平台实际运行'),
 ('微软恢复、正版验证与皮肤/游戏启动','release-microsoft.json','平台实际运行'),
 ('托管游戏隐藏、停止与恢复窗口','release-verified-managed.json','平台实际运行'),
 ('关闭应用后游戏独立运行','release-verified-detached/packaged-detached-verification.json','平台实际运行'),
 ('安装中关闭主窗口并取消','release-final-cancel.json','平台实际运行'),
 ('重开应用、任务中心安全续做','release-final-resume.json','平台实际运行'),
 ('最终包引用与 IL 守卫','release-guard.json','已编译'),
 ('最终包顶层运行时依赖','release-dependencies.json','已编译'),
]
summaries=[]
for title,name,level in records:
 p=r/name; data=json.loads(p.read_text()); assert data.get('status',data.get('Status'))=='passed',(name,data)
 summaries.append(dict(title=title,record=name,state=level,checks=len(data.get('checks',data.get('Checks',[]))) or None,sha256=hashlib.sha256(p.read_bytes()).hexdigest()))
guard=json.loads((r/'guard/guard-tests.json').read_text());assert len(guard['tests'])==6 and all(x['status']=='passed' for x in guard['tests'])
# OS locks are checked directly; stale lock-file existence is not ownership.
locks=[]
for folder in [root/'runtime/apple-silicon-1.21.1',*r.glob('*data'),*r.glob('*data-guest')]:
 p=folder/'.launcher.lock'
 if p.exists():
  with p.open('r+b') as f: fcntl.flock(f,fcntl.LOCK_EX|fcntl.LOCK_NB);fcntl.flock(f,fcntl.LOCK_UN)
  locks.append(str(folder.relative_to(root)))
ps=subprocess.check_output(['ps','-axo','pid,ppid,comm'],text=True)
owned=[line for line in ps.splitlines() if str(root) in line and ('/Pcl.Host' in line or '/PCL-MacPort' in line or line.rstrip().endswith('/java'))]
assert not owned,owned
(r/'release-cleanup.json').write_text(json.dumps(dict(status='passed',reacquiredRootLocks=locks,ownedHostOrGameProcesses=[]),ensure_ascii=False,indent=2))
# Keep the first tested revision and independently prove Host continuity through later Client/UI fixes.
macos=app/'Contents/MacOS'
initial=json.loads((r/'tested-code.json').read_text())
assert all(hashlib.sha256((macos/name).read_bytes()).hexdigest()==h for name,h in initial.items() if name.startswith('host/'))
code={str(p.relative_to(macos)):hashlib.sha256(p.read_bytes()).hexdigest() for p in macos.rglob('*') if p.is_file() and (p.suffix in ('.dll','.dylib') or p.name in ('PCL-MacPort','Pcl.Host'))}
(r/'final-tested-code.json').write_text(json.dumps(code,indent=2))
(r/'verification-summary.json').write_text(json.dumps(dict(version='0.32.0',desktopCoreDependencyRemoved=True,fullFeatureParityClaimed=False,tests=summaries,negativeGuardCases=5),ensure_ascii=False,indent=2))
doc=root/'docs/0.32-架构实现与验收.md'
s=doc.read_text();a=s.index('## 验收记录');b=s.index('## 保留的限制',a)
rows='\n'.join('| '+t+' | '+state+' | `'+name+'` |' for t,name,state in records)
s=s[:a]+'''## 验收记录

原始证据目录为 `artifacts/architecture-0.32/`。以下均已执行。断言数包含按钮启用与重复事件检查，不能解释成独立功能数。

| 用例 | 状态 | 证据文件 |
|---|---|---|
'''+rows+'''

- `release-verified-shell.json` 的 21 项涵盖真实回环 HTTP 下载、暂停/继续、SHA-1 校验、等待活跃页面后仅受理一次安装、诊断脱敏/分块导出、真实 ZIP 导入、自动重连获得新快照、Host 重启恢复账户/配置。
- `release-verified-shell-ui/report.json` 的 13 项驱动真实 Avalonia 按钮。资源和账户 Host 流程另有 29 项，真实资源/皮肤 UI 20 项；最终包主窗口/联机 UI 15 项。
- `release-final-main.json` 的 26 项包括官方加载器目录、实际 Fabric/OptiFabric 安装、两个本机 EasyTier 节点的真实 TCP 往返、微软设备码挑战及取消。它不证明跨互联网 NAT 或两位玩家进世界。
- 真实安装中关闭主窗口后 receipt 为 cancelled，UI 已完成；重开应用从任务中心创建续做操作并成功，关闭任务视图不取消后台安装。托管游戏窗口隐藏及恢复通过，测试主动停止 Java，退出码 137，不是主菜单正常退出。
- 微软既有账户的钥匙串恢复、刷新、正版验证、皮肤读取/规范化预览及 1.21.1 图形初始化 7 项通过；未注销原账户或修改在线皮肤。最后修订涉及 Client 受理协调和 Desktop 验收探针/注释，`host-code-continuity.json` 证明通过微软测试的 Host 代码文件未改变；最终 Client 又通过设备码取消、自动重连、托管及独立游戏测试。
- 关闭启动器后，Host 已退出、Java 渲染已初始化并连续存活观察 5 秒；随后只清理测试进程。最终 `release-cleanup.json` 记录原数据目录及测试目录的 OS 独占锁可重新取得，无本轮 Host/游戏残留。
- 全 Shell/Bootstrap 守卫检查项目图、运行时引用、字段/签名/IL 和异步状态机；正例与五类注入反例通过。最终 Desktop 的项目运行时依赖只有 Pcl.Contract/Pcl.Client，顶层不存在 Core/Application/Domain/Ports/MeloongCore/PCLCS；Host 的实现放在 host/ 子目录。
- Debug/Release 自包含编译通过；AVLN3001 是 MainWindow 构造注入导致的既有提示，实际由 App 创建并通过 UI 流程。内嵌功能矩阵仅更新架构状态，历史功能未验收项未一律改为完成。

## 本轮发现并修复

1. 诊断 ZIP 已生成但新命令不在受控导出许可中：补充明确的导出方法，重测真实 ZIP 读取和脱敏。
2. 日志列表未加载时预选索引 0，首条诊断不显示：改为权威列表到达后选中；实际日志页和关窗重开通过。
3. 界面后台查询与安装/启动/认证受理竞态：Client 受理共享页面排队入口，保留 Host 的写入保护，未使用盲目重试。真实下载占用期间安装请求等待，前序终态后只受理一次；安装关窗取消及重开续做通过。
4. 异步版本图标增加选择修订号，旧结果不能覆盖新选择；下载暂停事件补充断线异常处理；后台重连由会话持有并刷新公开快照。

首次失败及超时记录保留。托管探针此前仍将“没有旧写入授权”误作“连接不可用”，已改为核对连接、禁用的游戏操作按钮和无授权字段；安装探针还改为检查本次新收据，不能借用历史成功记录。源文件嵌套格式按现有格式器展开，未修改第三方源码。

交付前只更新内置报告、证据索引和外层签名，不重编已测业务代码。`delivery.json` 记录 ZIP 哈希、逐文件解压比对、签名与测试代码一致性；应用只做 ad-hoc 签名。

'''+s[b:];doc.write_text(s)
review=r/'review';review.mkdir(exist_ok=True)
tr=''.join('<tr><td>'+html.escape(x['title'])+'</td><td>'+x['state']+'</td><td><a href="../'+x['record']+'">'+html.escape(x['record'])+'</a></td></tr>' for x in summaries)
(review/'index.html').write_text('''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>PCL Mac 0.32 验证</title><style>body{font:16px system-ui;background:#eff4fa;color:#20364b;max-width:1100px;margin:48px auto;padding:20px}h1{color:#126cc5}section{background:white;padding:24px;border-radius:12px;margin:20px 0}td,th{padding:12px;text-align:left;border-bottom:1px solid #dde5ed}table{width:100%;font-size:14px}a{color:#156bb2}img{max-width:100%;border:1px solid #ddd;margin-top:16px}code{background:#edf3f9;padding:4px}</style><h1>PCL Mac 0.32</h1><section><h2>本轮优先项已完成</h2><p>Desktop → Core 的编译和运行时依赖已移除。主窗口、剩余旧页面及 Bootstrap 使用 Contract/Client，业务由独立 Host 持有；不再签发旧页面写入授权。</p><p>这是架构迁移完成项，不代表全部 Windows 功能等价。</p></section><section><h2>实际证据</h2><table><tr><th>用例</th><th>状态</th><th>记录</th></tr>'''+tr+'''</table><p>负例守卫：<a href="../guard/guard-tests.json">六个测试（一个正例、五类负例）</a>。交付校验：<a href="../delivery.json">delivery.json</a>。</p></section><section><h2>真实应用截图</h2><img src="../release-verified-shell-ui/network.png" alt="网络设置"><img src="../release-verified-shell-ui/diagnostics.png" alt="日志诊断"><img src="../release-browser-ui/skin-page.png" alt="皮肤页"><img src="../release-browser-ui/resource-search.png" alt="资源搜索"></section><section><h2>范围限制</h2><p>没有第二个微软账户、额外服务密钥、跨互联网联机或本轮人工世界存档验收；独立游戏观察为 5 秒，测试结束后清理自身进程。安装仍复用既有执行器，未知提交不盲目重放。应用为 Apple Silicon 自包含包，仅 ad-hoc 签名，未公证。</p></section></html>''')
shutil.copy2(doc,app/'Contents/Resources'/doc.name)
shutil.copy2(r/'verification-summary.json',app/'Contents/Resources/verification-0.32.json')
# Only the main apphost signature may change when outer Resources are signed; verify its unsigned payload too.
unsigned_before=r/'apphost-before-signing';unsigned_after=r/'apphost-after-signing'
shutil.copy2(macos/'PCL-MacPort',unsigned_before)
subprocess.run(['codesign','--remove-signature',str(unsigned_before)],check=True,capture_output=True)
subprocess.run(['codesign','--force','--sign','-',str(app)],check=True,capture_output=True)
shutil.copy2(macos/'PCL-MacPort',unsigned_after)
subprocess.run(['codesign','--remove-signature',str(unsigned_after)],check=True,capture_output=True)
assert unsigned_before.read_bytes()==unsigned_after.read_bytes()
for name,h in code.items():
 if name!='PCL-MacPort': assert hashlib.sha256((macos/name).read_bytes()).hexdigest()==h,name
subprocess.run(['codesign','--verify','--deep','--strict',str(app)],check=True,capture_output=True)
zip_path=root/'artifacts/PCL-Mac-0.32-arm64.zip'
subprocess.run(['ditto','-c','-k','--sequesterRsrc','--keepParent',str(app),str(zip_path)],check=True)
extract=r/'delivery-extract';extract.mkdir(exist_ok=True)
subprocess.run(['ditto','-x','-k',str(zip_path),str(extract)],check=True)
restored=extract/app.name
files=[p for p in app.rglob('*') if p.is_file()]
assert len(files)==sum(p.is_file() for p in restored.rglob('*'))
assert all(hashlib.sha256(p.read_bytes()).digest()==hashlib.sha256((restored/p.relative_to(app)).read_bytes()).digest() for p in files)
subprocess.run(['codesign','--verify','--deep','--strict',str(restored)],check=True,capture_output=True)
delivery=dict(status='passed',version='0.32.0',zip=str(zip_path),bytes=zip_path.stat().st_size,sha256=hashlib.sha256(zip_path.read_bytes()).hexdigest(),matchingExtractedFiles=len(files),unchangedTestedCodeFilesExceptApphostSignature=len(code)-1,mainApphostUnsignedPayloadUnchanged=True,signature='ad-hoc verified, not notarized')
(r/'delivery.json').write_text(json.dumps(delivery,ensure_ascii=False,indent=2));print(json.dumps(delivery,ensure_ascii=False))
