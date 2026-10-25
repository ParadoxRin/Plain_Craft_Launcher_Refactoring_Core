# -*- coding: utf-8 -*-
"""Compile real Java fixtures and ask the JDK for module identities; no game/account data is read."""
from pathlib import Path
import subprocess, zipfile, json, hashlib
root = Path(__file__).resolve().parents[1]
out = root / 'artifacts/verification-0.34/fixtures'
out.mkdir(parents=True, exist_ok=True)
jdk = root / 'runtime/apple-silicon-1.21.1/runtime/java-17-macos-arm64/jre.bundle/Contents/Home/bin'

def run(*args):
    """调用本地JDK编译器或模块检查器；失败立即停止，不把编译当作游戏验收。"""
    return subprocess.check_output([str(x) for x in args], stderr=subprocess.STDOUT, text=True)
for (folder, name) in [('base', 'pcl.base'), ('nine', 'pcl.nine'), ('seventeen', 'pcl.seventeen'), ('unicode', '测试.模块')]:
    d = out / folder
    d.mkdir(exist_ok=True)
    (d / 'module-info.java').write_text('module ' + name + ' {}\n')
    run(jdk / 'javac', '--release', '9', '-d', d, d / 'module-info.java')
src = out / 'Delegate.java'
src.write_text('package pcl.compat;\n// Integration fixture: keep every game argument unchanged and print only a fixed non-sensitive marker.\npublic final class Delegate {\n  public static void main(String[] args) throws Exception {\n    System.out.println("PCL034_CUSTOM_ENTRYPOINT");\n    Class.forName("net.minecraft.client.main.Main").getMethod("main", String[].class).invoke(null, (Object) args);\n  }\n}\n')
run(jdk / 'javac', '--release', '17', '-d', out, src)

def jar(name, entries):
    """构造隔离测试JAR，明确写入条目，不接触用户现有游戏归档。"""
    p = out / name
    with zipfile.ZipFile(p, 'w', zipfile.ZIP_DEFLATED) as z:
        for (key, value) in entries.items():
            z.writestr(key, value)
    return p
wrapper = jar('delegate-1.0.jar', {'pcl/compat/Delegate.class': (out / 'pcl/compat/Delegate.class').read_bytes()})
lib = {'name': 'pcl.compat:delegate:1.0', 'downloads': {'artifact': {'path': 'pcl/compat/delegate/1.0/delegate-1.0.jar', 'url': 'https://fixture.invalid/delegate-1.0.jar', 'sha1': hashlib.sha1(wrapper.read_bytes()).hexdigest(), 'size': wrapper.stat().st_size}}}
(out / 'library.json').write_text(json.dumps(lib, indent=2))
base = (out / 'base/module-info.class').read_bytes()
nine = (out / 'nine/module-info.class').read_bytes()
seventeen = (out / 'seventeen/module-info.class').read_bytes()
jar('explicit.jar', {'module-info.class': base, 'META-INF/MANIFEST.MF': 'Manifest-Version: 1.0\r\nAutomatic-Module-Name: ignored.name\r\n\r\n'})
jar('multi.jar', {'module-info.class': base, 'META-INF/versions/9/module-info.class': nine, 'META-INF/versions/17/module-info.class': seventeen, 'META-INF/MANIFEST.MF': 'Manifest-Version: 1.0\r\nMulti-Release: true\r\n\r\n'})
jar('nonmulti.jar', {'module-info.class': base, 'META-INF/versions/17/module-info.class': seventeen})
jar('unicode.jar', {'module-info.class': (out / 'unicode/module-info.class').read_bytes()})
jar('automatic.jar', {'META-INF/MANIFEST.MF': 'Manifest-Version: 1.0\r\nAutomatic-Module-Name: pcl.\r\n automatic\r\n\r\nName: extra\r\nAutomatic-Module-Name: wrong.name\r\n'})
jar('nice-library-1.2.3.jar', {'example/data.txt': 'test'})
jar('truncated.jar', {'module-info.class': base[:50]})
(out / 'ModuleOracle.java').write_text('import java.nio.file.*;\nimport java.util.jar.*;\nimport java.lang.module.*;\npublic class ModuleOracle {\n public static void main(String[] args) throws Exception {\n  Path path=Path.of(args[0]); int target=Integer.parseInt(args[1]);\n  try (JarFile jar=new JarFile(path.toFile(),true,JarFile.OPEN_READ,Runtime.Version.parse(""+target))) {\n   var entry=jar.getJarEntry("module-info.class");\n   if(entry!=null) { try(var input=jar.getInputStream(entry)) { System.out.print(ModuleDescriptor.read(input).name()); } }\n   else { System.out.print(ModuleFinder.of(path).findAll().iterator().next().descriptor().name()); }\n  }\n }\n}\n')
run(jdk / 'javac', '-d', out, out / 'ModuleOracle.java')
cases = []
for (name, target) in [('explicit.jar', 17), ('multi.jar', 9), ('multi.jar', 17), ('nonmulti.jar', 17), ('automatic.jar', 17), ('nice-library-1.2.3.jar', 17), ('unicode.jar', 17)]:
    expected = run(jdk / 'java', '-cp', out, 'ModuleOracle', out / name, target)
    cases.append({'file': name, 'targetJava': target, 'expected': expected})
(out / 'module-oracle.json').write_text(json.dumps(cases, ensure_ascii=False, indent=2))
print('Compiled Java entrypoint and', len(cases), 'JDK oracle cases')
