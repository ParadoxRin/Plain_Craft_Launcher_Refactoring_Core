"""Package an allowlisted, credential-free set of local test evidence and a readable review page."""
from pathlib import Path
from collections import Counter
import hashlib,html,json,shutil,zipfile,plistlib
root=Path(__file__).resolve().parents[1]
base=root/'artifacts/verification-0.22'
review=base/'review'; review.mkdir(exist_ok=True)
exports=review/'evidence'; exports.mkdir(exist_ok=True)
shots=review/'screenshots'; shots.mkdir(exist_ok=True)
checks=[]
for folder,name in [('final-local-ui','local-settings-ui.json'),('final-local-ui','local-settings-reopen.json'),('final-settings','settings-ui.json'),('final-settings','settings-reopen.json'),('final-layout','windows-layout-ui.json')]:
    path=base/folder/'evidence'/name
    item=json.loads(path.read_text())
    assert item['status']=='passed',(path,item)
    checks.append(dict(file=folder+'/'+name,checks=len(item['checks']),status=item['status']))
    shutil.copy2(path,exports/name)
regression=json.loads((base/'final-tests/test-results.json').read_text())
assert regression['status']=='passed'
shutil.copy2(base/'final-tests/test-results.json',exports/'test-results.json')
for name in ['comments.log','format.log','package.log','final-tests.log','skin-presets.json','detached-survival.json','lwjgl-agent-enabled.log','lwjgl-agent-disabled.log','game-native-1.12.2.log','game-native-1.21.1.log','game-native-26.3.log','final-app-game.log']:
    if (base/name).exists():shutil.copy2(base/name,exports/name)
for file in (root/'artifacts/comment-current').glob('*.json'):shutil.copy2(file,exports/file.name)
for file in (root/'artifacts/format-current').glob('*.json'):shutil.copy2(file,exports/file.name)
game=base/'game-data/evidence'
for pattern in ['visibility-*.json','window-policy-*.result.json','launch-result-1.12.2.json','launch-result-1.21.1.json','launch-result-26.3.json']:
    for file in game.glob(pattern):shutil.copy2(file,exports/file.name)
for name in ['1.12.2','1.21.1','26.3']:
    result=json.loads((game/f'launch-result-{name}.json').read_text())
    assert result['graphicsLogObserved'] and result['atlasCreationObserved']
    shutil.copy2(Path(result['log']),exports/f'minecraft-{name}.log')
for name in ['0.22-settings-binding-audit.json','【已弃用】0.22设置逐项核对.md','0.22实现与验收.md','0.21-settings-binding-audit.json']:
    shutil.copy2(root/'docs'/name,review/name)
images=[]
for folder,names in [('final-layout',['01-launch','setup-launch','setup-ui','setup-system','mod-list','mod-detail','shader-list','shader-detail']),('final-settings',['InstanceWorkspace','TaskCenterWindow','SkinManagerWindow','LocalResourcesWindow']),('final-local-ui',['local-appearance','local-system'])]:
    for name in names:
        file=base/folder/'evidence'/(name+'.png')
        if file.exists():
            target=folder+'-'+name+'.png';shutil.copy2(file,shots/target);images.append((name,'screenshots/'+target))
audit=json.loads((root/'docs/0.22-settings-binding-audit.json').read_text())
app=root/'artifacts/PCL Mac 0.22.app'
info=plistlib.loads((app/'Contents/Info.plist').read_bytes());assert info['CFBundleShortVersionString']=='0.22.0'
archive=root/'artifacts/PCL-Mac-0.22-arm64.zip'
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None
    assert any(n.endswith('/libpclwindow.dylib') for n in z.namelist())
    assert any(n.endswith('/lwjgl-unsafe-agent-2.0.jar') for n in z.namelist())
    assert not any(n.endswith('/accounts.json') for n in z.namelist())
receipt={'version':'0.22','coreTests':228,'uiChecks':sum(x['checks'] for x in checks),'uiSuites':checks,'original40':audit['original40'],'total121':audit['counts'],'appSha256':hashlib.sha256(archive.read_bytes()).hexdigest(),'appBytes':archive.stat().st_size,'nativeSha256':hashlib.sha256((app/'Contents/MacOS/libpclwindow.dylib').read_bytes()).hexdigest(),'signing':'ad hoc; not notarized','scope':'isolated offline test roots; no new manual world-play acceptance; accounts/game/runtime data excluded'}
(exports/'release-validation.json').write_text(json.dumps(receipt,ensure_ascii=False,indent=2)+'\n')
rows=''.join('<tr data-status="'+html.escape(r['status'])+'"><td>'+html.escape(r['page']+' / '+r['card'])+'</td><td>'+html.escape(r['label'])+'</td><td>'+html.escape(r['status'])+'</td><td>'+html.escape(r['implementation'])+'</td></tr>' for r in audit['fields'])
gallery=''.join('<figure><a href="'+url+'"><img loading="lazy" src="'+url+'" alt="'+html.escape(name)+'"></a><figcaption>'+html.escape(name)+'</figcaption></figure>' for name,url in images)
options=''.join('<option>'+html.escape(k)+'</option>' for k in audit['counts'])
page='''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>PCL Mac 0.22 实测与逐项核对</title><style>body{font:16px/1.65 system-ui,sans-serif;margin:0;background:#edf4fc;color:#24354a}main{max-width:1260px;margin:auto;padding:32px}h1{color:#146bc1}section{background:white;border-radius:12px;padding:22px;margin:20px 0}table{width:100%;border-collapse:collapse;font-size:14px}td,th{border-bottom:1px solid #d9e3ef;padding:10px;vertical-align:top;text-align:left}th{background:#e3efff}input,select{padding:10px;border:1px solid #bdcfe4;border-radius:6px;margin:6px}#gallery{display:grid;grid-template-columns:repeat(auto-fit,minmax(320px,1fr));gap:18px}figure{margin:0}img{width:100%;border:1px solid #d5e0ef}code{word-break:break-all}a{color:#0963bd}.note{background:#fff4dd;padding:16px;border-radius:10px}</style><main><h1>PCL Mac 0.22</h1><p>从设置存储到实际窗口、播放器、下载器和文件事务的逐项验证。</p><section><h2>本轮交付</h2><p>原 40 个未完成项：<b>21 项直接绑定，14 项部分实现，1 项固定隐私行为，4 项仍未完成。</b>全部 121 项中 73 项直接绑定；其他状态保留，不合并成“已完成”。</p><p>228 项核心回归；GUI 检查 <b>UI_COUNT</b> 项。Minecraft 1.12.2 / 1.21.1 / 26.3 完成图形初始化，真实窗口标题、最大化及 nice=10 有回执。五种启动器可见性实测，独立子进程在启动器退出后继续渲染。</p><p class="note">本轮没有重新进行世界内人工操作、存档保存和重进验收。自动测试主动停止产生的 137 不能当作正常退出。皮肤预设改变本地材质，保持 UUID 与手臂模型；原版外部服务、WPF XAML 与其他部分差异仍列在下表。</p><p><a href="0.22实现与验收.md">实现、修复与验收边界</a> · <a href="evidence/release-validation.json">发布校验</a> · <a href="evidence/test-results.json">回归结果</a> · <a href="evidence/local-settings-ui.json">新设置 GUI</a> · <a href="evidence/local-settings-reopen.json">新进程恢复</a> · <a href="evidence/windows-layout-ui.json">在线资源与导航</a></p><p>应用 SHA256：<code>APP_HASH</code></p></section><section><h2>121 项核对表</h2><p>同一份 Windows 基线，不把新增入口本身当作完成。</p><input id="q" placeholder="搜索功能或缺口"><select id="status"><option value="">全部状态</option>OPTIONS</select><table><thead><tr><th>页面 / 卡片</th><th>原版项</th><th>状态</th><th>实现 / 限制</th></tr></thead><tbody>ROWS</tbody></table></section><section><h2>真实发布包截图</h2><p>点击查看原图。测试样例包含自定义标题图片、动态配色与背景；这些不会写入用户默认配置。</p><div id="gallery">GALLERY</div></section></main><script>function filter(){const q=document.querySelector('#q').value.toLowerCase(),s=document.querySelector('#status').value;document.querySelectorAll('tbody tr').forEach(r=>r.hidden=!!((s&&r.dataset.status!==s)||!r.textContent.toLowerCase().includes(q)))}document.querySelector('#q').oninput=filter;document.querySelector('#status').onchange=filter;</script></html>'''.replace('UI_COUNT',str(receipt['uiChecks'])).replace('APP_HASH',receipt['appSha256']).replace('OPTIONS',options).replace('ROWS',rows).replace('GALLERY',gallery)
(review/'index.html').write_text(page)
target=root/'artifacts/PCL-Mac-0.22-verification.zip'
with zipfile.ZipFile(target,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as z:
    for file in sorted(review.rglob('*')):
        if file.is_file():z.write(file,Path('PCL-Mac-0.22-verification')/file.relative_to(review))
with zipfile.ZipFile(target) as z:assert z.testzip() is None
print(json.dumps(receipt,ensure_ascii=False,indent=2))
