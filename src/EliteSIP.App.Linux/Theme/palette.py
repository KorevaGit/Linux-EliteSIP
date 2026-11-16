#!/usr/bin/env python3
"""Generate independent Linux brushes from local palette snapshots."""
from pathlib import Path
import json
base = Path(__file__).parent
tokens = json.loads((base / "tokens.json").read_text())
gradients = json.loads((base / "gradients.json").read_text())
blocks = []
for theme, colors in tokens.items():
    entries = [f'      <SolidColorBrush x:Key="{key}" Color="{value}" />' for key, value in colors.items() if key not in gradients[theme]]
    for key, stops in gradients[theme].items():
        points = ''.join(f'<GradientStop Color="{color}" Offset="{offset}"/>' for color, offset in stops)
        entries.append(f'      <LinearGradientBrush x:Key="{key}" StartPoint="0%,0%" EndPoint="0%,100%">{points}</LinearGradientBrush>')
    blocks.append(f'    <ResourceDictionary x:Key="{theme}">\n' + '\n'.join(entries) + '\n    </ResourceDictionary>')
(base / "Palette.axaml").write_text('<!-- Generated from local tokens.json and gradients.json. -->\n<ResourceDictionary xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">\n  <ResourceDictionary.ThemeDictionaries>\n' + "\n".join(blocks) + '\n  </ResourceDictionary.ThemeDictionaries>\n</ResourceDictionary>\n')
