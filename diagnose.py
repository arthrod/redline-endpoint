#!/usr/bin/env python3
"""
Diagnostic script to examine DOCX files and understand potential issues
that cause Word's "unreadable content" warning.
"""

import zipfile
import xml.etree.ElementTree as ET
import sys
import re
from pathlib import Path

def examine_docx(path):
    """Examine a DOCX file and report on potential issues."""
    print(f"\n{'='*60}")
    print(f"Examining: {path}")
    print(f"{'='*60}")

    if not Path(path).exists():
        print(f"  File not found!")
        return

    with zipfile.ZipFile(path, 'r') as zf:
        entries = zf.namelist()
        print(f"\nEntries ({len(entries)} files):")

        # Group by directory
        dirs = {}
        for e in entries:
            parts = e.rsplit('/', 1)
            if len(parts) == 2:
                d, f = parts
            else:
                d, f = '', parts[0]
            if d not in dirs:
                dirs[d] = []
            dirs[d].append(f)

        for d in sorted(dirs.keys()):
            print(f"  {d or '[root]'}/")
            for f in sorted(dirs[d])[:10]:
                print(f"    - {f}")
            if len(dirs[d]) > 10:
                print(f"    ... and {len(dirs[d])-10} more")

        # Check _rels/.rels (package relationships)
        if '_rels/.rels' in entries:
            print(f"\n--- Package Relationships (_rels/.rels) ---")
            content = zf.read('_rels/.rels').decode('utf-8')
            analyze_rels(content, "_rels/.rels")

        # Check word/_rels/document.xml.rels (document relationships)
        doc_rels = 'word/_rels/document.xml.rels'
        if doc_rels in entries:
            print(f"\n--- Document Relationships ({doc_rels}) ---")
            content = zf.read(doc_rels).decode('utf-8')
            analyze_rels(content, doc_rels)

        # Check for pt14/PowerTools namespace in document.xml
        if 'word/document.xml' in entries:
            print(f"\n--- Document.xml Analysis ---")
            content = zf.read('word/document.xml').decode('utf-8')
            analyze_document(content)

        # Check Content_Types
        if '[Content_Types].xml' in entries:
            print(f"\n--- Content Types ---")
            content = zf.read('[Content_Types].xml').decode('utf-8')
            analyze_content_types(content)

def analyze_rels(content, filename):
    """Analyze a relationships file for issues."""
    issues = []

    try:
        root = ET.fromstring(content)
    except ET.ParseError as e:
        print(f"  ⚠️  XML Parse Error: {e}")
        return

    # Define namespace
    ns = {'r': 'http://schemas.openxmlformats.org/package/2006/relationships'}

    relationships = root.findall('.//r:Relationship', ns)
    if not relationships:
        # Try without namespace prefix
        relationships = root.findall('.//{http://schemas.openxmlformats.org/package/2006/relationships}Relationship')

    print(f"  Found {len(relationships)} relationships:")

    for rel in relationships:
        rel_id = rel.get('Id', '?')
        target = rel.get('Target', '?')
        rel_type = rel.get('Type', '?')

        # Extract type name from full URI
        type_name = rel_type.rsplit('/', 1)[-1] if '/' in rel_type else rel_type

        # Check for issues
        rel_issues = []

        # Issue 1: GUID-style IDs
        if re.match(r'^R[0-9a-fA-F]{16,}$', rel_id):
            rel_issues.append(f"GUID-style ID")
        # Issue 2: Other non-standard ID formats (should be rId followed by number)
        elif not re.match(r'^rId\d+$', rel_id):
            rel_issues.append(f"Non-standard ID format")

        # Issue 3: Absolute paths (should be relative for internal targets)
        if target.startswith('/') and 'http' not in target:
            rel_issues.append(f"Absolute path (should be relative)")

        status = "⚠️ " if rel_issues else "✓ "
        print(f"  {status} {rel_id} -> {target[:40]} ({type_name})")

        if rel_issues:
            for issue in rel_issues:
                print(f"       └─ Issue: {issue}")
            issues.extend(rel_issues)

    if issues:
        print(f"\n  Total issues found: {len(issues)}")
    else:
        print(f"\n  ✓ No relationship issues found!")

def analyze_document(content):
    """Analyze document.xml for known issues."""
    issues = []

    # Check for pt14 namespace
    if 'powertools' in content.lower() or 'pt14' in content:
        # Count occurrences
        pt14_count = content.count('pt14:')
        ns_decl = content.count('powertools.codeplex.com')

        if pt14_count > 0:
            issues.append(f"Found {pt14_count} pt14: prefixed attributes")
        if ns_decl > 0:
            issues.append(f"Found {ns_decl} PowerTools namespace declaration(s)")

    # Check for mc:Ignorable with pt14
    if 'mc:Ignorable' in content:
        match = re.search(r'mc:Ignorable="([^"]*)"', content)
        if match:
            ignorable = match.group(1)
            print(f"  mc:Ignorable contains: {ignorable}")
            if 'pt14' in ignorable:
                issues.append("pt14 in mc:Ignorable list")

    # Check document size
    print(f"  Document size: {len(content):,} bytes")

    # Sample some namespace declarations
    ns_matches = re.findall(r'xmlns:?(\w+)?="([^"]+)"', content[:5000])
    print(f"  Namespaces found in root element:")
    for prefix, uri in ns_matches[:15]:
        flag = "⚠️" if 'powertools' in uri.lower() else "  "
        print(f"  {flag} xmlns:{prefix or 'default'}=\"{uri[:60]}...\"" if len(uri) > 60 else f"  {flag} xmlns:{prefix or 'default'}=\"{uri}\"")

    if issues:
        print(f"\n  ⚠️  Issues found:")
        for issue in issues:
            print(f"       - {issue}")
    else:
        print(f"\n  ✓ No document.xml issues found!")

def analyze_content_types(content):
    """Analyze Content_Types.xml for issues."""
    try:
        root = ET.fromstring(content)
    except ET.ParseError as e:
        print(f"  ⚠️  XML Parse Error: {e}")
        return

    # Count different types
    defaults = root.findall('.//{http://schemas.openxmlformats.org/package/2006/content-types}Default')
    overrides = root.findall('.//{http://schemas.openxmlformats.org/package/2006/content-types}Override')

    print(f"  Default types: {len(defaults)}")
    print(f"  Override types: {len(overrides)}")

    # List Override types
    print(f"\n  Override entries:")
    for o in overrides:
        part_name = o.get('PartName', '?')
        content_type = o.get('ContentType', '?')
        # Shorten content type
        ct_short = content_type.rsplit('.', 1)[-1] if '.' in content_type else content_type
        print(f"    {part_name} -> {ct_short}")

def main():
    print("DOCX Diagnostic Tool")
    print("Checking for issues that may cause Word 'unreadable content' warning\n")

    # Default files to check
    files = [
        "original.docx",
        "modified.docx",
    ]

    # Check command line args
    if len(sys.argv) > 1:
        files = sys.argv[1:]

    for f in files:
        examine_docx(f)

    print("\n" + "="*60)
    print("Analysis complete!")
    print("="*60)

if __name__ == '__main__':
    main()
