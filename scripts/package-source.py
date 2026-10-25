"""Create a source-only, versioned archive; game/runtime/account/test data is never traversed."""
from pathlib import Path
import zipfile,hashlib,json,os
root = Path(__file__).resolve().parent.parent
skip = {'.git', '.tooling', '.cache', 'artifacts', 'runtime', 'bin', 'obj', '__pycache__', '.DS_Store'}
target = root / 'artifacts/PCL-Mac-0.23-source.zip'
files = [root / '.editorconfig'] + [f for f in root.iterdir() if f.is_file() and f.suffix in ('.sln','.json','.props','.config','.md','.txt')]
for folder in ('src','scripts','tests','docs','vendor'):
    files += [f for f in (root/folder).rglob('*') if f.is_file() and not f.is_symlink() and not any(x in skip for x in f.relative_to(root).parts)]
with zipfile.ZipFile(target, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
    for file in sorted(files):
        if file.suffix in ('.log','.dll','.exe','.pdb','.dylib') or (file.name.startswith('.') and file.name != '.editorconfig'): continue
        # 新包只携带当前说明与必要许可，历史发布说明留在工作目录和旧包。
        if file.parent == root/'docs' and file.name not in ('0.23-运行记录.md','0.23-功能进度.json','windows-layout-baseline.json','PCL2完整功能矩阵.json','THIRD-PARTY-NOTICES.md') and 'LICENSE' not in file.name and 'NOTICE' not in file.name: continue
        archive.write(file, Path('pcl-mac-port') / file.relative_to(root))
if os.environ.get('PCL_SKIP_CHECKS') != '1':
    with zipfile.ZipFile(target) as archive:
        assert archive.testzip() is None
print(str(target))
