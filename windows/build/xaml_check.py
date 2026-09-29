#!/usr/bin/env python3
"""Static sanity checks for the WPF project that the compiler cannot do:
 - every StaticResource/DynamicResource key is defined somewhere
 - Light and Dark themes define identical key sets
 - every {Binding Path} root exists as a property/command on some view model
 - Run elements bound without Mode=OneWay (common runtime error source)
"""
import re, sys, pathlib

root = pathlib.Path(__file__).resolve().parents[1] / "src" / "Evict.App"
xaml_files = list(root.rglob("*.xaml"))
cs_files = list(root.rglob("*.cs")) + list((root.parent / "Evict.Core").rglob("*.cs"))

defined = set()
for f in xaml_files:
    text = f.read_text(encoding="utf-8")
    defined |= set(re.findall(r'x:Key="([^"]+)"', text))
# implicit type-keyed styles referenced as {StaticResource {x:Type CheckBox}}
defined |= {"{x:Type CheckBox}", "{x:Type ProgressBar}", "{x:Type ToolTip}", "{x:Type ComboBox}"}

problems = 0
# Style setters must target dependency properties; these common Window/control CLR properties are not.
NON_DP = {"WindowStartupLocation", "Owner", "DialogResult", "IsDefault_", "Resources", "Name"}
for f in xaml_files:
    text = f.read_text(encoding="utf-8")
    for prop in re.findall(r'<Setter\s+(?:TargetName="[^"]*"\s+)?Property="([^"]+)"', text):
        if prop in NON_DP:
            print(f"[setter] {f.name}: Setter Property='{prop}' is not a DependencyProperty (crashes when the style is first used)")
            problems += 1
for f in xaml_files:
    text = f.read_text(encoding="utf-8")
    for kind, key in re.findall(r'\{(StaticResource|DynamicResource)\s+([^}]+)\}', text):
        key = key.strip()
        if key.startswith("{x:Type"):
            continue
        if key not in defined:
            print(f"[resource] {f.name}: {kind} '{key}' is not defined anywhere")
            problems += 1

# Theme parity
light = set(re.findall(r'x:Key="([^"]+)"', (root / "Themes" / "Light.xaml").read_text(encoding="utf-8")))
dark = set(re.findall(r'x:Key="([^"]+)"', (root / "Themes" / "Dark.xaml").read_text(encoding="utf-8")))
for k in sorted(light ^ dark):
    print(f"[theme] key only in one theme: {k}")
    problems += 1

# Binding roots vs view-model members
members = set()
for f in cs_files:
    text = f.read_text(encoding="utf-8")
    members |= set(re.findall(r'public\s+[\w<>?,\[\] ()]+\s+(\w+)\s*(?:\{|=>|;)', text))            # properties
    for fld in re.findall(r'\[ObservableProperty\][^;]*?\s_(\w+)\s*(?:=|;)', text, flags=re.S):
        members.add(fld[0].upper() + fld[1:])
    for m in re.findall(r'\[RelayCommand[^\]]*\]\s*(?:private|public|internal)?\s*(?:async\s+)?[\w<>?]+\s+(\w+)\s*\(', text):
        name = m[:-5] if m.endswith("Async") else m
        members.add(name + "Command")
    # records / tuple names are rare; also add record positional params
    for rec in re.findall(r'record\s+\w+\s*\(([^)]*)\)', text):
        for part in rec.split(","):
            toks = part.strip().split()
            if len(toks) >= 2: members.add(toks[-1])
# CollectionViewGroup members used in group headers
members |= {"Name", "ItemCount", "Items", "Count", "PlacementTarget", "DataContext", "WindowState", "Foreground", "IsExpanded", "SelectedItem", "IsDropDownOpen", "ActualWidth", "Key", "Value"}

for f in xaml_files:
    text = f.read_text(encoding="utf-8")
    for b in re.findall(r'\{Binding\s+([^}]*)\}', text):
        b = b.strip()
        if not b or b.startswith(("RelativeSource", "Path=", "Converter", "Mode", "ElementName")):
            m = re.search(r'Path=([\w.]+)', b)
            path = m.group(1) if m else None
        else:
            path = b.split(",")[0].strip()
        if not path:
            continue
        roots = path.split(".")
        for seg in roots:
            seg = re.sub(r'\[.*\]', '', seg)
            if seg and seg not in members:
                print(f"[binding] {f.name}: '{path}' - segment '{seg}' not found on any view model")
                problems += 1
                break

# Run bindings must be OneWay
for f in xaml_files:
    text = f.read_text(encoding="utf-8")
    for run in re.findall(r'<Run\s+Text="\{Binding[^}]*\}"[^>]*/>', text):
        if "Mode=OneWay" not in run:
            print(f"[run] {f.name}: Run binding without Mode=OneWay: {run[:80]}")
            problems += 1

# duplicate attribute + property-element (e.g. Style attr and <X.Style>) – real XML parse
import xml.etree.ElementTree as ET
for f in xaml_files:
    tree = ET.parse(f)
    for el in tree.iter():
        tag = el.tag.split('}')[-1]
        for child in el:
            ctag = child.tag.split('}')[-1]
            if ctag.startswith(tag + ".") and ctag[len(tag)+1:] in el.attrib:
                print(f"[dup] {f.name}: <{tag}> sets '{ctag[len(tag)+1:]}' twice")
                problems += 1

print(f"\n{len(xaml_files)} XAML files, {len(defined)} resource keys, {len(members)} VM members checked. Problems: {problems}")
sys.exit(1 if problems else 0)
