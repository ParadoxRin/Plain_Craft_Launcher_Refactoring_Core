"""Package this project's own executable; ad-hoc signing is local, not notarization."""
import pathlib, plistlib, subprocess, sys, struct, zlib, shutil, os

root, app = map(pathlib.Path, sys.argv[1:])
info = dict(CFBundleName='Plain Craft Launcher Refactoring', CFBundleDisplayName='Plain Craft Launcher Refactoring',
    CFBundleIdentifier='local.pcl.macport.preview', CFBundleExecutable='PCL-MacPort',
    CFBundlePackageType='APPL', CFBundleShortVersionString=os.environ.get('PCL_PACKAGE_VERSION', '0.36.1'), CFBundleVersion=os.environ.get('PCL_PACKAGE_BUILD', '361'),
    CFBundleIconFile='AppIcon.icns', NSHighResolutionCapable=True,
    LSMinimumSystemVersion='14.0', NSPrincipalClass='NSApplication',
    NSHumanReadableCopyright='PCL by 龙腾猫跃 (LTCatt). Independent third-party refactoring. See Resources/Legal.')
(app/'Contents/Info.plist').write_bytes(plistlib.dumps(info))

def png(size):
    pixels = bytearray()
    for y in range(size):
        pixels.append(0)
        for x in range(size):
            u, v = x/size, y/size
            rounded = max(abs(u-.5)-.29,0)**2 + max(abs(v-.5)-.29,0)**2 <= .16**2
            if not rounded:
                pixels.extend((0,0,0,0));continue
            stem = .29 <= u <= .39 and .22 <= v <= .79
            bowl = .38 <= u <= .71 and .22 <= v <= .56
            cut = .40 <= u <= .59 and .32 <= v <= .46
            if stem or (bowl and not cut):pixels.extend((244,248,255,255))
            else:pixels.extend((int(70+30*v),int(99+35*v),235,255))
    def chunk(kind,data):return struct.pack('>I',len(data))+kind+data+struct.pack('>I',zlib.crc32(kind+data)&0xffffffff)
    return b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>2I5B',size,size,8,6,0,0,0))+chunk(b'IDAT',zlib.compress(bytes(pixels),9))+chunk(b'IEND',b'')
parts=[]
for kind,size in [(b'ic08',256),(b'ic09',512),(b'ic10',1024)]:
    content=png(size);parts.append(kind+struct.pack('>I',len(content)+8)+content)
body=b''.join(parts)
(app/'Contents/Resources/AppIcon.icns').write_bytes(b'icns'+struct.pack('>I',len(body)+8)+body)

licenses=app/'Contents/Resources/Legal/Dependencies'
for package in (root/'.cache/nuget').glob('*/*'):
    if not package.is_dir():continue
    for file in package.iterdir():
        if file.is_file() and ('license' in file.name.lower() or 'notice' in file.name.lower()) and file.suffix.lower() in ('.txt','.md',''):
            dest=licenses/package.parent.name/package.name;dest.mkdir(parents=True,exist_ok=True);shutil.copy2(file,dest/file.name)
runtime_legal = root/'.tooling/dotnet/shared/Microsoft.NETCore.App'
for version in runtime_legal.glob('*'):
    for name in ('LICENSE.txt','THIRD-PARTY-NOTICES.TXT'):
        f=version/name
        if f.exists():shutil.copy2(f,app/'Contents/Resources/Legal'/('dotnet-'+name))

main = app/'Contents/MacOS/PCL-MacPort'
for file in sorted((app/'Contents/MacOS').rglob('*')):
    if file.is_file() and not file.is_symlink():
        if file == main:continue
        # macOS treats everything under Contents/MacOS as nested code, including
        # .NET config/data files; sign leaves before the executable and bundle.
        signed = subprocess.run(['/usr/bin/codesign','--force','--sign','-',str(file)],capture_output=True,text=True)
        if signed.returncode:raise RuntimeError(str(file)+': '+signed.stderr)
subprocess.run(['/usr/bin/codesign','--force','--sign','-',str(main)],check=True)
subprocess.run(['/usr/bin/codesign','--force','--sign','-',str(app)],check=True)
if os.environ.get('PCL_SKIP_CHECKS') != '1':
    subprocess.run(['/usr/bin/codesign','--verify','--deep','--strict',str(app)],check=True)
print('Ad-hoc signed; not a Developer ID notarized release.')
