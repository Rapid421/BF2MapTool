"""The docs only name buttons and settings that exist: every **bold** term in docs/*.md that looks like a control must
be an operator label (bl_label) or a property name of the add-on.  python test_docs.py"""
import glob
import os
import re
import sys

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
src = ''.join(open(f, encoding='utf-8').read() for f in glob.glob(os.path.join(ROOT, 'addon', 'bf2_map_tool', '*.py')))
labels = set(re.findall(r"bl_label = '([^']+)'", src)) | set(re.findall(r"name='([^']+)'", src))
labels |= set(re.findall(r"\('[a-z_0-9]+', '([^']+)'", src))           # enum item names (tabs, classes, presets ...)
labels |= set(re.findall(r"text='([^'%]+)'", src))                       # buttons drawn with their own text
# words in bold that are not controls (headings in prose, Blender's own menus, file names)
NOT_CONTROLS = {'do not', 'Install from Disk', 'Port map again', 'Get Extensions', 'Blender World (match the scene)', 'N',
                'Principled BSDF', 'Instances', 'Many lamps', 'BF2 Map Tool'}
fails, seen = [], 0
for f in sorted(glob.glob(os.path.join(ROOT, 'docs', '*.md'))):
    if os.path.basename(f) == 'CHANGELOG.md':
        continue
    for term in re.findall(r'\*\*([^*]+)\*\*', open(f, encoding='utf-8').read()):
        t = term.strip()
        if t in NOT_CONTROLS or not re.match(r'^[A-Z][A-Za-z0-9 ./()\-]+$', t) or len(t) > 40:
            continue
        seen += 1
        if t not in labels:
            fails.append('%s: "%s"' % (os.path.basename(f), t))
print('%d control names checked; %d unknown%s' % (seen, len(fails), (': ' + '; '.join(fails)) if fails else ''))
sys.exit(1 if fails else 0)
