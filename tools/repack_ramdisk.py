#!/usr/bin/env python3
"""Repack an Android emulator ramdisk with an extra file.

The engine's `-ramdisk` input is the documented boot artifact for the root lab.
This tool decodes every LZ4 legacy frame, rebuilds the cpio archive of the
frame that owns the target path, and re-encodes the same frame layout. Frames
are emitted as valid literal-only LZ4 blocks, so the tool needs no external
compression binary.

Usage:
    python tools/repack_ramdisk.py <in.img> <out.img> --add <guest/path> --content <text>
"""

from __future__ import annotations

import argparse
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from probe_ramdisk import (  # noqa: E402
    CPIO_MAGIC,
    LZ4_LEGACY_MAGIC,
    decode_container,
    parse_cpio_segment,
)

FRAME_BLOCK = 2 * 1024 * 1024


def lz4_literal_block(payload: bytes) -> bytes:
    """Encode one block as a single literal run (valid LZ4, no matches)."""
    out = bytearray()
    token = 0xF0 if len(payload) >= 15 else (len(payload) << 4)
    out.append(token)
    if len(payload) >= 15:
        remaining = len(payload) - 15
        while remaining >= 255:
            out.append(255)
            remaining -= 255
        out.append(remaining)
    out += payload
    return bytes(out)


def encode_lz4_frames(payloads: list[bytes]) -> bytes:
    out = bytearray()
    for payload in payloads:
        for start in range(0, len(payload), FRAME_BLOCK):
            block = payload[start:start + FRAME_BLOCK]
            encoded = lz4_literal_block(block)
            out += struct.pack("<I", LZ4_LEGACY_MAGIC)
            out += struct.pack("<I", len(encoded))
            out += encoded
    return bytes(out)


def build_cpio(entries: list[tuple[str, int, bytes]], ino_start: int = 300000) -> bytes:
    out = bytearray()
    ino = ino_start

    def write_entry(name: str, mode: int, data: bytes, nlink: int = 1) -> None:
        nonlocal ino
        name_bytes = name.encode() + b"\0"
        header = CPIO_MAGIC + b"".join(
            f"{value:08x}".encode()
            for value in (
                ino, mode, 0, 0, nlink, 0,
                len(data), 0, 0, 0, 0, len(name_bytes), 0,
            )
        )
        out.extend(header)
        out.extend(name_bytes)
        while len(out) % 4:
            out.append(0)
        out.extend(data)
        while len(out) % 4:
            out.append(0)
        ino += 1

    for name, mode, data in entries:
        if name == "TRAILER!!!":
            continue
        write_entry(name, mode, data)

    write_entry("TRAILER!!!", 0, b"", nlink=1)
    return bytes(out)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("source")
    parser.add_argument("output")
    parser.add_argument("--add", help="Guest path, e.g. system/etc/ramdisk/ws-probe.txt")
    parser.add_argument("--content", default="emulator-workspace ramdisk probe\n")
    parser.add_argument(
        "--append-line",
        help="Append a line to an existing guest file, formatted as <guest/path>=<text>")
    args = parser.parse_args()

    if not args.add and not args.append_line:
        raise SystemExit("nothing to do: pass --add or --append-line")

    append_path = None
    append_text = None
    if args.append_line:
        append_path, _, append_text = args.append_line.partition("=")
        if not append_path or not append_text:
            raise SystemExit("--append-line must look like <guest/path>=<text>")
        append_path = append_path.lstrip("/")

    raw = open(args.source, "rb").read()
    container, payloads = decode_container(raw)
    if not container.startswith("lz4-legacy"):
        raise SystemExit(f"unexpected container {container}; refusing to repack blind")

    frames: list[bytes] = []
    added = False
    appended = False
    guest_path = (args.add or "").lstrip("/")

    for payload in payloads:
        entries = parse_cpio_segment(payload)
        if not entries:
            frames.append(payload)
            continue

        rebuilt: list[tuple[str, int, bytes]] = []
        for entry in entries:
            if entry["name"] == "TRAILER!!!":
                continue
            if guest_path and entry["name"] == guest_path:
                continue
            start = entry["data_offset"]
            data = payload[start:start + entry["size"]]
            if append_path and entry["name"] == append_path:
                data = data + (append_text + "\n").encode()
                appended = True
            rebuilt.append((entry["name"], entry["mode"], data))

        # Keep the marker in the generic ramdisk frame (the one that owns /init).
        if guest_path and not added and any(name == "init" for name, _, _ in rebuilt):
            rebuilt.append((guest_path, 0o100644, args.content.encode()))
            added = True

        frames.append(build_cpio(rebuilt))

    if guest_path and not added:
        raise SystemExit("no frame containing 'init' was found; refusing to guess")
    if append_path and not appended:
        raise SystemExit(f"no cpio entry named '{append_path}' was found")

    new_image = encode_lz4_frames(frames)
    with open(args.output, "wb") as handle:
        handle.write(new_image)

    print(f"source      : {args.source} ({len(raw)} bytes, {container})")
    print(f"output      : {args.output} ({len(new_image)} bytes, {len(frames)} frames)")
    print(f"added       : /{guest_path} ({len(args.content)} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
