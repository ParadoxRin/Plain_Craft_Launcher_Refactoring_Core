"""Extract original navigation, vector icons and setting card order for the Mac comparison UI."""
from pathlib import Path
import json
import xml.etree.ElementTree as ET
root = Path(__file__).resolve().parents[1]
source = root.parent / 'pcl-download-fix/PCL-2.99.0.0/Plain Craft Launcher 2'
pages = {}
for name in ['PageSetupLaunch', 'PageSetupLink', 'PageSetupUI', 'PageSetupSystem']:
    path = source / ('Pages/PageSetup/' + name + '.xaml')
    tree = ET.parse(path)
    cards = []
    parents = {child: parent for parent in tree.iter() for child in parent}
    for card in tree.iter():
        if card.tag.split('}')[-1] != 'MyCard' or not card.get('Title'):
            continue
        labels = []
        for node in card.iter():
            label = node.get('Text') or node.get('Content')
            if label and not label.startswith('{') and len(label) < 95 and label not in labels:
                labels.append(label)
        fields = []
        control_types = {'MyComboBox', 'MyTextBox', 'MyCheckBox', 'MyRadioBox', 'MySlider', 'MyButton'}
        for node in card.iter():
            kind = node.tag.split('}')[-1]
            if kind not in control_types or node.get('Visibility') == 'Collapsed':
                continue
            label = node.get('Text') if kind in {'MyCheckBox', 'MyRadioBox', 'MyButton'} else None
            parent = parents.get(node)
            if not label:
                # Original setting forms pair a caption and an input on the same Grid.Row.
                ancestor = parent
                while ancestor is not None and ancestor is not card:
                    if ancestor.tag.split('}')[-1] == 'Grid':
                        candidates = [x.get('Text') for x in ancestor
                                      if x.tag.split('}')[-1] == 'TextBlock'
                                      and x.get('Grid.Row', '0') == node.get('Grid.Row', '0')
                                      and x.get('Text') and not x.get('Text').startswith('{')
                                      and x.get('Visibility') != 'Collapsed'
                                      and x.get('Text').strip() not in {'×', '%', 'MB', 'GB'}]
                        if candidates:
                            label = candidates[0]
                            break
                    ancestor = parents.get(ancestor)
            if not label:
                label = node.get('HintText') or node.get('ToolTip', '').split('\n')[0]
            setting_key = next((value for key, value in node.attrib.items() if key.endswith('SettingService.Key')), '')
            if setting_key == 'LaunchArgumentWindowType':
                label = '游戏窗口'
            if not label or label.startswith('{'):
                continue
            fields.append({'label': label[:90], 'kind': kind,
                           'options': [x.get('Content') for x in node.iter() if x.tag.split('}')[-1] == 'MyComboBoxItem' and x.get('Content')]})
        cards.append({'title': card.get('Title'), 'collapsed': card.get('IsSwapped') == 'True', 'labels': labels, 'fields': fields})
    pages[name] = cards
menus = {}
for area in ['Download', 'Setup', 'Other']:
    path = source / f'Pages/Page{area}/Page{area}Left.xaml'
    menus[area] = [dict(title=node.get('Title'), icon=node.get('Logo', ''), tag=node.get('Tag'))
                   for node in ET.parse(path).iter()
                   if node.tag.split('}')[-1] == 'MyListItem' and node.get('Title')]
output = root / 'docs/windows-layout-baseline.json'
output.write_text(json.dumps({'baseline': 'PCL 2.99.0.0', 'commit': '6b9a50df911c9d68b69b24c979efba477fb14b67', 'menus': menus, 'pages': pages}, ensure_ascii=False, indent=2))
print(output)
