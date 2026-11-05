"""Export independently buildable clients; no Core, Host, vendor, SDK source, or repository Git metadata."""
import argparse,json,pathlib,shutil,subprocess,xml.etree.ElementTree as ET
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--output',type=pathlib.Path,required=True)
p.add_argument('--sdk-feed',type=pathlib.Path,required=True)
a=p.parse_args();root=pathlib.Path(__file__).resolve().parent.parent;out=a.output.resolve()
if out.exists(): raise SystemExit('FRESH_CLIENT_WORKSPACE_REQUIRED')
feed=a.sdk_feed.resolve();version=json.loads((root/'release.json').read_text())['sdkVersion']
for name in ('Pcl.Client','Pcl.Contract'):
 if not (feed/(name+'.'+version+'.nupkg')).is_file(): raise SystemExit('SDK_PACKAGES_REQUIRED')
out.mkdir(parents=True)
local_feed=out/'sdk-packages';local_feed.mkdir()
for name in ('Pcl.Client','Pcl.Contract'):
 shutil.copy2(feed/(name+'.'+version+'.nupkg'),local_feed)
shutil.copy2(feed/'sdk-manifest.json',local_feed)
feed=local_feed
# Export only tracked source paths so bin/obj, local secrets and runtime data never enter this workspace.
scopes=['src/Pcl.Desktop','src/Pcl.Desktop.Updates','src/Pcl.Cli','examples/Pcl.MinimalClient','tests/Pcl.MinimalClient.Tests']
paths=subprocess.check_output(['git','-C',str(root),'ls-files','-z','--',*scopes]).decode().split('\0')
for name in filter(None,paths):
 source=root/name
 if source.is_file():
  target=out/name;target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(source,target)
for name in ['Directory.Build.props','global.json','release.json','LICENSE','LICENSE.md','LICENSE-MIT.txt','PCL-LICENCE.txt',
             'docs/windows-layout-baseline.json','docs/data/PCL2完整功能矩阵.json','docs/THIRD-PARTY-NOTICES.md',
             'src/SharedProtocol/PrivatePipePath.cs']:
 target=out/name;target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(root/name,target)
for name in ['src/Pcl.Native/window-agent.m', 'scripts/build-native.sh']:
 target=out/name;target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(root/name,target)
# Normalise away unused source ProjectReferences; this workspace can never fall back to a sibling core checkout.
for project in out.rglob('*.csproj'):
 tree=ET.parse(project)
 for group in tree.getroot():
  for item in list(group):
   if item.tag=='ProjectReference' and pathlib.Path(item.attrib['Include']).stem in ('Pcl.Client','Pcl.Contract'):group.remove(item)
 ET.indent(tree,space='  ');tree.write(project,encoding='unicode')
(out/'Directory.Build.targets').write_text('<Project><PropertyGroup><UsePclSdkPackages>true</UsePclSdkPackages></PropertyGroup></Project>\n')
# PackageReference conditions are evaluated before targets, so pin package mode in props too.
p=out/'Directory.Build.props';p.write_text(p.read_text().replace('<PropertyGroup>','<PropertyGroup><UsePclSdkPackages>true</UsePclSdkPackages>',1))
config=ET.Element('configuration');sources=ET.SubElement(config,'packageSources');ET.SubElement(sources,'clear')
ET.SubElement(sources,'add',key='pclcs-local-sdk',value='sdk-packages')
ET.SubElement(sources,'add',key='nuget.org',value='https://api.nuget.org/v3/index.json')
ET.ElementTree(config).write(out/'NuGet.Config',encoding='unicode')
(out/'Clients.slnx').write_text('<Solution><Project Path="src/Pcl.Desktop/Pcl.Desktop.csproj" /><Project Path="src/Pcl.Desktop.Updates/Pcl.Desktop.Updates.csproj" /><Project Path="src/Pcl.Cli/Pcl.Cli.csproj" /><Project Path="examples/Pcl.MinimalClient/Pcl.MinimalClient.csproj" /></Solution>\n')
(out/'README.md').write_text('PCLCS independent client workspace\n\nOn macOS ARM64 first run bash scripts/build-native.sh. Build Clients.slnx with .NET 10. Set PCL_HOST_EXECUTABLE to a separately supplied Host.\nSDK packages: '+str(feed)+'\nNo core source is included. This is a local export, not a new remote repository.\n')
print('INDEPENDENT_CLIENT_WORKSPACE_EXPORTED',out)
