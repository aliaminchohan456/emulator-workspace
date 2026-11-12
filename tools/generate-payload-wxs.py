#!/usr/bin/env python3
"""Generates the WiX payload fragment for a harvested directory.

A per-user package installs into the user profile, so every component must
use a registry key under HKCU as its key path (ICE38). The harvested payload
therefore becomes one component per directory, each with a registry key path
and one file element per payload file.

The output is deterministic: the same payload always produces the same
identifiers and the same component GUIDs.
"""

import hashlib
import pathlib
import sys


def identifier(prefix: str, value: str) -> str:
    digest = hashlib.sha256(value.encode("utf-8")).hexdigest()[:16]
    return f"{prefix}_{digest}"


def component_guid(value: str) -> str:
    digest = hashlib.sha256(("component:" + value).encode("utf-8")).hexdigest()
    return f"{digest[0:8]}-{digest[8:12]}-{digest[12:16]}-{digest[16:20]}-{digest[20:32]}".upper()


def escape(value: str) -> str:
    return (value.replace("&", "&amp;").replace("<", "&lt;")
            .replace(">", "&gt;").replace('"', "&quot;"))


def main() -> int:
    if len(sys.argv) < 3:
        print("usage: generate-payload-wxs.py <payload-dir> <output.wxs>", file=sys.stderr)
        return 2

    payload = pathlib.Path(sys.argv[1]).resolve()
    output = pathlib.Path(sys.argv[2])

    files = sorted(
        (path for path in payload.rglob("*") if path.is_file()),
        key=lambda path: str(path.relative_to(payload)).lower(),
    )
    if not files:
        print("the payload is empty", file=sys.stderr)
        return 2

    # The directories of the payload, relative to the install folder.
    directories: set[str] = set()
    for path in files:
        parent = path.relative_to(payload).parent
        parts = [] if str(parent) == "." else parent.parts
        for index in range(len(parts)):
            directories.add("/".join(parts[: index + 1]))

    directory_id = {directory: identifier("dir", directory) for directory in directories}

    lines = [
        '<?xml version="1.0" encoding="utf-8"?>',
        '<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">',
        "  <Fragment>",
        '    <StandardDirectory Id="LocalAppDataFolder">',
        '      <Directory Id="ProgramsFolder" Name="Programs">',
        '        <Directory Id="INSTALLFOLDER" Name="Emulator Workspace">',
        '          <Directory Id="MenuFolder" Name="Emulator Workspace" />',
    ]

    # The directory tree, nested so that every parent exists.
    def emit(directory: str, indent: str) -> None:
        children = []
        for candidate in directories:
            parts = candidate.split("/")
            if len(parts) == len(directory.split("/")) + 1 and candidate.startswith(directory + "/"):
                children.append(candidate)
        for child in sorted(children):
            name = child.split("/")[-1]
            lines.append(f'{indent}    <Directory Id="{directory_id[child]}" Name="{escape(name)}">')
            emit(child, indent + "    ")
            lines.append(f"{indent}    </Directory>")

    for top in sorted(d for d in directories if "/" not in d):
        name = top
        lines.append(f'          <Directory Id="{directory_id[top]}" Name="{escape(name)}">')
        emit(top, "          ")
        lines.append("          </Directory>")

    lines.append("        </Directory>")
    lines.append("      </Directory>")
    lines.append("    </StandardDirectory>")
    lines.append("  </Fragment>")
    lines.append("")

    # One component per directory, each with a registry key path.
    by_directory: dict[str, list[pathlib.Path]] = {}
    for path in files:
        relative = path.relative_to(payload)
        parent = relative.parent
        key = "" if str(parent) == "." else "/".join(parent.parts)
        by_directory.setdefault(key, []).append(path)

    lines.append("  <Fragment>")
    lines.append('    <ComponentGroup Id="ApplicationFiles" Directory="INSTALLFOLDER">')
    for key in sorted(by_directory):
        directory = "INSTALLFOLDER" if key == "" else directory_id[key]
        lines.append(f'      <Component Id="{identifier("cmp", key)}" Directory="{directory}" '
                     f'Guid="{component_guid(key)}">')
        lines.append('        <RegistryValue Root="HKCU"')
        lines.append('                       Key="Software\\Emulator Workspace\\Payload"')
        lines.append(f'                       Name="{escape(identifier("f", key))}"')
        lines.append('                       Type="integer"')
        lines.append('                       Value="1"')
        lines.append('                       KeyPath="yes" />')
        for path in by_directory[key]:
            relative = path.relative_to(payload).as_posix()
            name = path.name
            source = "payload\\" + relative.replace("/", "\\")
            file_id = identifier("file", relative)
            lines.append(f'        <File Id="{file_id}" Source="{source}" '
                         f'Name="{escape(name)}" />')
        lines.append("      </Component>")
    lines.append("    </ComponentGroup>")
    lines.append("  </Fragment>")
    lines.append("")

    lines.append("</Wix>")

    output.write_text("\n".join(lines), encoding="utf-8")
    print(f"{len(files)} payload files in {len(by_directory)} components")
    return 0


if __name__ == "__main__":
    sys.exit(main())
