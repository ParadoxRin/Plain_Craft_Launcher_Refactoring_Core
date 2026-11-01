"""Assemble unchanged accepted artifacts plus source, SDK workspaces and developer documentation."""
import argparse,hashlib,json,pathlib,re,shutil,subprocess,tempfile,zipfile,os,stat
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--release',type=pathlib.Path,required=True)
p.add_argument('--sdk-feed',type=pathlib.Path,required=True)
p.add_argument('--output',type=pathlib.Path,required=True)
a=p.parse_args();root=pathlib.Path(__file__).resolve().parent.parent;out=a.output.resolve();release=a.release.resolve();feed=a.sdk_feed.resolve()
if out.exists() or out.with_suffix('.zip').exists():raise SystemExit('FRESH_OUTPUT_REQUIRED')
if subprocess.check_output(['git','-C',str(root),'status','--porcelain']).strip():raise SystemExit('CLEAN_COMMITTED_SOURCE_REQUIRED')
def digest(p):
 h=hashlib.sha256()
 with p.open('rb') as f:
  for chunk in iter(lambda:f.read(1024*1024),b''):h.update(chunk)
 return h.hexdigest()
def read(p):return json.loads(p.read_text())
def save(p,value):p.write_text(json.dumps(value,ensure_ascii=False,indent=2)+'\n')
index=read(release/'release-manifest.json');sdk=read(feed/'sdk-manifest.json')
assert read(release/'unified-verification.json')['status']=='passed'
for item in index['packages'].values():
 name=item['file']
 if pathlib.Path(name).name!=name:raise SystemExit('INVALID_ASSET_NAME')
 assert (release/name).stat().st_size==item['size'] and digest(release/name)==item['sha256']
for item in sdk['packages']:assert digest(feed/item['file'])==item['sha256']
out.mkdir(parents=True);commit=subprocess.check_output(['git','-C',str(root),'rev-parse','HEAD'],text=True).strip()
(out/'source').mkdir()
with tempfile.TemporaryDirectory() as temp:
 tar=pathlib.Path(temp)/'source.tar'
 subprocess.run(['git','-C',str(root),'archive','--format=tar','--output',str(tar),commit],check=True)
 subprocess.run(['tar','-xf',str(tar),'-C',str(out/'source')],check=True)
for folder in ('packages','sdk','quickstart','runtime','evidence','workspaces'):(out/folder).mkdir()
# Copy only publisher outputs, excluding expanded deployments and all arbitrary extra files.
assets={item['file'] for item in index['packages'].values()}
assets.update(item['signature']['file'] for item in index['packages'].values() if 'signature' in item)
assets.update(['SHA256SUMS','app-feed.json','app-update-policy.json','client-feed.json','client-update-policy.json','host-update-policy.json','publisher-public-key.pem','release-manifest.json','release-manifest.signed.json','unified-verification.json'])
assets.update(p.name for p in release.glob('pcl-wiki-project-index-*.json'))
for name in sorted(assets):shutil.copy2(release/name,out/'packages'/name)
for item in sdk['packages']:shutil.copy2(feed/item['file'],out/'sdk')
shutil.copy2(feed/'sdk-manifest.json',out/'sdk');shutil.copy2(root/'sdk/README.md',out/'sdk')
for pth in (root/'examples/Pcl.DeveloperQuickstart').iterdir():
 if pth.is_file():shutil.copy2(pth,out/'quickstart')
(out/'quickstart/NuGet.Config').write_text('<configuration><packageSources><clear/><add key="handoff-sdk" value="../sdk"/></packageSources></configuration>\n')
shutil.copy2(root/'global.json',out/'global.json')
subprocess.run(['python3',str(root/'scripts/export-core-workspace.py'),'--output',str(out/'workspaces/core')],check=True)
subprocess.run(['python3',str(root/'scripts/export-client-workspace.py'),'--output',str(out/'workspaces/clients'),'--sdk-feed',str(feed)],check=True)
(out/'workspaces/clients/README.md').write_text('Independent client workspace. Build Clients.slnx with .NET 10. Local SDK packages are in sdk-packages/. Set PCL_HOST_EXECUTABLE to an extracted external Host. See ../../source/docs/开发者交接与调用指南.md.\n')
for name in ('0.42.1-五类分包收尾验收.json','0.42.1-项目拆分验收.json'):
 shutil.copy2(root/'docs'/name,out/'evidence')
shutil.copy2(release.parent/'verified/delivery-result.json',out/'evidence/delivery-result.json')
shutil.copy2(release.parent/'negative.json',out/'evidence/unified-negative.json')
# Read-only source index: exact location is authoritative; this is not a generated wire schema.
entries=[]
for folder in ('src/Pcl.Client','src/Pcl.Contract','src/Pcl.Desktop.Updates'):
 for file in sorted((root/folder).rglob('*.cs')):
  if any(part in ('bin','obj') for part in file.parts):continue
  text=file.read_text()
  for match in re.finditer(r'^\s*public\s+[^\n]*(?:\n[^\n]*){0,6}?(?=\{|;|=>)',text,re.M):
   value=match.group().strip()
   if len(value)<1400:entries.append({'file':'source/'+str(file.relative_to(root)),'line':text[:match.start()].count('\n')+1,'declaration':value})
rpc=[]
for file in [root/'src/Pcl.Host/Program.cs',*sorted((root/'src/Pcl.Core/Adapters/Pages').glob('*.cs'))]:
 text=file.read_text()
 for match in re.finditer(r'"([a-z]+\.[a-zA-Z0-9_.]+)"\s*(?:=>|:)',text):
  rpc.append({'method':match.group(1),'file':'source/'+str(file.relative_to(root)),'line':text[:match.start()].count('\n')+1})
save(out/'api-index.json',{'note':'Source declaration and dispatch lookup only; consult complete source for overloads, DTOs and lifecycle. Workspace methods are nested page operations, not direct top-level RPC. Special attach/hello handlers also require reading Program/PrivatePipeServer.','publicDeclarations':entries,'dispatchLocations':rpc})
classes=[
 {'package':'客户端包','artifact':'packages/pcl-0.42.1-osx-arm64-client.zip','entry':'PCL-MacPort --data-root <absolute-root>','configuration':'PCL_HOST_EXECUTABLE=<absolute-Host>','api':'HostClient / LauncherSession / WorkspaceApi','source':'source/src/Pcl.Client'},
 {'package':'Host核心包','artifact':'packages/pcl-0.42.1-osx-arm64-host.zip','entry':'Pcl.Host --update-probe; sessions launched by HostClient','api':'NDJSON RPC v1; Contract; hello capabilities','source':'source/src/Pcl.Host/Program.cs'},
 {'package':'数据与内容包','artifact':'packages/pcl-wiki-project-index-42102.zip','entry':'Pcl.ContentPack.dll verify|install|status|rollback','api':'content.update.status / content.update.begin; page workspace.search.page','source':'source/tools/Pcl.ContentPack/Program.cs'},
 {'package':'外部工具与运行环境','artifact':'runtime/manifest.json','entry':'Host-managed java and easytier processes; fetched on demand','api':'WorkspaceApi workspace.java.inspect/discover and workspace.link.*','source':'source/src/Pcl.Core/Adapters/Pages/WorkspaceExecutionAdapter.cs'},
 {'package':'开发与验证工具','artifact':'source/scripts and source/tests and source/tools','entry':'PCLCS.Tools.slnx; source/scripts/test-package-connectivity.sh','api':'CLI scripts and tests; not application RPC','source':'source/docs/开发者交接与调用指南.md'}]
save(out/'package-entrypoints.json',classes)
save(out/'runtime/manifest.json',{'binaryDelivery':'on-demand, not embedded in this handoff','java':{'verifiedVersion':'21.0.7','verifiedPlatform':'macOS aarch64','discoveryRpc':'workspace.java.inspect','selection':'Host JavaService uses metadata hashes and platform/version selection','source':'../source/src/Pcl.Core/JavaService.cs'},'easytier':{'version':'2.6.4','runtime':'macos-aarch64','url':'https://github.com/EasyTier/EasyTier/releases/download/v2.6.4/easytier-macos-aarch64-v2.6.4.zip','sha256':'4be1882d1aa36d31c1d6ba0596f2cf8a097e371f8da124212324b2e0f8df7e4b','size':30174017,'entrypoints':['easytier-core','easytier-cli'],'licenseSource':'https://github.com/EasyTier/EasyTier/blob/v2.6.4/LICENSE'},'nativeAssets':'Window bridge and compatibility agent are included where required in accepted packages; source/scripts/build-native.sh reproduces assets.'})
(out/'runtime/README.md').write_text('外部工具与运行环境\n\nJava/EasyTier 沿用 Host 的按需获取机制，本目录提供获取入口与校验清单，不捆绑第三方二进制或个人缓存。Java 与 .NET SDK/Host 自包含运行时不同。调用、真实验证及工具进程所有权见 ../source/docs/开发者交接与调用指南.md。离线开发时自行准备对应运行环境，再运行 test-package-connectivity.sh。\n')
(out/'NOTICE-CORRECTIONS.md').write_text('交接说明补充\n\n原已验收包内旧版 THIRD-PARTY-NOTICES 将 EasyTier 标为 Apache-2.0；核对 v2.6.4 标签根 LICENSE 后，本次源码说明更正为 GNU LGPL v3。官方依据：https://github.com/EasyTier/EasyTier/blob/v2.6.4/LICENSE 。本交接不附 EasyTier 二进制，按需从官方获取并保留其许可；已签名旧包原始字节保持不变。项目各部分许可仍以 source/LICENSE.md 及各上游文件为准，不把整个项目统称为 MIT。\n')
(out/'START_HERE.md').write_text('''# PCLCS 0.42.1 / build 425 开发交接包

1. 先读 [开发者交接与调用指南](source/docs/开发者交接与调用指南.md)。
2. 接口和生命周期约束见 [五类包连接规范](source/docs/五类包连接规范.md)。
3. 快速定位每个包：[入口表](package-entrypoints.json)；[源码 API 索引](api-index.json)。
4. 按指南从 packages 解出 Host，编译 quickstart 并连接。示例默认不安装游戏或登录账号。
5. 实际验收范围见 [收尾报告](source/docs/0.42.1-五类分包收尾.md)。

packages 是已验收原始产物；source 是附开发说明的完整已提交源码；workspaces 是独立维护入口。sdk 为本地 NuGet 包，不是 NuGet.org 发布。Java/EasyTier 在 runtime 中提供按需获取信息，第三方 NuGet 构建依赖不离线内置。

本包仅本地交接，未推送 GitHub。Mac 包为 ad-hoc 签名，更新地址为测试占位，不是生产频道。交接包不含任何签名私钥。外层 ZIP 不是自更新载荷。许可补充见 [NOTICE-CORRECTIONS](NOTICE-CORRECTIONS.md)。

完整性检查：在本目录执行 `shasum -a 256 -c SHA256SUMS`。这验证交接完整性；可信更新仍需按预置公钥验证 packages 内的签名清单。
''')
save(out/'handoff-manifest.json',{'version':index['version'],'clientBuild':425,'hostSequence':42102,'contentSequence':42102,'sourceCommit':commit,'acceptedRuntimeSource':'73cbda4; closeout evidence 655e987','runtimeArtifactsRebuilt':False,'releaseIndexSha256':digest(release/'release-manifest.json'),'sdkVersion':sdk['version'],'platform':'macOS arm64','packageClasses':classes,'privateKeysIncluded':False,'externalToolsIncludedAs':'on-demand acquisition manifest, not third-party binary cache'})
# Detect accidental local developer paths/private keys in generated handoff metadata, not historical source prose.
for file in list(out.glob('*.json'))+list(out.glob('*.md')):
 data=file.read_text()
 if str(root) in data or '-----BEGIN PRIVATE KEY-----' in data or '-----BEGIN RSA PRIVATE KEY-----' in data:raise SystemExit('HANDOFF_METADATA_PRIVACY_FAILED')
files=sorted(p for p in out.rglob('*') if p.is_file())
(out/'SHA256SUMS').write_text(''.join(digest(file)+'  '+file.relative_to(out).as_posix()+'\n' for file in files))
archive=out.with_suffix('.zip')
with zipfile.ZipFile(archive,'w',allowZip64=True) as z:
 for file in sorted(out.rglob('*')):
  if not file.is_file():continue
  name=out.name+'/'+file.relative_to(out).as_posix()
  if file.is_symlink():
   info=zipfile.ZipInfo(name);info.create_system=3;info.external_attr=(stat.S_IFLNK|0o777)<<16;z.writestr(info,os.readlink(file))
  else:z.write(file,name,compress_type=zipfile.ZIP_STORED if file.suffix in ('.zip','.nupkg') else zipfile.ZIP_DEFLATED,compresslevel=6)
with zipfile.ZipFile(archive) as z:
 if z.testzip() is not None:raise SystemExit('ZIP_CRC_FAILED')
archive.with_suffix('.zip.sha256').write_text(digest(archive)+'  '+archive.name+'\n')
print(json.dumps({'archive':str(archive),'bytes':archive.stat().st_size,'sha256':digest(archive),'files':len(files)+1,'sourceCommit':commit}))
