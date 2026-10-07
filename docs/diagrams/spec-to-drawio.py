#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 fireworks-tech-graph 的图纸 JSON 转成 draw.io 原生文件（.drawio）。

为什么需要这个转换：
    SVG / PNG 只能看不能改。draw.io 导入 SVG 时会把箭头当成普通路径图形，
    移动方框后箭头不会跟着走，连接关系也就丢了。
    本脚本直接生成 draw.io 原生的 mxGraph 模型：方框是顶点（vertex），
    箭头是带 source / target 的边（edge），因此在 draw.io 里拖动方框时
    箭头会自动跟随，可以长期维护这些架构图。

输入：`docs/diagrams/<name>.json`（fireworks-tech-graph 图纸定义）
输出：`docs/diagrams/<name>.drawio`（mxfile，可直接用 draw.io 打开或导入）

用法：
    python spec-to-drawio.py docs/diagrams          # 转换目录下全部 JSON
    python spec-to-drawio.py docs/diagrams --check  # 只校验已生成的 .drawio
"""
import argparse
import glob
import html
import json
import os
import sys
import xml.etree.ElementTree as ET

# 语义流向 → 颜色。取值与 SVG 里 data-flow 的描边颜色一致，保证三套产物同色。
FLOW_COLORS = {
    "control": "#7c3aed",
    "data": "#f97316",
    "write": "#10b981",
    "read": "#2563eb",
    "feedback": "#ef4444",
    "neutral": "#6b7280",
}
FLOW_COLOR_FALLBACK = "#7c3aed"

# 端口 → 出口/入口归一化坐标（draw.io 的 exitX/exitY/entryX/entryY 取值 0..1）。
PORTS = {
    "left": (0.0, 0.5),
    "right": (1.0, 0.5),
    "top": (0.5, 0.0),
    "bottom": (0.5, 1.0),
}

# 图纸节点类型 → draw.io 形状前缀。无法识别的类型退化为圆角矩形。
SHAPE_PREFIX = {
    "rect": "rounded=1;arcSize=8;",
    "round_rect": "rounded=1;arcSize=14;",
    # draw.io 原生 double=1 会画出真正的双线边框，比嵌套子框更简单也更稳。
    "double_rect": "rounded=1;arcSize=8;double=1;",
    "cylinder": "shape=cylinder3;boundedLbl=1;",
    "hexagon": "shape=hexagon;perimeter=hexagonPerimeter2;fixedSize=1;",
    "ellipse": "ellipse;",
    "circle": "ellipse;",
    "rhombus": "rhombus;",
    "diamond": "rhombus;",
    "parallelogram": "shape=parallelogram;perimeter=parallelogramPerimeter;",
    "note": "shape=note;size=16;",
    "document": "shape=document;boundedLbl=1;",
    "cloud": "ellipse;shape=cloud;",
}
SHAPE_FALLBACK = "rounded=1;arcSize=8;"

TITLE_COLOR = "#111827"
MUTED_COLOR = "#64748b"
BODY_FONT = 14
SUB_FONT = 11


def esc(text):
    """把文本转义成可放进 mxCell 属性的内容。

    输入：原文；输出：转义后的文本。
    为什么要转义引号：value 属性整体用双引号包裹，而标签里会嵌入
    `<font style="...">` 这类内联 HTML；不转义引号会直接破坏 XML 结构。
    转义后由 XML 解析器还原成原始 HTML，draw.io 再按 HTML 渲染，行为不变。
    """
    return html.escape(text or "", quote=True)


def sub_line(text):
    """把副标题包成灰色小字。输入：副标题；输出：HTML 片段（空标题返回空串）。"""
    return f'<br><font style="font-size:{SUB_FONT}px;color:{MUTED_COLOR}">{esc(text)}</font>' if text else ""


def node_value(node):
    """节点显示文本。输入：节点定义；输出：加粗标题 + 灰色副标题的 HTML。"""
    label = f'<b>{esc(node.get("label", ""))}</b>'
    return label + sub_line(node.get("sublabel"))


def container_value(container):
    """容器显示文本。输入：容器定义；输出：加粗标题 + 灰色副标题的 HTML。"""
    return f'<b>{esc(container.get("label", ""))}</b>' + sub_line(container.get("subtitle"))


def container_geometry(container):
    """容器几何。输入：容器定义；输出：mxGeometry XML 片段。"""
    return (f'<mxGeometry x="{container["x"]}" y="{container["y"]}" '
            f'width="{container["width"]}" height="{container["height"]}" as="geometry"/>')


def node_geometry(node):
    """节点几何。输入：节点定义；输出：mxGeometry XML 片段。"""
    return (f'<mxGeometry x="{node["x"]}" y="{node["y"]}" '
            f'width="{node["width"]}" height="{node["height"]}" as="geometry"/>')


def build_cells(spec):
    """把图纸定义转换成 draw.io 单元格列表。

    输入：图纸定义 dict；输出：mxCell XML 片段列表（含容器、节点、双线内框、箭头、图例与页脚）。
    """
    cells = []
    node_index = {}

    # 1) 分区容器：虚线圆角框，标题贴左上角，作为背景元素先输出。
    for container in spec.get("containers", []):
        style = ("rounded=1;arcSize=10;whiteSpace=wrap;html=1;fillColor=none;"
                 f"strokeColor=#dbe5f1;strokeWidth=1.4;dashed=1;dashPattern=1 3;"
                 f"verticalAlign=top;align=left;spacingLeft=14;spacingTop=10;"
                 f"fontColor={MUTED_COLOR};fontSize=13;")
        cells.append(f'<mxCell id="{esc(container["id"])}" value="{esc(container_value(container))}" '
                     f'style="{style}" vertex="1" parent="1">{container_geometry(container)}</mxCell>')
        node_index[container["id"]] = container

    # 2) 普通节点。
    for node in spec.get("nodes", []):
        style = (SHAPE_PREFIX.get(node.get("kind"), SHAPE_FALLBACK) +
                 f"whiteSpace=wrap;html=1;shadow=0;fillColor={node.get('fill', '#ffffff')};"
                 f"strokeColor={node.get('stroke', '#94a3b8')};strokeWidth=1.8;"
                 f"fontColor={TITLE_COLOR};fontSize={BODY_FONT};align=center;verticalAlign=middle;")
        if node.get("kind") == "hexagon":
            # 六边形需要留出斜边宽度，否则文字会压到折角。
            style += "spacingLeft=18;spacingRight=18;"
        cells.append(f'<mxCell id="{esc(node["id"])}" value="{esc(node_value(node))}" '
                     f'style="{style}" vertex="1" parent="1">{node_geometry(node)}</mxCell>')
        node_index[node["id"]] = node

    # 3) 箭头：真正的边，带 source/target，拖动节点时会自动重连。
    for arrow in spec.get("arrows", []):
        color = FLOW_COLORS.get(arrow.get("flow"), FLOW_COLOR_FALLBACK)
        source = node_index.get(arrow.get("source"))
        target = node_index.get(arrow.get("target"))
        style = (f"edgeStyle=orthogonalEdgeStyle;rounded=0;html=1;shadow=0;"
                 f"endArrow=block;endFill=1;jumpStyle=arc;strokeColor={color};strokeWidth=2.4;"
                 f"fontColor={MUTED_COLOR};fontSize=12;labelBackgroundColor=#ffffff;")
        if source is not None:
            sx, sy = PORTS.get(arrow.get("source_port"), (0.5, 0.5))
            style += f"exitX={sx};exitY={sy};exitDx=0;exitDy=0;"
        if target is not None:
            tx, ty = PORTS.get(arrow.get("target_port"), (0.5, 0.5))
            style += f"entryX={tx};entryY={ty};entryDx=0;entryDy=0;"
        source_id = esc(arrow["source"]) if source is not None else ""
        target_id = esc(arrow["target"]) if target is not None else ""
        cells.append(f'<mxCell id="{esc(arrow["id"])}" value="{esc(arrow.get("label", ""))}" '
                     f'style="{style}" edge="1" parent="1" source="{source_id}" target="{target_id}">'
                     f'<mxGeometry relative="1" as="geometry"/></mxCell>')

    # 4) 图例：一条带箭头的短线 + 说明文字，位置取自图纸定义。
    legend_y = spec.get("legend_y")
    legend = spec.get("legend") or []
    if legend and legend_y is not None:
        x = 42
        for item in legend:
            color = FLOW_COLORS.get(item.get("flow"), FLOW_COLOR_FALLBACK)
            line_id = f"legend-line-{item.get('flow')}"
            text_id = f"legend-text-{item.get('flow')}"
            style = (f"endArrow=block;endFill=1;html=1;strokeColor={color};strokeWidth=2.4;"
                     f"fontColor={MUTED_COLOR};fontSize=12;")
            cells.append(f'<mxCell id="{esc(line_id)}" value="" style="{style}" edge="1" parent="1">'
                         f'<mxGeometry relative="1" as="geometry">'
                         f'<mxPoint x="{x}" y="{legend_y}" as="sourcePoint"/>'
                         f'<mxPoint x="{x + 30}" y="{legend_y}" as="targetPoint"/></mxGeometry></mxCell>')
            width = 8 * len(item.get("label", "")) + 24
            label_style = (f"text;html=1;strokeColor=none;fillColor=none;align=left;verticalAlign=middle;"
                           f"fontColor={MUTED_COLOR};fontSize=12;")
            cells.append(f'<mxCell id="{esc(text_id)}" value="{esc(item.get("label", ""))}" '
                         f'style="{label_style}" vertex="1" parent="1">'
                         f'<mxGeometry x="{x + 40}" y="{legend_y - 10}" width="{width}" height="20" as="geometry"/>'
                         f'</mxCell>')
            x += 40 + width + 20

    # 5) 标题、副标题与页脚：便于单独选中调整。
    width = spec.get("width", 1600)
    if spec.get("title"):
        cells.append(f'<mxCell id="page-title" value="{esc(spec["title"])}" '
                     f'style="text;html=1;strokeColor=none;fillColor=none;align=center;verticalAlign=middle;'
                     f'fontColor={TITLE_COLOR};fontSize=24;fontStyle=1;" vertex="1" parent="1">'
                     f'<mxGeometry x="0" y="20" width="{width}" height="40" as="geometry"/></mxCell>')
    if spec.get("subtitle"):
        cells.append(f'<mxCell id="page-subtitle" value="{esc(spec["subtitle"])}" '
                     f'style="text;html=1;strokeColor=none;fillColor=none;align=center;verticalAlign=middle;'
                     f'fontColor={MUTED_COLOR};fontSize=13;" vertex="1" parent="1">'
                     f'<mxGeometry x="0" y="58" width="{width}" height="28" as="geometry"/></mxCell>')
    if spec.get("footer"):
        footer_y = spec.get("footer_y", spec.get("height", 900) - 40)
        cells.append(f'<mxCell id="page-footer" value="{esc(spec["footer"])}" '
                     f'style="text;html=1;strokeColor=none;fillColor=none;align=left;verticalAlign=middle;'
                     f'fontColor={MUTED_COLOR};fontSize=12;" vertex="1" parent="1">'
                     f'<mxGeometry x="32" y="{footer_y - 14}" width="{width - 64}" height="24" as="geometry"/></mxCell>')

    return cells


def build_document(spec, name):
    """把单元格列表包成完整的 mxfile 文档。

    输入：图纸定义与图名；输出：可直接写入 .drawio 的 XML 文本。
    """
    width = spec.get("width", 1600)
    height = spec.get("height", 900)
    cells = build_cells(spec)
    body = "\n        ".join(cells)
    return (
        '<mxfile host="app.diagrams.net" type="device" version="24.7.17">\n'
        f'  <diagram id="{esc(name)}" name="{esc(spec.get("title", name))}">\n'
        f'    <mxGraphModel dx="1400" dy="900" grid="1" gridSize="10" guides="1" tooltips="1" '
        f'connect="1" arrows="1" fold="1" page="1" pageScale="1" pageWidth="{width}" '
        f'pageHeight="{height}" math="0" shadow="0" background="#ffffff">\n'
        '      <root>\n'
        '        <mxCell id="0"/>\n'
        '        <mxCell id="1" parent="0"/>\n'
        f'        {body}\n'
        '      </root>\n'
        '    </mxGraphModel>\n'
        '  </diagram>\n'
        '</mxfile>\n'
    )


def check(path):
    """校验一个 .drawio 文件的基本可用性。

    输入：文件路径；输出：问题列表（为空表示结构完整）。
    检查项：XML 合法、根元素为 mxfile、每条边的 source/target 都能找到对应顶点。
    """
    problems = []
    try:
        tree = ET.parse(path)
    except ET.ParseError as error:
        return [f"{path}: XML 解析失败：{error}"]
    root = tree.getroot()
    if root.tag != "mxfile":
        problems.append(f"{path}: 根元素应为 mxfile，实际为 {root.tag}")
        return problems
    cells = {c.get("id"): c for c in root.iter("mxCell")}
    vertices = {cid for cid, c in cells.items() if c.get("vertex") == "1"}
    edges = [c for c in root.iter("mxCell") if c.get("edge") == "1"]
    for edge in edges:
        for attr in ("source", "target"):
            ref = edge.get(attr)
            if ref and ref not in vertices:
                problems.append(f"{path}: 边 {edge.get('id')} 的 {attr}={ref} 找不到对应顶点")
    if not vertices:
        problems.append(f"{path}: 没有任何顶点")
    return problems


def convert(json_path, out_dir=None):
    """转换单个图纸 JSON。

    输入：JSON 路径与可选输出目录；输出：生成的 .drawio 路径。
    """
    with open(json_path, encoding="utf-8") as handle:
        spec = json.load(handle)
    name = os.path.splitext(os.path.basename(json_path))[0]
    target = os.path.join(out_dir or os.path.dirname(json_path), name + ".drawio")
    with open(target, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(build_document(spec, name))
    return target


def main():
    """命令行入口。输入：目录与 --check；输出：退出码与报告。"""
    parser = argparse.ArgumentParser(description="把图纸 JSON 转成 draw.io 原生 .drawio 文件")
    parser.add_argument("directory", help="包含 *.json 图纸定义的目录")
    parser.add_argument("--check", action="store_true", help="只校验已生成的 .drawio，不重新转换")
    args = parser.parse_args()

    if args.check:
        targets = sorted(glob.glob(os.path.join(args.directory, "*.drawio")))
        if not targets:
            print("没有找到任何 .drawio 文件")
            return 1
        problems = []
        for target in targets:
            problems += check(target)
        for problem in problems:
            print("问题:", problem)
        print(f"校验完成：{len(targets)} 个文件，{len(problems)} 个问题")
        return 1 if problems else 0

    sources = [p for p in sorted(glob.glob(os.path.join(args.directory, "*.json")))
               if not p.endswith(".layout.json")]
    if not sources:
        print("没有找到任何图纸 JSON")
        return 1
    problems = []
    for source in sources:
        target = convert(source)
        problems += check(target)
        print(f"已生成 {target}（{os.path.getsize(target)} 字节）")
    for problem in problems:
        print("问题:", problem)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
