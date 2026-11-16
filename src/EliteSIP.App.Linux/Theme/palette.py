#!/usr/bin/env python3
"""Generate the independent Avalonia palette from local tokens.json."""
from pathlib import Path
import json
base = Path(__file__).parent
tokens = json.loads((base / "tokens.json").read_text())
blocks = []
for theme, colors in tokens.items():
    brushes = "\n".join(f'      <SolidColorBrush x:Key="{key}" Color="{value}" />' for key, value in colors.items())
    top, bottom = ("#FFF8F7F4", "#FFE3E2DE") if theme == "Light" else ("#FF41464E", "#FF32363D")
    # One brush is immutable and reused by controls at every window size.
    key = "ControlBackgroundBrush"
    solid = f'      <SolidColorBrush x:Key="{key}" Color="{colors[key]}" />'
    gradient = f'      <LinearGradientBrush x:Key="{key}" StartPoint="0%,0%" EndPoint="0%,100%"><GradientStop Color="{top}" Offset="0"/><GradientStop Color="{bottom}" Offset="1"/></LinearGradientBrush>'
    brushes = brushes.replace(solid, gradient)
    blocks.append(f'    <ResourceDictionary x:Key="{theme}">\n{brushes}\n    </ResourceDictionary>')
(base / "Palette.axaml").write_text('<!-- Generated from local tokens.json. -->\n<ResourceDictionary xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">\n  <ResourceDictionary.ThemeDictionaries>\n' + "\n".join(blocks) + '\n  </ResourceDictionary.ThemeDictionaries>\n</ResourceDictionary>\n')
