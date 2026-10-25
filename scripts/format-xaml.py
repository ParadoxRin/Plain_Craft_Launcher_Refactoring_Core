#!/usr/bin/env python3
"""展开自有 XAML 控件树；保持属性、文本、注释和元素顺序不变。"""
import hashlib
import json
from pathlib import Path
import sys
from xml.dom import Node, minidom


def is_container(node):
    """只调整纯控件容器；混合文本与 xml:space 保留区域不参与排版。"""
    return (
        node.nodeType == Node.ELEMENT_NODE
        and node.getAttribute("xml:space") != "preserve"
        and any(child.nodeType == Node.ELEMENT_NODE for child in node.childNodes)
        and not any(
            child.nodeType in (Node.TEXT_NODE, Node.CDATA_SECTION_NODE)
            and child.data.strip()
            for child in node.childNodes
        )
    )


def signature(node):
    """递归比较 XML 内容，只忽略可排版容器节点之间的空白。"""
    children = [
        signature(child)
        for child in node.childNodes
        if not (
            is_container(node)
            and child.nodeType == Node.TEXT_NODE
            and not child.data.strip()
        )
    ]
    attrs = sorted(node.attributes.items()) if node.attributes else []
    return node.nodeType, node.nodeName, getattr(node, "data", None), attrs, children


def indent(node, level=0):
    """每个容器的子节点另起一行，并递增四个空格。"""
    if node.nodeType == Node.ELEMENT_NODE and node.getAttribute("xml:space") == "preserve":
        return
    if not is_container(node):
        return
    children = list(node.childNodes)
    for child in children:
        if child.nodeType == Node.TEXT_NODE and not child.data.strip():
            node.removeChild(child)
    for child in list(node.childNodes):
        node.insertBefore(node.ownerDocument.createTextNode("\n" + "    " * (level + 1)), child)
        indent(child, level + 1)
    node.appendChild(node.ownerDocument.createTextNode("\n" + "    " * level))


def format_xml(text):
    document = minidom.parseString(text)
    indent(document.documentElement)
    return document.documentElement.toxml() + "\n"


def main():
    """先验证内容等价和幂等性，再写回；历史交付包不在扫描范围内。"""
    root = Path(__file__).resolve().parent.parent
    check = "--check" in sys.argv
    pending = []
    report = []
    for path in sorted((root / "src").rglob("*.axaml")):
        if any(part in {"bin", "obj"} for part in path.relative_to(root).parts):
            continue
        original = path.read_text()
        output = format_xml(original)
        assert signature(minidom.parseString(original).documentElement) == signature(
            minidom.parseString(output).documentElement
        ), path
        assert format_xml(output) == output, path
        if original != output:
            pending.append((path, output))
        report.append({
            "file": str(path.relative_to(root)),
            "changed": original != output,
            "xmlEquivalent": True,
            "idempotent": True,
            "beforeSha256": hashlib.sha256(original.encode()).hexdigest(),
            "afterSha256": hashlib.sha256(output.encode()).hexdigest(),
        })
    if check:
        print(f"Checked {len(report)} XAML files; {len(pending)} require formatting.")
        return bool(pending)
    for path, output in pending:
        path.write_text(output)
    audit = root / "artifacts/formatting-current/xaml-audit.json"
    audit.parent.mkdir(parents=True, exist_ok=True)
    audit.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
    print(f"Formatted {len(pending)} XAML files; XML content and idempotence verified.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
