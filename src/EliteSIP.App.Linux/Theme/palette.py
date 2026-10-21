#!/usr/bin/env python3
"""Собирает Palette.axaml из палитр Windows-версии.

Числа живут в shared/src/EliteSIP.App/Theme/Palette.{Light,Dark}.xaml, и
правятся там. Этот сценарий переносит их сюда, в одну палитру с вариантами
тем Avalonia, и добавляет кисти SimpleTheme тех же цветов — так поля, списки
и меню простой темы выходят цветами продукта.

Запуск из корня репозитория:  python3 src/EliteSIP.App.Linux/Theme/palette.py
"""

import pathlib
import re

ROOT = pathlib.Path(__file__).resolve().parents[3]
SOURCE = ROOT / "shared/src/EliteSIP.App/Theme"
TARGET = pathlib.Path(__file__).with_name("Palette.axaml")

# Кисть SimpleTheme ← кисть продукта.
SIMPLE = {
    "ThemeBackgroundBrush": "WindowBackgroundBrush",
    "ThemeForegroundBrush": "TextPrimaryBrush",
    "ThemeForegroundLowBrush": "TextTertiaryBrush",
    "ThemeControlLowBrush": "ControlBackgroundBrush",
    "ThemeControlMidBrush": "ControlHoverBrush",
    "ThemeControlHighBrush": "ControlPressedBrush",
    "ThemeControlHighlightLowBrush": "ControlHoverBrush",
    "ThemeControlHighlightMidBrush": "ControlPressedBrush",
    "ThemeBorderLowBrush": "ControlBorderBrush",
    "ThemeBorderMidBrush": "ControlBorderHoverBrush",
    "ThemeBorderHighBrush": "ControlBorderHoverBrush",
    "ThemeAccentBrush": "AccentBrush",
    "HighlightBrush": "SelectionBrush",
}


def read(name):
    text = (SOURCE / name).read_text(encoding="utf-8-sig")
    return re.findall(r'<SolidColorBrush x:Key="([^"]+)" Color="([^"]+)"', text)


def block(pairs):
    colors = dict(pairs)
    lines = [f'      <SolidColorBrush x:Key="{key}" Color="{value}" />' for key, value in pairs]
    lines.append("      <!-- SimpleTheme -->")
    lines += [f'      <SolidColorBrush x:Key="{key}" Color="{colors[source]}" />' for key, source in SIMPLE.items()]
    return "\n".join(lines)


light = read("Palette.Light.xaml")
dark = dict(read("Palette.Dark.xaml"))
assert set(dark) == {key for key, _ in light}, "ключи палитр Windows разошлись"

TARGET.write_text(f"""<!--
    Палитра — числа обеих тем Windows-версии (shared/src/EliteSIP.App/Theme/
    Palette.Light.xaml и Palette.Dark.xaml), сведённые в одну с вариантами тем
    Avalonia. Там словарь подменялся целиком при смене темы, здесь тему меняет
    RequestedThemeVariant приложения, и кисти, взятые через DynamicResource,
    перекрашиваются сами.

    Не править руками: файл собирает palette.py рядом. Почему цвета именно
    такие — в комментариях WPF-файлов.
-->
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <ResourceDictionary.ThemeDictionaries>
    <ResourceDictionary x:Key="Light">
{block(light)}
    </ResourceDictionary>
    <ResourceDictionary x:Key="Dark">
{block([(key, dark[key]) for key, _ in light])}
    </ResourceDictionary>
  </ResourceDictionary.ThemeDictionaries>
</ResourceDictionary>
""", encoding="utf-8")
