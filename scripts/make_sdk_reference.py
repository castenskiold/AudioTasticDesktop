"""Writes docs/plugins/sdk-reference.md from the SDK's XML documentation (P14).

    dotnet build src/AudioTastic.Sdk -c Release
    python scripts/make_sdk_reference.py

Every public type of AudioTastic.Sdk is listed by namespace with its summary, followed by its members. The XML file
is produced by the build (GenerateDocumentationFile); regenerate the reference whenever the SDK changes.
"""
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

REPO = pathlib.Path(__file__).resolve().parent.parent
XML = REPO / 'src' / 'AudioTastic.Sdk' / 'bin' / 'Release' / 'net10.0' / 'AudioTastic.Sdk.xml'
OUT = REPO / 'docs' / 'plugins' / 'sdk-reference.md'
VERSION = re.search(r'<Version>([^<]+)</Version>', (REPO / 'src' / 'AudioTastic.Sdk' / 'AudioTastic.Sdk.csproj').read_text(encoding='utf-8')).group(1)


def text(element):
    """The summary as one line of Markdown: <see cref> and <paramref> become code, <c> becomes code."""
    if element is None:
        return ''
    parts = []

    def walk(node):
        if node.text:
            parts.append(node.text)
        for child in node:
            if child.tag == 'see':
                ref = child.get('cref') or child.get('langword') or child.get('href') or ''
                parts.append('`' + ref.split(':')[-1].split('.')[-1].split('(')[0] + '`')
            elif child.tag in ('paramref', 'typeparamref'):
                parts.append('`' + child.get('name', '') + '`')
            elif child.tag == 'c':
                parts.append('`' + ''.join(child.itertext()) + '`')
            elif child.tag == 'code':
                parts.append(' ')
            else:
                walk(child)
            if child.tail:
                parts.append(child.tail)
    walk(element)
    return re.sub(r'\s+', ' ', ''.join(parts)).strip()


def main():
    if not XML.exists():
        sys.exit(f'{XML} not found: build the SDK in Release first.')
    members = ET.parse(XML).getroot().find('members')
    types = {}
    entries = []
    for m in members:
        name = m.get('name')
        kind, _, full = name.partition(':')
        summary = text(m.find('summary'))
        if full.startswith('System.'):
            continue  # compiler-generated (regex source generator)
        if kind == 'T':
            namespace, _, short = full.rpartition('.')
            types[full] = {'namespace': namespace, 'name': short, 'summary': summary, 'members': [], 'params': []}
            for p in m.findall('param'):
                types[full]['params'].append((p.get('name'), text(p)))
        else:
            entries.append((kind, full, summary, [(p.get('name'), text(p)) for p in m.findall('param')]))
    for kind, full, summary, params in entries:
        bare = full.split('(')[0]
        owner, _, member = bare.rpartition('.')
        if owner not in types:
            continue
        label = {'M': 'method', 'P': 'property', 'F': 'field', 'E': 'event'}.get(kind, kind)
        if member == '#ctor':
            member, label = types[owner]['name'], 'constructor'
        types[owner]['members'].append((member, label, summary, params))

    lines = [
        f'# AudioTastic SDK {VERSION} reference',
        '',
        'Generated from the SDK\'s XML documentation by `scripts/make_sdk_reference.py`; do not edit by hand. '
        'How to write a plugin, the manifest, packaging and versioning: [plugin-guide.md](plugin-guide.md).',
        '',
    ]
    by_namespace = {}
    for full, t in sorted(types.items()):
        by_namespace.setdefault(t['namespace'], []).append(t)
    lines.append('Namespaces: ' + ', '.join(f'[{ns}](#{ns.lower().replace(".", "")})' for ns in sorted(by_namespace)))
    lines.append('')
    for ns in sorted(by_namespace):
        lines += [f'## {ns}', '']
        for t in by_namespace[ns]:
            lines += [f'### {t["name"]}', '']
            if t['summary']:
                lines += [t['summary'], '']
            for name, desc in t['params']:
                lines.append(f'- `{name}`: {desc}')
            if t['params']:
                lines.append('')
            if t['members']:
                lines += ['| Member | Kind | Description |', '|---|---|---|']
                for member, label, summary, params in t['members']:
                    detail = summary
                    if params:
                        detail += ' ' + '; '.join(f'`{n}`: {d}' for n, d in params if d)
                    lines.append(f'| `{member}` | {label} | {detail.replace("|", "\\|").strip()} |')
                lines.append('')
    OUT.write_text('\n'.join(lines) + '\n', encoding='utf-8')
    print(f'wrote {OUT} ({len(types)} types)')


if __name__ == '__main__':
    main()
