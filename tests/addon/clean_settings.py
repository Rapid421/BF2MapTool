"""The settings of a clean-machine walk-through: this PC's tool paths, but a NEW empty work folder.
python clean_settings.py <settings.json> <new work dir> <out settings.json>"""
import json
import os
import sys

s = json.load(open(sys.argv[1], encoding='utf-8'))
os.makedirs(sys.argv[2], exist_ok=True)
s['work_dir'] = sys.argv[2].replace('\\', '/')
json.dump(s, open(sys.argv[3], 'w', encoding='utf-8'), indent=1)
print('work_dir', s['work_dir'], 'entries in it:', len(os.listdir(sys.argv[2])))
