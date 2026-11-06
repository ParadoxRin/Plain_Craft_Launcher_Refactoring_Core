"""Check the maintained developer route's local links, including project markers."""
from pathlib import Path
import re

root = Path(__file__).resolve().parents[1]
files = ['README.md', 'docs/README.md', 'docs/开发快速开始.md', 'docs/测试指南.md',
         'docs/开发指南.md', 'docs/功能实现导览.md', 'docs/扩展示例-搜索来源状态.md',
         'docs/公共接口与内部实现.md', 'examples/Pcl.MinimalClient/README.md', 'sdk/README.md']
files += [str(p.relative_to(root)) for p in (root / 'src').glob('*/README.md')]
failures = []
for name in files:
    path = root / name
    for link in re.findall(r'\]\(([^)]+)\)', path.read_text(encoding='utf-8')):
        if '://' in link or link.startswith('#'):
            continue
        if not (path.parent / link.split('#')[0]).exists():
            failures.append(f'{name}: {link}')
if failures:
    raise SystemExit('\n'.join(failures))
print(f'PASS developer documentation: {len(files)} files, local link targets exist (anchors not checked)')
