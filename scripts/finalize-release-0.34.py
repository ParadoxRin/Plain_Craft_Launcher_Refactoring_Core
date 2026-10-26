# -*- coding: utf-8 -*-
"""Verify evidence and exact tested payloads, then sign/package the 0.34 deliverable."""
from pathlib import Path
import json, hashlib, shutil, subprocess, fcntl, html
root = Path(__file__).resolve().parents[1]
out = root / 'artifacts/verification-0.34'
app = root / 'artifacts/PCL Mac 0.34 Architecture.app'
macos = app / 'Contents/MacOS'

def sha(p):
    """计算确切文件的SHA-256，用于将证据、已测程序和交付文件关联。"""
    return hashlib.sha256(p.read_bytes()).hexdigest()
records = [('联合依赖、回滚、完整性失败、模块及整合包边界', 'focused-integrity/results.json', '替身运行＋本地JDK/文件事务'), ('MultiMC/HMCL导入及mrpack往返启动', 'final-custom-packs.json', '平台实际运行'), ('Forge/NeoForge与真实Sodium嵌套依赖', 'final-loaders.json', '平台实际运行'), ('真实Fabric API下载、批量更新及回退', 'release-resources.json', '平台实际运行'), ('账户、公开皮肤保存刷新与重启恢复', 'release-browser.json', '平台实际运行'), ('微软钥匙串、正版、皮肤与游戏启动', 'final-microsoft.json', '平台实际运行'), ('Avalonia安装入口、托管游戏与窗口恢复', 'release-managed.json', '平台实际运行'), ('UI/Host退出后独立游戏存活', 'release-detached/packaged-detached-verification.json', '平台实际运行'), ('最终包引用图与符号守卫', 'release-guard.json', '已编译'), ('矩阵重编后Host代码一致性', 'host-continuity.json', '文件校验')]
results = []
for (title, file, state) in records:
    p = out / file
    d = json.loads(p.read_text())
    status = d.get('status', d.get('Status', 'passed' if d.get('failed') == 0 else None))
    assert status == 'passed', (file, status)
    results.append(dict(title=title, path=file, state=state, sha256=sha(p), assertionCount=len(d.get('checks', d.get('Checks', d.get('rows', []))))))
m = json.loads((root / 'docs/data/PCL2完整功能矩阵.json').read_text())
refs = set()
for f in m['features']:
    for e in f['audit033']['evidence'] + f['audit033'].get('newEvidence', []) + f.get('audit034', {}).get('evidence', []):
        assert sha(root / e['path']) == e['sha256'], e['path']
        assert e['assertions'] and e['scope']
        refs.add(e['path'])
summary = dict(status='passed', version='0.34.0', reviewedRows=132, implementationGapRows=35, completedSubcapabilities=3, completedWholeFeatureRows=0, fullFeatureParityClaimed=False, tests=results, uniqueHistoricalAndNewEvidenceFiles=len(refs))
(out / 'verification-summary.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2))
locks = []
for data in [root / 'runtime/apple-silicon-1.21.1', *out.glob('*data')]:
    p = data / '.launcher.lock'
    if p.exists():
        with p.open('r+b') as f:
            fcntl.flock(f, fcntl.LOCK_EX | fcntl.LOCK_NB)
            fcntl.flock(f, fcntl.LOCK_UN)
        locks.append(str(data.relative_to(root)))
owned = [line for line in subprocess.check_output(['ps', '-axo', 'pid,ppid,comm'], text=True).splitlines() if str(root) in line and ('/Pcl.Host' in line or '/PCL-MacPort' in line or line.rstrip().endswith('/java'))]
assert not owned, owned
(out / 'release-cleanup.json').write_text(json.dumps(dict(status='passed', reacquiredRootLocks=locks, remainingOwnedProcesses=[]), indent=2))
rows = '\n'.join(('| ' + x['title'] + ' | ' + x['state'] + ' | ' + str(x['assertionCount'] or '见原始记录') + ' | `' + x['path'] + '` |' for x in results))
doc = root / 'docs/0.34-复杂依赖与整合包验收.md'
doc.write_text('# PCL Mac 0.34：复杂依赖与整合包兼容\n\n## 实际变化\n\n1. **批量更新联合求解**：ResourceBatchUpdater 不再逐根独立选前置。ResourceCatalog.ResolveManyAsync 共用项目版本表，显式根版本锁定；JointResourcePlanner 回溯未锁定的声明前置。每套候选在暂存目录交给现有 ResourceInstaller 和加载器校验，成功后才允许预览/提交，失败、取消均保留现有模组。最多256个已检查状态、每项64个候选、16384个待检查状态；超过限制明确“尚未判定”，不伪称无解。网络/文件错误直接传播；下载完整性失败单独传播，不触发降级回溯。\n2. **JarJar模块身份**：按目标Java优先选显式module-info.class，再按Multi-Release主属性选择适用版本；回退Automatic-Module-Name及文件名。处理manifest续行和主段、修改版UTF-8、描述符边界/重复；纯字节解析在Domain，JAR/文件读取仍在Core/Host。Forge/NeoForge嵌套库不同文件名、不同坐标但同显式模块可按既有身份规则去重。不是完整JVM class验证器，也未补完语言插件或所有TOML语义。\n3. **自定义整合包入口**：MultiMC声明式组件及HMCL官方基础元数据可附加经过大小/SHA-1校验的库和mainClass；类名、Mac规则和类文件存在/唯一性须通过。保留标准账户参数、Java和natives重建流程。PCL扩展schema2保存入口与有序+tweakers，mrpack导出重导不丢入口；tweakers仅允许LaunchWrapper入口。schema1继续可读，schema2需要PCL Mac0.34+，不声称其他启动器识别该扩展。\n\n入口仍在现有“本地资源→检查更新→批量更新”和“整合包→导入/导出”中，执行位于Host；没有新增空页面或把业务规则迁回Desktop。Desktop仍只运行时引用Contract/Client。沿用IAccountSecrets、既有微软应用ID和游戏退出策略。\n\n## 本轮证据\n\n原始证据：`artifacts/verification-0.34/`。断言数量不是功能数量；构造整合包不是公开真实整合包兼容性的替代证据。\n\n| 用例 | 状态 | 断言数 | 原始记录 |\n|---|---|---:|---|\n' + rows + (
    '\n\n- 63项定向检查通过，含两个根、两个可变前置的联合回溯，循环根、精确版本冲突、取消、无法满足约束的回滚和损坏下载不降级。HTTP目录与下载为替身，实际JAR解析和文件事务使用生产代码。复用的Quilt覆盖、JarJar范围求交、整合包隐私/导出等相关分支亦运行。\n- JDK17实际编译并读取7组模块：显式、Multi-Release目标9/17、未启用Multi-Release、manifest续行、文件名推导、Unicode模块，与C#结果一致；另测重复/截断以及Forge/NeoForge嵌套身份。\n- 随包Host分别安装MultiMC、HMCL构造包，及导出重导mrpack，共3次真实Minecraft1.21.1启动：编译的自定义入口输出固定标记并委托官方Main，保留完整游戏参数，图形atlas初始化完成。入口JAR为本地编译并预置SHA1缓存，不把它写成公网下载成功。未做人工进入世界、保存、重进或任意商业整合包验收。\n- 真实Forge1.21.1/52.1.20及NeoForge1.21.1/21.1.264启动；NeoForge从Modrinth下载Sodium mc1.21.1-0.8.13-neoforge，实际选中5个嵌套JarJar项，图形初始化后再观察5秒。旧实例和存档使用隔离副本。\n- 真实Fabric API下载、批量准备/丢弃/提交、旧文件保留11项；公开皮肤和账户29项；微软既有账户钥匙串恢复、正版验证、只读皮肤和游戏启动7项。没有新增第二个微软账户或外置账号成功证据。\n- 托管测试实际点击安装/启动，验证隐藏、停止后恢复窗口；退出137来自测试主动停止，不是正常菜单退出。独立模式实际关闭UI及Host，确认游戏已渲染并继续运行5秒，再清理本轮游戏进程；不代表数小时稳定性。\n- 初轮发现自定义入口清单仍写schema1，导致重导失败；修为schema2后完整重导/启动通过。收尾修复完整性错误误归为依赖冲突，63项检查及随包实际流程均在修复后重跑。旧失败记录保留，不用于通过结论。\n- 最后只重编Desktop版本/内嵌矩阵，199'
    '个Host代码文件与已测版本逐文件一致。随后用最终包运行托管/独立窗口流程及符号守卫。AVLN3001是MainWindow构造注入的既有编译提示，真实窗口路径已运行。\n\n## 剩余范围\n\n仍为42项已覆盖限定范围、35项实现缺口、46项定向验收、9项外部条件验收。**本轮完成3组子能力，补强7个部分实现条目；没有把35个大项全部标成完成。** 每项下一步详见 [0.34剩余清单](【已弃用】0.34-剩余清单.md)，历史audit033保留，新证据以audit034及SHA-256关联。\n\n优先待补：Quilt path/pattern/version覆盖，Fabric schema0/覆盖与插件，NeoForge provides/语言插件，整合包锁定清单联合补全、自定义客户端/jarmods/安装器、配置三方合并。+tweakers正向LaunchWrapper游戏本轮未实测；现在仅有解析/拒绝保护证据。没有自动推断它已完整兼容LiteLoader/OptiFine任意组合。\n\n## 交付与官方依据\n\nApple Silicon自包含应用，ad-hoc签名已验证，未做Developer ID公证。ZIP逐文件解压比较和签名结果见delivery.json；测试进程已清理，数据目录OS锁已重新取得后释放。原0.33包和原数据目录保留。\n\n- [MultiMC JSON Patches](https://github.com/MultiMC/Launcher/wiki/JSON-Patches)：mainClass、+tweakers、组件补丁；官方页面本身声明并非完整规范，故保留受支持字段边界。\n- [Java ModuleFinder](https://docs.oracle.com/en/java/javase/21/docs/api/java.base/java/lang/module/ModuleFinder.html)：模块查找、显式/自动模块身份；本轮另有独立JDK对照数据。\n- [JVM class文件规范](https://docs.oracle.com/j'
    'avase/specs/jvms/se21/html/jvms-4.html)：常量池、修改版UTF-8、Module属性结构。\n- [NeoForge Jar-in-Jar](https://docs.neoforged.net/toolchain/docs/dependencies/jarinjar/)：内嵌元数据和版本协商；范围求交继续复用已实现路径。\n'
))
review = out / 'review'
review.mkdir(exist_ok=True)
trs = []
for f in m['features']:
    audit = f.get('audit034', f['audit033'])
    ev = f['audit033']['evidence'] + f['audit033'].get('newEvidence', []) + f.get('audit034', {}).get('evidence', [])
    links = ''.join(('<p><a href="' + html.escape((root / e['path']).as_uri(), quote=True) + '">' + html.escape(e['path']) + '</a> ' + html.escape(e['scope']) + '</p>' for e in ev))
    trs.append('<tr data-kind="' + audit['workKind'] + '"><td>' + f['id'] + '</td><td>' + html.escape(f['name']) + '</td><td>' + audit['workKind'] + '</td><td>' + html.escape(f['remaining']) + '<details><summary>证据与范围</summary>' + links + '</details></td></tr>')
tests = ''.join(('<tr><td>' + x['title'] + '</td><td>' + x['state'] + '</td><td><a href="../' + x['path'] + '">' + x['path'] + '</a></td></tr>' for x in results))
(review / 'index.html').write_text('<!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>PCL Mac 0.34</title><style>body{font:16px system-ui;max-width:1300px;margin:35px auto;padding:22px;background:#eef3f8;color:#25364b}section{background:white;border-radius:16px;padding:25px;margin:20px 0}table{border-collapse:collapse;width:100%;font-size:14px}td,th{padding:12px;border-bottom:1px solid #dce3ec;text-align:left;vertical-align:top}a{color:#1662ad;overflow-wrap:anywhere}details{font-size:12px;margin-top:10px}input,select{padding:10px;margin:8px}</style><h1>PCL Mac 0.34</h1><section><h2>本轮新增三组实际能力</h2><p>批量更新多根联合回溯 · JarJar模块身份 · 经校验的自定义整合包入口及往返</p><p>63项定向检查；真实Host完成MultiMC/HMCL/mrpack启动、Forge/NeoForge和Sodium、微软正版/皮肤及窗口退出流程。</p><p><strong>35个实现缺口大项未全部清零</strong>：本轮补强7个部分实现条目。图形初始化不冒充世界保存验收，构造包不冒充所有公开整合包。</p><p><a href="../delivery.json">下载包与签名校验</a> · <a href="../matrix-final.json">完整矩阵JSON</a></p></section><section><h2>逐项实际证据</h2><table>' + tests + '</table></section><section><h2>132项矩阵与准确剩余范围</h2><p>42项已覆盖限定范围 · 35项实现缺口 · 46项定向验收 · 9项外部条件验收</p><input id="q" placeholder="搜索编号、功能、限制"><select id="k"><option value="">全部类别</option><option>实现缺口</option><option>定向验收</option><option>外部条件验收</option><option>已覆盖限定范围</option></select><table id="matrix"><thead><tr><th>ID</th><th>功能</th><th>类别</th><th>仍需完成及证据</th></tr></thead><tbody>' + ''.join(trs) + "</tbody></table></section><script>const q=document.querySelector('#q'),k=document.querySelector('#k');function filter(){for(const r of document.querySelectorAll('#matrix tbody tr'))r.hidden=!!((k.value&&r.dataset.kind!==k.value)||!r.textContent.toLowerCase().includes(q.value.toLowerCase()));}q.addEventListener('input',filter);k.addEventListener('change',filter);</script></html>")
for p in [doc, root / 'docs/【已弃用】0.34-剩余清单.md', root / 'docs/data/PCL2完整功能矩阵.json', out / 'verification-summary.json']:
    shutil.copy2(p, app / 'Contents/Resources' / p.name)
tested = json.loads((out / 'final-tested-code.json').read_text())
for (name, expected) in tested.items():
    assert sha(macos / name) == expected, name
before = out / 'apphost-before-signing'
after = out / 'apphost-after-signing'
shutil.copy2(macos / 'PCL-MacPort', before)
subprocess.run(['codesign', '--remove-signature', str(before)], check=True, capture_output=True)
subprocess.run(['codesign', '--force', '--sign', '-', str(app)], check=True, capture_output=True)
shutil.copy2(macos / 'PCL-MacPort', after)
subprocess.run(['codesign', '--remove-signature', str(after)], check=True, capture_output=True)
assert before.read_bytes() == after.read_bytes()
for (name, expected) in tested.items():
    if name != 'PCL-MacPort':
        assert sha(macos / name) == expected, name
subprocess.run(['codesign', '--verify', '--deep', '--strict', str(app)], check=True, capture_output=True)
zipfile = root / 'artifacts/PCL-Mac-0.34-arm64.zip'
subprocess.run(['ditto', '-c', '-k', '--sequesterRsrc', '--keepParent', str(app), str(zipfile)], check=True)
extract = out / 'delivery-extract'
extract.mkdir(exist_ok=True)
subprocess.run(['ditto', '-x', '-k', str(zipfile), str(extract)], check=True)
restored = extract / app.name
files = [p for p in app.rglob('*') if p.is_file()]
assert len(files) == sum((p.is_file() for p in restored.rglob('*')))
assert all((sha(p) == sha(restored / p.relative_to(app)) for p in files))
subprocess.run(['codesign', '--verify', '--deep', '--strict', str(restored)], check=True, capture_output=True)
delivery = dict(status='passed', version='0.34.0', zip=str(zipfile), bytes=zipfile.stat().st_size, sha256=sha(zipfile), matchingExtractedFiles=len(files), unchangedTestedCodeFilesExceptApphostSignature=len(tested) - 1, mainApphostUnsignedPayloadUnchanged=True, signature='ad-hoc verified, not notarized')
(out / 'delivery.json').write_text(json.dumps(delivery, ensure_ascii=False, indent=2))
wrapper = root / 'artifacts/打开PCL-Mac-0.34.command'
wrapper.write_text('#!/bin/bash\n# Reuse the existing data root and keychain namespace without copying credentials.\nset -euo pipefail\nartifact_dir="$(cd "$(dirname "$0")" && pwd)"\nopen -n "$artifact_dir/PCL Mac 0.34 Architecture.app" --args --data-root "$artifact_dir/../runtime/apple-silicon-1.21.1"\n')
wrapper.chmod(448)
print(json.dumps(delivery, ensure_ascii=False))
