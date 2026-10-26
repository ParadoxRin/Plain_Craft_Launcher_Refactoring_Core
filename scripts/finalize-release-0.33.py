"""核对每条证据、记录剩余项，签名交付副本并逐文件验证解压结果。"""
from pathlib import Path
import collections
import fcntl
import hashlib
import html
import json
import shutil
import subprocess

root = Path(__file__).resolve().parent.parent
output = root / 'artifacts/verification-0.33'
app = root / 'artifacts/PCL Mac 0.33 Architecture.app'
macos = app / 'Contents/MacOS'


def sha(path):
    """对确切文件取 SHA-256，以内容而非时间戳关联证据。"""
    return hashlib.sha256(path.read_bytes()).hexdigest()


records = [
    ('脚本转义、离线身份、保护和皮肤准备', 'verified-script-tests.json', '平台实际运行（拒绝输入含替身）'),
    ('最终交付包实例页与导出按钮', 'delivery-script-ui/ui.json', '平台实际运行'),
    ('UI/Host退出后脚本直接启动游戏', 'delivery-script-ui/standalone.json', '平台实际运行'),
    ('离线账户、公开皮肤、资源目录和重启恢复', 'release-browser.json', '平台实际运行'),
    ('微软钥匙串恢复、正版验证与皮肤/游戏启动', 'final-microsoft.json', '平台实际运行'),
    ('托管游戏隐藏、停止和窗口恢复', 'release-managed.json', '平台实际运行'),
    ('正常关闭应用后游戏继续运行', 'release-detached/packaged-detached-verification.json', '平台实际运行'),
    ('最终包引用和编译后符号守卫', 'release-guard.json', '已编译'),
    ('最后矩阵重编前后的Host一致性', 'host-continuity.json', '文件校验'),
]
results = []
for title, name, state in records:
    path = output / name
    data = json.loads(path.read_text())
    assert data.get('status', data.get('Status')) == 'passed', (name, data)
    results.append(dict(title=title, path=name, state=state, sha256=sha(path)))

# 所有矩阵证据都必须仍可读取且哈希不变；历史与本轮断言不会因重复次数提升为新功能。
matrix = json.loads((root / 'docs/data/PCL2完整功能矩阵.json').read_text())
assert len(matrix['features']) == 132
references = set()
for feature in matrix['features']:
    audit = feature['audit033']
    assert audit['reviewed'] and audit['nextAction']
    for evidence in audit['evidence'] + audit.get('newEvidence', []):
        path = root / evidence['path']
        assert sha(path) == evidence['sha256'], evidence['path']
        assert evidence['scope'] and evidence['assertions']
        references.add(evidence['path'])
summary = dict(status='passed', version='0.33.0', historicalRowsWithDirectEvidence=77,
               reviewedRows=132, uniqueEvidenceFiles=len(references),
               workKinds=matrix['evidenceReconciliation']['workKinds'],
               completedImplementation=['INST-010'], tests=results,
               fullFeatureParityClaimed=False)
(output / 'verification-summary.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2))

# 锁文件可以残留；验证实际OS独占锁已释放，不凭PID或文件存在判断。
locks = []
for data_root in [root / 'runtime/apple-silicon-1.21.1', *output.glob('*data')]:
    lock = data_root / '.launcher.lock'
    if lock.exists():
        with lock.open('r+b') as handle:
            fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
            fcntl.flock(handle, fcntl.LOCK_UN)
        locks.append(str(data_root.relative_to(root)))
processes = subprocess.check_output(['ps', '-axo', 'pid,ppid,comm'], text=True)
owned = [line for line in processes.splitlines() if str(root) in line and
         ('/Pcl.Host' in line or '/PCL-MacPort' in line or line.rstrip().endswith('/java'))]
assert not owned, owned
(output / 'release-cleanup.json').write_text(json.dumps(dict(status='passed', reacquiredRootLocks=locks, remainingOwnedProcesses=[]), indent=2))

rows = '\n'.join('| ' + title + ' | ' + state + ' | `' + name + '` |' for title, name, state in records)
doc = root / 'docs/0.33-架构实现与验收.md'
doc.write_text('''# PCL Mac 0.33 实现与验收

## 交付变化

本轮完整审查132项矩阵，将0.24–0.32的原始测试文件、具体断言、SHA-256和适用范围回填到77项；未找到新直接证据的条目保留历史范围，没有按报告总数推算完成率。最终为 **42项已覆盖限定范围、35项实现缺口、46项定向验收、9项外部条件验收**。分类互斥；外部条件标记可与实现缺口交叉。详见 [逐项剩余清单](0.33-证据回填与剩余清单.md)。

实际补齐 **INST-010：离线启动脚本**，从“实例管理 → 导出离线启动脚本”使用。旧0.12拒绝导出代码已删除。它不是打开启动器的快捷方式：Host复用现有版本加载、Java选择和LaunchService，生成逐参数引用的POSIX脚本；Client通过受控分块导出校验SHA-256，同目录原子发布并设置所有者可执行权限。已存在目标不覆盖，非法玩家名与非离线身份拒绝。

同名离线账户的本地皮肤和模型在准备阶段沿用，其他账户和选中状态不改变。脚本不读取微软钥匙串，不写入微软令牌，不执行前置命令。脚本仅在原Mac、原Java和实例路径上运行；改动路径/配置后重新导出，不能当作可跨机离线整合包。

## 架构与退出责任

Desktop保持Contract/Client边界，新页面没有Core引用。执行位于Host，新增导出命令受既有工作区受理和未知提交保护约束。公开DTO只新增默认false的Executable标记，没有把Java命令行或凭据放进公共启动契约。导出窗口持有自身CTS；关闭请求取消，待处理器收尾后关闭，只有处理器释放CTS。原有托管游戏及独立运行策略未更改。

## 本轮实际测试

原始记录位于 `artifacts/verification-0.33/`。下表中的断言数不是独立功能数量。调用栈确认钥匙串等待后，用户允许当前Pcl.Host访问；随后微软复测通过。

| 用例 | 状态 | 证据 |
|---|---|---|
''' + rows + '''

- 新脚本在真实/bin/sh中验证空参数、单引号、换行、命令替换及反引号不会被求值；受控Host导出、仅所有者可执行、原目标保留、非法名称、取消前无文件、Alex模型UUID和资源包准备通过，共12条断言。
- 最终UI通过真实实例页打开导出窗口并点击按钮；重复导出给出明确错误且原文件不变，共4条。实际保存选择器的OS对话框没有自动操作，测试填入同一个路径字段。
- 等应用和Host退出后，直接运行导出的.command；Minecraft1.21.1实际完成图形/atlas初始化，额外观察5秒存活后清理本轮进程。这不是人工进世界或保存验收。
- 公开皮肤/账户/资源29条；微软7条涵盖既有账户恢复、权益刷新、皮肤只读查询、游戏渲染及幂等启动。不涉及第二个微软账户、在线皮肤上传或外置认证成功链路。
- 托管测试执行真实安装入口、隐藏/停止/恢复窗口；退出137为测试主动停止，不冒充正常菜单退出。独立模式验证应用/Host退出后游戏继续存活5秒；脚本测试退出143同为测试清理。
- 首次新测试遇到await与Span跨越的编译错误，已修正并重编；初版窗口底部空间不足，已增加高度并重新渲染。所有失败记录保留，未用它们作为通过证据。
- 最后一次桌面重编仅更新内嵌矩阵，Host逐文件与已测版本完全一致；随后重新执行交付包导出UI和独立启动。最终程序集守卫零违规，AVLN3001为MainWindow构造注入的既有提示，实际UI流程已运行。

## 剩余边界

本轮只新增并完成离线脚本这一项真实功能，不宣称其余35项缺口完成，也不把46项定向验收算作未实现。复杂依赖求解、第三方自定义整合包、完整代理覆盖、安装任务续做、在线更新等仍按逐项清单推进。第二微软账户、CurseForge密钥、外置账号、跨互联网联机条件未补造。旧存档未拿来做破坏性测试；本轮没有新的人工世界、游戏内皮肤或长时间稳定性验收。

交付为Apple Silicon自包含应用，使用ad-hoc签名，未做Developer ID公证。`delivery.json`记录ZIP哈希、签名及解压逐文件比对。原0.32归档与原数据目录保留。
''')

# 同页展示精确剩余项与证据链接，方便按类别和名称核对，不隐藏部分实现项。
review = output / 'review'
review.mkdir(exist_ok=True)
feature_rows = []
for feature in matrix['features']:
    audit = feature['audit033']
    links = []
    for evidence in audit['evidence'] + audit.get('newEvidence', []):
        links.append('<a href="' + html.escape((root / evidence['path']).as_uri(), quote=True) + '">' + html.escape(evidence['path']) + '</a>：' + html.escape(evidence['scope']))
    feature_rows.append('<tr data-kind="' + html.escape(audit['workKind']) + '"><td>' + feature['id'] + '</td><td>' + html.escape(feature['name']) + '</td><td>' + audit['workKind'] + '</td><td>' + html.escape(feature['remaining']) + '<details><summary>证据与范围（' + str(len(links)) + '）</summary>' + '<p>'.join(links) + '</details></td></tr>')
record_rows = ''.join('<tr><td>' + html.escape(x['title']) + '</td><td>' + x['state'] + '</td><td><a href="../' + x['path'] + '">' + x['path'] + '</a></td></tr>' for x in results)
(review / 'index.html').write_text('''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>PCL Mac 0.33：证据与剩余清单</title><style>body{font:16px system-ui;background:#eff4fa;color:#20364b;max-width:1250px;margin:40px auto;padding:20px}section{background:white;padding:24px;border-radius:12px;margin:20px 0}table{width:100%;border-collapse:collapse;font-size:14px}td,th{text-align:left;padding:12px;border-bottom:1px solid #dde5ed;vertical-align:top}a{color:#156bb2;overflow-wrap:anywhere}details{margin-top:10px;font-size:12px}input,select{padding:10px;margin:10px}img{max-width:100%}</style><h1>PCL Mac 0.33</h1><section><h2>132项逐项审查，77项补入历史直接证据</h2><p>42项已覆盖限定范围 · 35项实现缺口 · 46项定向验收 · 9项外部条件验收。</p><p>本轮实际补齐：离线启动脚本。UI/Host退出后直接运行Minecraft1.21.1；保留现有登录、皮肤和独立运行行为。未声称全部PCL2功能完成。</p><img src="../delivery-script-ui/ui.png" alt="实际导出窗口截图"></section><section><h2>本轮验证</h2><table>''' + record_rows + '''</table><p><a href="../delivery.json">ZIP与签名校验</a> · <a href="../matrix-final.json">完整矩阵JSON</a></p></section><section><h2>全部功能与剩余项</h2><input id="query" placeholder="搜索编号、功能、限制"><select id="kind"><option value="">全部类别</option><option>实现缺口</option><option>定向验收</option><option>外部条件验收</option><option>已覆盖限定范围</option></select><table id="features"><thead><tr><th>ID</th><th>功能</th><th>类别</th><th>限制与证据</th></tr></thead><tbody>''' + ''.join(feature_rows) + '''</tbody></table></section><script>const q=document.querySelector('#query'),k=document.querySelector('#kind');function filter(){for(const r of document.querySelectorAll('#features tbody tr')){r.hidden=!!((k.value&&r.dataset.kind!==k.value)||!r.textContent.toLowerCase().includes(q.value.toLowerCase()));}}q.addEventListener('input',filter);k.addEventListener('change',filter);</script></html>''')

# 只更新包内说明和外层签名；已测代码除主apphost的外层签名外必须逐文件不变。
for path in [doc, root / 'docs/0.33-证据回填与剩余清单.md', root / 'docs/data/PCL2完整功能矩阵.json', output / 'verification-summary.json']:
    shutil.copy2(path, app / 'Contents/Resources' / path.name)
tested = json.loads((output / 'final-tested-code.json').read_text())
for name, expected in tested.items():
    assert sha(macos / name) == expected, name
before = output / 'apphost-before-signing'
after = output / 'apphost-after-signing'
shutil.copy2(macos / 'PCL-MacPort', before)
subprocess.run(['codesign', '--remove-signature', str(before)], check=True, capture_output=True)
subprocess.run(['codesign', '--force', '--sign', '-', str(app)], check=True, capture_output=True)
shutil.copy2(macos / 'PCL-MacPort', after)
subprocess.run(['codesign', '--remove-signature', str(after)], check=True, capture_output=True)
assert before.read_bytes() == after.read_bytes()
for name, expected in tested.items():
    if name != 'PCL-MacPort':
        assert sha(macos / name) == expected, name
subprocess.run(['codesign', '--verify', '--deep', '--strict', str(app)], check=True, capture_output=True)
zip_path = root / 'artifacts/PCL-Mac-0.33-arm64.zip'
subprocess.run(['ditto', '-c', '-k', '--sequesterRsrc', '--keepParent', str(app), str(zip_path)], check=True)
extract = output / 'delivery-extract'
extract.mkdir(exist_ok=True)
subprocess.run(['ditto', '-x', '-k', str(zip_path), str(extract)], check=True)
restored = extract / app.name
files = [path for path in app.rglob('*') if path.is_file()]
assert len(files) == sum(path.is_file() for path in restored.rglob('*'))
assert all(sha(path) == sha(restored / path.relative_to(app)) for path in files)
subprocess.run(['codesign', '--verify', '--deep', '--strict', str(restored)], check=True, capture_output=True)
delivery = dict(status='passed', version='0.33.0', zip=str(zip_path), bytes=zip_path.stat().st_size,
                sha256=sha(zip_path), matchingExtractedFiles=len(files),
                unchangedTestedCodeFilesExceptApphostSignature=len(tested)-1,
                mainApphostUnsignedPayloadUnchanged=True, signature='ad-hoc verified, not notarized')
(output / 'delivery.json').write_text(json.dumps(delivery, ensure_ascii=False, indent=2))
print(json.dumps(delivery, ensure_ascii=False))
