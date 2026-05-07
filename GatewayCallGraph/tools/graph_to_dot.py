"""
graph_to_dot.py — convert a CallGraph JSON file (produced by GatewayCallGraph.exe)
into Graphviz DOT.

Usage:
    python graph_to_dot.py <input.json> <output.dot>

Node styling:
    controller_action  -> filled green box
    gateway_method     -> filled red box
    intermediate       -> white ellipse

Edge styling:
    no loop            -> black, normal arrow
    statement_loop     -> orange, bold, label = "<subkind> @line N"
    enumerable_loop    -> purple, bold, dashed, label = ".<subkind>() @line N"
"""

import json
import sys
import os


def short_label(node):
    """Use Type.Method as the visible label; full FQN goes in the tooltip."""
    type_name = node.get("type", "")
    method = node.get("method", "")
    # Strip namespace from type for readability
    type_short = type_name.rsplit(".", 1)[-1] if type_name else "?"
    return f"{type_short}.{method}"


def node_attrs(node):
    kind = node.get("kind", "intermediate")
    label = short_label(node)
    tooltip = node.get("fqn", label).replace('"', '\\"')

    if kind == "controller_action":
        return {
            "label": label,
            "tooltip": tooltip,
            "shape": "box",
            "style": "filled,bold",
            "fillcolor": "#b6f2c1",
            "color": "#2e7d32",
        }
    if kind == "gateway_method":
        return {
            "label": label,
            "tooltip": tooltip,
            "shape": "box",
            "style": "filled,bold",
            "fillcolor": "#ffd1d1",
            "color": "#c62828",
        }
    return {
        "label": label,
        "tooltip": tooltip,
        "shape": "ellipse",
        "style": "filled",
        "fillcolor": "white",
        "color": "#888888",
    }


def edge_attrs(edge):
    loop = edge.get("loop")
    call_site = edge.get("callSite", {})
    call_line = call_site.get("line", "?")

    base_tooltip = f"call @ line {call_line}"
    if loop is None:
        return {
            "tooltip": base_tooltip,
            "color": "#444444",
        }

    kind = loop.get("kind")
    subkind = loop.get("subkind", "?")
    loop_line = loop.get("line", "?")

    if kind == "statement_loop":
        return {
            "label": f"{subkind} @{loop_line}",
            "tooltip": f"{base_tooltip}; inside {subkind} at line {loop_line}",
            "color": "#ef6c00",
            "fontcolor": "#ef6c00",
            "penwidth": "2",
        }

    if kind == "enumerable_loop":
        return {
            "label": f".{subkind}() @{loop_line}",
            "tooltip": f"{base_tooltip}; inside .{subkind}() at line {loop_line}",
            "color": "#6a1b9a",
            "fontcolor": "#6a1b9a",
            "penwidth": "2",
            "style": "dashed",
        }

    return {
        "label": f"loop @{loop_line}",
        "tooltip": base_tooltip,
        "color": "#ef6c00",
        "penwidth": "2",
    }


def fmt_attrs(attrs):
    return ", ".join(f'{k}="{v}"' for k, v in attrs.items())


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(2)

    in_path, out_path = sys.argv[1], sys.argv[2]
    with open(in_path, "r", encoding="utf-8") as f:
        graph = json.load(f)

    nodes = graph.get("nodes", [])
    edges = graph.get("edges", [])

    lines = []
    lines.append("digraph CallGraph {")
    lines.append('  rankdir="BT";')  # callers on top, gateways on bottom
    lines.append('  graph [splines=true, overlap=false, fontname="Segoe UI", fontsize=10];')
    lines.append('  node  [fontname="Segoe UI", fontsize=10];')
    lines.append('  edge  [fontname="Segoe UI", fontsize=9];')
    lines.append("")

    # Group nodes by kind so they cluster nicely
    for node in nodes:
        nid = node["id"]
        attrs = node_attrs(node)
        lines.append(f"  n{nid} [{fmt_attrs(attrs)}];")
    lines.append("")

    for edge in edges:
        attrs = edge_attrs(edge)
        lines.append(f"  n{edge['from']} -> n{edge['to']} [{fmt_attrs(attrs)}];")

    lines.append("}")

    with open(out_path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))

    print(
        f"Wrote {out_path} ({len(nodes)} nodes, {len(edges)} edges). "
        f"Render with: dot -Tsvg \"{out_path}\" -o \"{os.path.splitext(out_path)[0]}.svg\""
    )


if __name__ == "__main__":
    main()
