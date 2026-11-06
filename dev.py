#!/usr/bin/env python3
"""Repository development entry point; Python 3.9+, no extra packages."""
import argparse
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parent
HOST = 'src/Pcl.Host/Pcl.Host.csproj'
EXAMPLE = 'examples/Pcl.MinimalClient/Pcl.MinimalClient.csproj'
TARGETS = {'host': HOST, 'example': EXAMPLE, 'core': 'PCLCS.Core.slnx',
           'client': 'PCLCS.Clients.slnx', 'sdk': 'PCLCS.ClientSdk.slnx'}
QUICK = ('test-module-boundaries.py', 'test-component-packages.py', 'test-core-baseline.py')


def environment():
    env = dict(os.environ)
    env.setdefault('DOTNET_CLI_HOME', str(ROOT / '.cache/dotnet-home'))
    env.update(DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1',
               AVALONIA_TELEMETRY_OPTOUT='1')
    return env


def run(args, diagnostic=False):
    print('+ ' + subprocess.list2cmdline([str(a) for a in args]), flush=True, file=sys.stderr)
    subprocess.run([str(a) for a in args], cwd=ROOT, env=environment(), check=True,
                   stdout=sys.stderr if diagnostic else None)


def sdk():
    explicit = os.environ.get('DOTNET')
    local = ROOT / '.tooling/dotnet' / ('dotnet.exe' if os.name == 'nt' else 'dotnet')
    candidate = explicit or (str(local) if local.is_file() else shutil.which('dotnet'))
    resolved = shutil.which(candidate) if candidate else None
    if not resolved:
        raise RuntimeError('未找到 .NET SDK。运行 python3 dev.py setup，或将 DOTNET 设为 SDK 可执行文件。')
    result = subprocess.run([resolved, '--version'], cwd=ROOT, env=environment(),
                            capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError('SDK 无法满足 global.json：\n' + result.stdout + result.stderr)
    expected = json.loads((ROOT / 'global.json').read_text())['sdk']['version']
    actual = result.stdout.strip().splitlines()[-1]
    # global.json allows latestPatch within this feature band, not arbitrary .NET 10.
    try:
        want, got = [int(x) for x in expected.split('.')], [int(x) for x in actual.split('.')]
        compatible = got[:2] == want[:2] and got[2] // 100 == want[2] // 100 and got[2] >= want[2]
    except ValueError:
        compatible = False
    if not compatible:
        raise RuntimeError(f'SDK {actual} 不符合 {expected} / latestPatch。')
    return str(Path(resolved).resolve()), actual


def doctor():
    failures = []
    print(f'仓库：{ROOT}\n平台：{platform.system()} {platform.machine()}\nPython：{platform.python_version()}')
    for name in ('git',):
        found = shutil.which(name)
        print(f'{name}: {found or "MISSING"}')
        if not found:
            failures.append(name)
    try:
        executable, version = sdk()
        print(f'.NET SDK: {version} ({executable})')
    except RuntimeError as error:
        print(error)
        failures.append('SDK')
    if os.name != 'nt':
        compiler = shutil.which('cc')
        print(f'C compiler (Host 日志辅助程序): {compiler or "MISSING"}')
        if not compiler:
            failures.append('cc；macOS 请安装 Xcode Command Line Tools，Linux 请安装 C11 编译器')
    print('未检查：NuGet 网络、GUI、Java、真人账户和发布签名；这些不属于基础环境检查。')
    if failures:
        raise RuntimeError('缺少或不可用：' + ', '.join(failures))


def build(dotnet, project):
    run([dotnet, 'build', project, '-c', 'Release', '-m:1', '-nr:false', '-p:UsedAvaloniaProducts='], diagnostic=True)


def dll(project):
    path = Path(project)
    return ROOT / path.parent / 'bin/Release/net10.0' / (path.stem + '.dll')


def fresh(label):
    parent = ROOT / 'artifacts/dev'
    parent.mkdir(parents=True, exist_ok=True)
    session = Path(tempfile.mkdtemp(prefix=label + '-', dir=parent)).resolve()
    print(f'本次隔离输出：{session}', flush=True, file=sys.stderr)
    return session


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    commands.add_parser('doctor', help='检查基础开发环境，不下载依赖')
    commands.add_parser('setup', help='复用兼容 SDK；缺少时在 Apple Silicon 上安装仓库私有 SDK')
    p = commands.add_parser('build', help='构建指定开发目标')
    p.add_argument('target', choices=TARGETS)
    commands.add_parser('example', help='构建并运行最小交互客户端，由 SDK 管理隔离 Host')
    commands.add_parser('host', help='构建并运行隔离 stdio Host；普通开发请用 example')
    p = commands.add_parser('test', help='执行实际断言，失败返回非零')
    p.add_argument('suite', choices=('quick', 'core', 'contract'), nargs='?', default='quick')
    args = parser.parse_args()
    if args.command == 'doctor':
        doctor()
        return
    if args.command == 'setup':
        try:
            executable, version = sdk()
            print(f'复用 SDK {version}: {executable}')
        except RuntimeError:
            if os.environ.get('DOTNET'):
                raise RuntimeError('DOTNET 指定的 SDK 不可用；请修正或取消该环境变量后重试。')
            if platform.system() != 'Darwin' or platform.machine() not in ('arm64', 'aarch64'):
                raise RuntimeError('自动安装目前仅支持 Apple Silicon。请安装 global.json 指定的官方 SDK，然后运行 doctor。')
            run([sys.executable, ROOT / 'scripts/bootstrap-dotnet.py'])
            sdk()
        return
    if args.command == 'test' and args.suite == 'quick':
        for script in QUICK:
            run([sys.executable, ROOT / 'scripts' / script])
        print('PASS: quick（仓库边界/打包夹具/基线校验；未执行 .NET、Host 或游戏测试）')
        return
    dotnet, _ = sdk()
    if args.command == 'build':
        build(dotnet, TARGETS[args.target])
    elif args.command in ('example', 'host'):
        build(dotnet, HOST)
        if args.command == 'example':
            build(dotnet, EXAMPLE)
        session = fresh(args.command)
        if args.command == 'example':
            print('建议输入 accounts、exit；安装和启动游戏仅在显式输入相应命令后执行。', flush=True)
            run([dotnet, dll(EXAMPLE), '--host-dll', dll(HOST), '--dotnet', dotnet,
                 '--data-root', session / 'data'])
        else:
            print('stdin/stdout 为 NDJSON 协议；Ctrl+D / EOF 结束。协议见 docs/开发指南.md。', file=sys.stderr)
            run([dotnet, dll(HOST), '--data-root', session / 'data'])
    elif args.suite == 'core':
        project = 'tests/Pcl.Tests/Pcl.Tests.csproj'
        build(dotnet, project)
        run([dotnet, dll(project), fresh('core') / 'data'])
        print('PASS: core（控制台核心断言；不代表真人账户或游戏世界验收）')
    else:
        project = 'tests/Pcl.MinimalClient.Tests/Pcl.MinimalClient.Tests.csproj'
        build(dotnet, HOST)
        build(dotnet, project)
        session = fresh('contract')
        run([dotnet, dll(project), dotnet, dll(HOST), session / 'data', session / 'result.json', 'contract'])
        print('PASS: contract（真实 Host 契约；可能请求元数据，不完成游戏安装）')


if __name__ == '__main__':
    try:
        main()
    except (RuntimeError, OSError) as error:
        print(f'ERROR: {error}', file=sys.stderr)
        sys.exit(1)
    except subprocess.CalledProcessError as error:
        print(f'FAILED: 子命令退出码 {error.returncode}，请查看上方输出。', file=sys.stderr)
        sys.exit(error.returncode if error.returncode > 0 else 1)
    except KeyboardInterrupt:
        print('\n已中断。', file=sys.stderr)
        sys.exit(130)
