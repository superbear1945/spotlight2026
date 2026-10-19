#!/usr/bin/env python3
"""Render the repository's flat diagram JSON to SVG, PNG, HTML and layout report.

Input: one or more diagram JSON paths. Output: matching sibling artifacts and drawio.
Uses Pillow plus the repository's SimHei font, without a browser or network dependency.
The same measured text and geometry drive SVG and PNG; JSON remains the only source.
"""
import argparse
import html
import importlib.util
import json
import math
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]
FONT = ROOT / 'Assets/Spotlight/Fonts/SimHei.ttf'
COLORS = {'control': '#7c3aed', 'data': '#f97316', 'write': '#10b981',
          'read': '#2563eb', 'feedback': '#ef4444', 'neutral': '#64748b'}


class Canvas:
    """Shared SVG/PNG drawing surface; coordinates are logical pixels, PNG uses 2x."""

    def __init__(self, width, height):
        """Create a white canvas and SVG root using the JSON dimensions."""
        self.image = Image.new('RGB', (width * 2, height * 2), 'white')
        self.draw = ImageDraw.Draw(self.image)
        self.svg = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">',
                    '<rect width="100%" height="100%" fill="white"/>']

    def box(self, x, y, width, height, fill, stroke, radius=12):
        """Draw one rounded rectangular node/container in both output formats."""
        self.draw.rounded_rectangle((x*2, y*2, (x+width)*2, (y+height)*2), radius=radius*2, fill=fill, outline=stroke, width=3)
        self.svg.append(f'<rect x="{x}" y="{y}" width="{width}" height="{height}" rx="{radius}" fill="{fill}" stroke="{stroke}" stroke-width="1.5"/>')

    def text(self, text, x, y, size=22, color='#172033', center=True):
        """Draw a measured single line centered at its top edge; return its width."""
        font = ImageFont.truetype(str(FONT), size * 2)
        width = self.draw.textlength(text, font=font) / 2
        left = x - width / 2 if center else x
        self.draw.text((left*2, y*2), text, font=font, fill=color, anchor='lt')
        self.svg.append(f'<text x="{left}" y="{y}" dominant-baseline="text-before-edge" font-family="SimHei,Microsoft YaHei,sans-serif" font-size="{size}" fill="{color}">{html.escape(text)}</text>')
        return width

    def wrap(self, value, width, size):
        """Wrap source text by measured glyph width; no label is truncated."""
        font = ImageFont.truetype(str(FONT), size * 2)
        lines = []
        for paragraph in value.split('\n'):
            line = ''
            for char in paragraph:
                if line and self.draw.textlength(line + char, font=font) > width * 2:
                    lines.append(line)
                    line = ''
                line += char
            lines.append(line)
        return lines

    def arrow(self, points, color, label):
        """Draw an orthogonal arrow and optional label beside its longest segment."""
        self.draw.line([(x*2, y*2) for x, y in points], fill=color, width=4)
        self.svg.append('<polyline points="' + ' '.join(f'{x},{y}' for x, y in points) + f'" fill="none" stroke="{color}" stroke-width="2"/>')
        tip = points[-1]
        tail = next(p for p in reversed(points[:-1]) if p != tip)
        angle = math.atan2(tip[1]-tail[1], tip[0]-tail[0])
        head = [tip] + [(tip[0]-12*math.cos(angle+a), tip[1]-12*math.sin(angle+a)) for a in (-.45, .45)]
        self.draw.polygon([(x*2, y*2) for x, y in head], fill=color)
        self.svg.append('<polygon points="' + ' '.join(f'{x},{y}' for x, y in head) + f'" fill="{color}"/>')
        if label:
            a, b = max(zip(points, points[1:]), key=lambda pair: math.dist(*pair))
            x, y = (a[0]+b[0])/2, (a[1]+b[1])/2
            self.text(label, x+10 if a[0] == b[0] else x, y-24, 18, color, center=a[0] != b[0])


def port(node, side):
    """Map a named JSON port to its absolute node boundary coordinate."""
    x, y, w, h = (node[k] for k in ('x', 'y', 'width', 'height'))
    return {'top': (x+w/2, y), 'bottom': (x+w/2, y+h), 'left': (x, y+h/2), 'right': (x+w, y+h/2)}[side]


def route(source, target, source_port, target_port):
    """Produce deterministic elbow paths; same-side loops use an outside lane."""
    a, b = port(source, source_port), port(target, target_port)
    if source_port == target_port:
        if source_port in ('left', 'right'):
            x = min(a[0], b[0])-35 if source_port == 'left' else max(a[0], b[0])+35
            return [a, (x, a[1]), (x, b[1]), b]
        y = min(a[1], b[1])-35 if source_port == 'top' else max(a[1], b[1])+35
        return [a, (a[0], y), (b[0], y), b]
    if source_port in ('top', 'bottom'):
        y = (a[1]+b[1])/2
        return [a, (a[0], y), (b[0], y), b]
    x = (a[0]+b[0])/2
    return [a, (x, a[1]), (x, b[1]), b]


def render(path):
    """Validate geometry, then generate all sibling exports from one JSON source."""
    spec = json.loads(path.read_text(encoding='utf-8'))
    w, h = spec['width'], spec['height']
    nodes = {n['id']: n for n in spec['nodes']}
    problems = []
    for n in nodes.values():
        if n['x'] < 0 or n['y'] < 0 or n['x']+n['width'] > w or n['y']+n['height'] > h:
            problems.append('node out of bounds: ' + n['id'])
    values = list(nodes.values())
    for i, a in enumerate(values):
        for b in values[i+1:]:
            if max(a['x'], b['x']) < min(a['x']+a['width'], b['x']+b['width']) and max(a['y'], b['y']) < min(a['y']+a['height'], b['y']+b['height']):
                problems.append('overlapping nodes: ' + a['id'] + '/' + b['id'])
    canvas = Canvas(w, h)
    canvas.text(spec['title'], w/2, 22, 34)
    canvas.text(spec.get('subtitle', ''), w/2, 69, 21, '#64748b')
    for box in spec.get('containers', []):
        canvas.box(box['x'], box['y'], box['width'], box['height'], '#f8fafc', '#cbd5e1', 18)
        canvas.text(box['label'], box['x']+22, box['y']+18, 24, center=False)
        canvas.text(box.get('subtitle', ''), box['x']+22, box['y']+54, 19, '#64748b', center=False)
    for edge in spec['arrows']:
        canvas.arrow(route(nodes[edge['source']], nodes[edge['target']], edge.get('source_port', 'bottom'), edge.get('target_port', 'top')),
                     COLORS.get(edge.get('flow'), '#64748b'), edge.get('label', ''))
    for node in nodes.values():
        x, y, nw, nh = (node[k] for k in ('x', 'y', 'width', 'height'))
        canvas.box(x, y, nw, nh, node.get('fill', '#fff'), node.get('stroke', '#2563eb'))
        lines = [(line, 24, '#172033') for line in canvas.wrap(node['label'], nw-30, 24)]
        lines += [(line, 19, '#475569') for line in canvas.wrap(node.get('sublabel', ''), nw-30, 19)]
        total = sum(size+8 for _, size, _ in lines)
        if total > nh-16:
            problems.append('text overflow: ' + node['id'])
        ty = y+(nh-total)/2
        for line, size, color in lines:
            canvas.text(line, x+nw/2, ty, size, color)
            ty += size+8
    canvas.text(spec.get('footer', ''), w/2, spec.get('footer_y', h-38), 19, '#64748b')
    if problems:
        raise ValueError('\n'.join(problems))
    svg = '\n'.join(canvas.svg + ['</svg>'])
    path.with_suffix('.svg').write_text(svg, encoding='utf-8')
    canvas.image.save(path.with_suffix('.png'), optimize=True)
    title = html.escape(spec['title'])
    viewer = f'''<!doctype html><html lang="zh"><meta charset="utf-8"><title>{title}</title>
<style>body{{margin:0;background:#e2e8f0;font-family:sans-serif}}header{{position:sticky;top:0;padding:12px;background:white}}main{{padding:20px;overflow:auto}}svg{{width:100%;height:auto;background:white}}</style>
<header>{title} · <label>缩放 <input id="zoom" type="range" min="40" max="200" value="100"></label></header>
<main>{svg}</main><script>document.getElementById('zoom').oninput=e=>document.querySelector('svg').style.width=e.target.value+'%';</script></html>'''
    path.with_suffix('.html').write_text(viewer, encoding='utf-8')
    report = {'renderer': 'render-diagrams.py', 'source': path.name, 'node_count': len(nodes),
              'edge_count': len(spec['arrows']), 'checks': {'node_bounds': True, 'node_overlap': False, 'text_overflow': False},
              'scope': 'Node geometry and measured text. Edge routing is visually reviewed; crossings are not automatically scored.'}
    path.with_suffix('.layout.json').write_text(json.dumps(report, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    module_spec = importlib.util.spec_from_file_location('drawio', Path(__file__).with_name('spec-to-drawio.py'))
    module = importlib.util.module_from_spec(module_spec)
    module_spec.loader.exec_module(module)
    module.convert(str(path))
    print(path.name + ': SVG / PNG / HTML / drawio / layout generated')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('sources', nargs='+', type=Path)
    for source in parser.parse_args().sources:
        render(source)
