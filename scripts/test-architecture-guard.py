"""定向验证守卫能拒绝真实编译后的越界符号及项目引用；不启动桌面应用。"""
import json
import os
import pathlib
import shutil
import subprocess

root = pathlib.Path(__file__).resolve().parent.parent
output = root / os.environ.get('PCL_ARCH_TEST_OUTPUT','artifacts/architecture-contract-0.25')
output.mkdir(parents=True, exist_ok=True)
sdk = root / '.tooling/dotnet/dotnet'
env = dict(os.environ, DOTNET_CLI_HOME=str(root / '.cache/dotnet-home'))
guard = root / 'scripts/ArchitectureGuard/bin/Debug/net10.0/ArchitectureGuard.dll'
desktop = root / 'src/Pcl.Desktop/bin/Debug/net10.0/PCL-MacPort.dll'
fixture = output / 'negative-fixture'
fixture.mkdir(exist_ok=True)
(fixture / 'Fixture.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>GuardFaultFixture</AssemblyName></PropertyGroup><ItemGroup><ProjectReference Include="{root}/src/Pcl.Core/Pcl.Core.csproj"/><ProjectReference Include="{root}/src/Pcl.Application/Pcl.Application.csproj"/><ProjectReference Include="{root}/src/Pcl.Client/Pcl.Client.csproj"/></ItemGroup></Project>''')
(fixture / 'Fixture.cs').write_text('''using Alias = Pcl.Core.Application.Installation.VersionInstallService;
namespace Pcl.Desktop;
// 故障注入仅在独立测试程序集，真实 Shell 不加入违规代码。
public sealed class ComponentManagerWindow
{
    public Pcl.Core.MinecraftService? ForbiddenLegacyField;
    public Pcl.Core.MinecraftService Bad() => new(null!);
}
public sealed class InstanceWorkspace { public Pcl.Core.InstanceManager? ForbiddenField; }
public sealed class MultiplayerWindow { public Pcl.Core.EasyTierService? ForbiddenField; }
public sealed class OptiFabricWindow { public Pcl.Core.OptiFabricService? ForbiddenField; }
public sealed class MainWindow { public Pcl.Core.AccountManager? ForbiddenField; public void LoginClick() { using var auth = new Pcl.Core.MicrosoftAuthentication(); } }
public sealed class ResourceBrowser { public Pcl.Core.ResourceCatalog? ForbiddenField; }
public sealed class SkinManagerWindow { public Pcl.Core.OfflineSkinService? ForbiddenField; }
public sealed class AccountToolsWindow { public Pcl.Core.AccountManager? ForbiddenField; }
public sealed class UnifiedSearchWindow { public Pcl.Core.ResourceCatalog? ForbiddenField; }
public sealed class CurseForgeWindow { public Pcl.Core.CurseForgeCatalog? ForbiddenField; }
public sealed class SettingsHelpWindow { public Pcl.Core.AppearanceService? ForbiddenField; }
public sealed class CacheNetworkWindow { public Pcl.Core.TemporaryDownloadCache? ForbiddenField; }
public sealed class LocalResourcesWindow { public Pcl.Core.LocalResourceService? ForbiddenField; }
public sealed class PackManagerWindow { public Pcl.Core.PortableModpackService? ForbiddenField; }
public sealed class PackSelectionWindow { public Pcl.Core.PortablePackPlan? ForbiddenField; }
public sealed class GuardFaultFixture
{
    public Pcl.Client.HostClient? Client;
    public Alias Construct(Pcl.Core.Application.Operations.InstallOperationService runner)
        => new(runner, new("osx", "aarch64", "test"));
    public System.Threading.Tasks.Task Call(Alias service) => service.StopAsync();
}
''')
(fixture / 'BadBootstrap.cs').write_text('namespace Pcl.Desktop.Bootstrap; public sealed class Composition { public Pcl.Core.MinecraftService? ForbiddenField; }')
# 使用实际 Avalonia 类型构造绕过统一导航入口的调用，不靠源码字符串测试。
project = fixture / 'Fixture.csproj'
refs = ''.join(f'<Reference Include="{dll.stem}"><HintPath>{dll}</HintPath></Reference>' for dll in desktop.parent.glob('Avalonia*.dll'))
project.write_text(project.read_text().replace('</Project>', '<ItemGroup>' + refs + '</ItemGroup></Project>'))
(fixture / 'BadNavigation.cs').write_text('namespace Pcl.Desktop; public static class BadNavigation { public static System.Threading.Tasks.Task Open(Avalonia.Controls.Window owner) => new Avalonia.Controls.Window().ShowDialog(owner); }')
with (output / 'negative-fixture-build.log').open('w') as log:
    subprocess.run([str(sdk), 'build', str(fixture / 'Fixture.csproj'), '--nologo', '-m:1', '-nr:false', '-p:UseSharedCompilation=false', '-p:NuGetAudit=false', '--source', str(root / '.cache/nuget')], cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT, check=True)


def run_case(name, project_root, assembly, expected_code=None):
    report = output / (name + '.json')
    result = subprocess.run([str(sdk), str(guard), str(project_root), str(assembly), str(report)], cwd=root, env=env, capture_output=True, text=True)
    data = json.loads(report.read_text())
    if expected_code is None:
        assert result.returncode == 0 and data['status'] == 'passed'
    else:
        assert result.returncode != 0 and any(x.startswith(expected_code) for x in data['violations'])
    return {'case': name, 'status': 'passed', 'guardExitCode': result.returncode}


for dll in desktop.parent.glob('*.dll'):
    if not (fixture / 'bin/Debug/net10.0' / dll.name).exists():
        shutil.copy2(dll, fixture / 'bin/Debug/net10.0' / dll.name)

results = [run_case('positive', root, desktop)]
results.append(run_case('negative-symbols', root, fixture / 'bin/Debug/net10.0/GuardFaultFixture.dll', 'SHELL_IMPLEMENTATION_USE:'))
negative = json.loads((output / 'negative-symbols.json').read_text())['violations']
for name in ('MainWindow', 'Bootstrap.Composition', 'ResourceBrowser', 'SkinManagerWindow', 'AccountToolsWindow', 'MultiplayerWindow', 'OptiFabricWindow'):
    assert any('Pcl.Desktop.' + name + '.ForbiddenField' in item for item in negative), name
assert any(item.startswith('SHELL_EXECUTION_USE:Pcl.Desktop.MainWindow.LoginClick') for item in negative)
assert any(item.startswith('UNMANAGED_DIALOG_PRESENTATION:') for item in negative)
# 只更改测试副本的 Client 引用，验证真实项目引用检查，保留生产 csproj。
clone = output / 'negative-project-root'
for name in ('Pcl.Domain','Pcl.Ports','Pcl.Application','Pcl.Contract', 'Pcl.Core', 'Pcl.Client', 'Pcl.Desktop', 'Pcl.Host'):
    destination = clone / 'src' / name
    destination.mkdir(parents=True, exist_ok=True)
    shutil.copy2(root / 'src' / name / (name + '.csproj'), destination)
client = clone / 'src/Pcl.Client/Pcl.Client.csproj'
client.write_text(client.read_text().replace('</Project>', '<ItemGroup><ProjectReference Include="../Pcl.Host/Pcl.Host.csproj" /></ItemGroup></Project>'))
results.append(run_case('negative-project-reference', clone, desktop, 'PROJECT_REFERENCES_CHANGED:Pcl.Client'))
client.write_text((root / 'src/Pcl.Client/Pcl.Client.csproj').read_text().replace('</Project>', '<ItemGroup><Reference Include="Pcl.Core" /></ItemGroup></Project>'))
results.append(run_case('negative-manual-reference', clone, desktop, 'UNREVIEWED_MANUAL_REFERENCE:Pcl.Client'))
client.write_text((root / 'src/Pcl.Client/Pcl.Client.csproj').read_text())
desktop_project = clone / 'src/Pcl.Desktop/Pcl.Desktop.csproj'
original_desktop = (root / 'src/Pcl.Desktop/Pcl.Desktop.csproj').read_text()
desktop_project.write_text(original_desktop.replace('</Project>', '<ItemGroup><ProjectReference Include="../Pcl.Core/Pcl.Core.csproj" /></ItemGroup></Project>'))
results.append(run_case('negative-desktop-project-reference', clone, desktop, 'PROJECT_REFERENCES_CHANGED:Pcl.Desktop'))
desktop_project.write_text(original_desktop.replace('</Project>', '<ItemGroup><Reference Include="Pcl.Core" /></ItemGroup></Project>'))
results.append(run_case('negative-desktop-manual-reference', clone, desktop, 'UNREVIEWED_MANUAL_REFERENCE:Pcl.Desktop'))
# Domain 反例在独立程序集副本构建，保留所有现有值类型，绝不改动生产源码。
domain = output / 'domain-negative'
domain.mkdir(exist_ok=True)
(domain / 'Pcl.Domain.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Pcl.Domain</AssemblyName></PropertyGroup><ItemGroup><Compile Include="{root}/src/Pcl.Domain/**/*.cs" Exclude="{root}/src/Pcl.Domain/obj/**;{root}/src/Pcl.Domain/bin/**" /></ItemGroup></Project>')
(domain / 'BadDomain.cs').write_text('namespace Pcl.Domain; public static class BadDomain { public static bool Read() => System.IO.File.Exists("fixture"); }')
with (output / 'negative-domain-build.log').open('w') as log:
    subprocess.run([str(sdk), 'build', str(domain / 'Pcl.Domain.csproj'), '-m:1', '-nr:false', '-p:UseSharedCompilation=false', '-p:NuGetAudit=false', '--source', str(root / '.cache/nuget')], cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
# Guard uses only metadata: a copied bundle with the injected Domain exposes the forbidden IL.
bundle = output / 'negative-domain-bundle'
shutil.copytree(desktop.parent, bundle, dirs_exist_ok=True)
shutil.copy2(domain / 'bin/Debug/net10.0/Pcl.Domain.dll', bundle / 'host/Pcl.Domain.dll')
results.append(run_case('negative-domain-io', root, bundle / desktop.name, 'DOMAIN_EXTERNAL_DEPENDENCY:'))
(output / 'guard-tests.json').write_text(json.dumps({'tests': results}, indent=2))
print(json.dumps(results))
