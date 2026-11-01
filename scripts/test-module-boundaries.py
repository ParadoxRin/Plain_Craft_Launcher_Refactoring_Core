"""Positive and fault-injected build-boundary checks without modifying the checkout."""
import pathlib,subprocess,tempfile,shutil,json
root=pathlib.Path(__file__).resolve().parent.parent
command=['python3',str(root/'scripts/check-module-boundaries.py')]
subprocess.run(command,check=True,stdout=subprocess.DEVNULL)
with tempfile.TemporaryDirectory() as temporary:
 stage=pathlib.Path(temporary)
 for source in list((root/'src').glob('*/*.csproj'))+[root/'examples/Pcl.MinimalClient/Pcl.MinimalClient.csproj']:
  target=stage/source.relative_to(root);target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(source,target)
 cases=[('src/Pcl.Desktop/Pcl.Desktop.csproj','<ProjectReference Include="../Pcl.Host/Pcl.Host.csproj" ReferenceOutputAssembly="false" />'),
        ('src/Pcl.Cli/Pcl.Cli.csproj','<ProjectReference Include="../Pcl.Core/Pcl.Core.csproj" />'),
        ('src/Pcl.Client/Pcl.Client.csproj','<Compile Include="../Pcl.Desktop/Program.cs" />')]
 for name,injection in cases:
  path=stage/name;original=path.read_text();path.write_text(original.replace('</Project>','<ItemGroup>'+injection+'</ItemGroup></Project>'))
  result=subprocess.run(command+['--root',str(stage)],capture_output=True,text=True)
  assert result.returncode!=0 and json.loads(result.stdout)['violations']
  path.write_text(original)
print('MODULE_BOUNDARY_TESTS_PASSED: positive + 3 injected violations')
