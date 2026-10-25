"""编译反例：只引用 Client 的消费者不能构造旧 Core 服务。"""
from pathlib import Path
import os, subprocess, json
root=Path(__file__).resolve().parent.parent
out=root/os.environ.get('PCL_ARCH_TEST_OUTPUT','artifacts/architecture-contract-0.25')/'isolation-negative'
out.mkdir(parents=True,exist_ok=True)
(out/'BadConsumer.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include="{root}/src/Pcl.Client/Pcl.Client.csproj" /></ItemGroup></Project>')
(out/'BadConsumer.cs').write_text('namespace BadConsumer; public static class Factory { public static object Create() => new Pcl.Core.MinecraftService(null!); }')
result=subprocess.run([str(root/'.tooling/dotnet/dotnet'),'build',str(out/'BadConsumer.csproj'),'--nologo'],cwd=root,env=dict(os.environ,DOTNET_CLI_HOME=str(root/'.cache/dotnet-home')),capture_output=True,text=True)
(out/'build.log').write_text(result.stdout+result.stderr)
assert result.returncode!=0 and ('CS0234' in result.stdout or 'CS0246' in result.stdout)
(out.parent/'compiler-isolation.json').write_text(json.dumps({'status':'passed','illegalCoreConsumerCompiled':False,'compilerExitCode':result.returncode},indent=2))
print('Client-only consumer cannot compile Core service construction')
