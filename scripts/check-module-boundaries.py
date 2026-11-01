"""Check maintenance boundaries, including build-only references (not just runtime DLL references)."""
import argparse,json,pathlib,xml.etree.ElementTree as ET
p=argparse.ArgumentParser(description=__doc__);p.add_argument('--root',type=pathlib.Path,default=pathlib.Path(__file__).resolve().parent.parent);p.add_argument('--report',type=pathlib.Path)
a=p.parse_args();root=a.root.resolve()
allowed={
 'src/Pcl.Contract/Pcl.Contract.csproj':[],
 'src/Pcl.Client/Pcl.Client.csproj':['Pcl.Contract'],
 'src/Pcl.Domain/Pcl.Domain.csproj':[],
 'src/Pcl.Ports/Pcl.Ports.csproj':['Pcl.Domain'],
 'src/Pcl.Application/Pcl.Application.csproj':['Pcl.Domain','Pcl.Ports','Pcl.Contract'],
 'src/Pcl.Core/Pcl.Core.csproj':['MeloongCore','PCLCS','Pcl.Legacy','Pcl.Contract','Pcl.Domain','Pcl.Ports'],
 'src/Pcl.Host/Pcl.Host.csproj':['Pcl.Core','Pcl.Contract','Pcl.Application'],
 'src/Pcl.Desktop.Updates/Pcl.Desktop.Updates.csproj':['Pcl.Contract'],
 'src/Pcl.Desktop/Pcl.Desktop.csproj':['Pcl.Client','Pcl.Contract','Pcl.Desktop.Updates'],
 'src/Pcl.Cli/Pcl.Cli.csproj':['Pcl.Client','Pcl.Contract'],
 'examples/Pcl.MinimalClient/Pcl.MinimalClient.csproj':['Pcl.Client','Pcl.Contract']}
graph={};violations=[]
for name,references in allowed.items():
 project=ET.parse(root/name)
 actual=sorted(pathlib.Path(e.attrib['Include']).stem for e in project.findall('.//ProjectReference'))
 graph[name]=actual
 if actual!=sorted(references):violations.append('PROJECT_BUILD_BOUNDARY:'+name)
 if project.findall('.//Reference'):violations.append('MANUAL_ASSEMBLY_REFERENCE:'+name)
 if name.startswith(('src/Pcl.Client/','src/Pcl.Contract/')) and project.findall('.//PackageReference'):
  violations.append('SDK_EXTERNAL_DEPENDENCY:'+name)
 for source in project.findall('.//Compile'):
  include=source.attrib.get('Include','')
  if include.startswith('../') and not (root/name).parent.joinpath(include).resolve().is_relative_to(root/'src/SharedProtocol'):
   violations.append('CROSS_MODULE_SOURCE_LINK:'+name+':'+include)
if list((root/'src/Pcl.Client').glob('SelfUpdate/*.cs')):violations.append('DESKTOP_UPDATER_IN_SDK')
result={'status':'failed' if violations else 'passed','scope':'All project references including ReferenceOutputAssembly=false and cross-module source links','graph':graph,'violations':violations}
if a.report:a.report.parent.mkdir(parents=True,exist_ok=True);a.report.write_text(json.dumps(result,indent=2)+'\n')
print(json.dumps(result,indent=2));raise SystemExit(bool(violations))
