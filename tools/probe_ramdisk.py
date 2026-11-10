#!/usr/bin/env python3
"""Inspect an Android emulator ramdisk without external dependencies.

The API 35 emulator ramdisk is a sequence of concatenated LZ4 legacy frames.
Each frame wraps a newc cpio archive; the first holds the generic ramdisk and
later frames hold first-stage fragments. Magisk patching has to decode every
frame, modify the cpio members, and re-encode the same frame layout.

Usage:
    python tools/probe_ramdisk.py <ramdisk.img> [--extract <dir>]
"""

from __future__ import annotations

import argparse
import gzip
import hashlib
import os
import struct
import sys

LZ4_LEGACY_MAGIC = 0x184C2102
CPIO_MAGIC = b"070701"


def lz4_block_decompress(src: bytes) -> bytes:
    """Minimal LZ4 block decoder (sequence decoding, overlapping matches)."""
    dst = bytearray()
    i = 0
    n = len(src)
    while i < n:
        token = src[i]
        i += 1

        literal_length = token >> 4
        if literal_length == 15:
            while i < n:
                extra = src[i]
                i += 1
                literal_length += extra
                if extra != 255:
                    break

        if literal_length:
            dst += src[i:i + literal_length]
            i += literal_length

        if i >= n:
            break

        offset = src[i] | (src[i + 1] << 8)
        i += 2
        if offset == 0:
            raise ValueError("invalid LZ4 match offset 0")

        match_length = token & 0x0F
        if match_length == 15:
            while i < n:
                extra = src[i]
                i += 1
                match_length += extra
                if extra != 255:
                    break
        match_length += 4

        start = len(dst) - offset
        if start < 0:
            raise ValueError("LZ4 match points before start of output")
        for k in range(match_length):
            dst.append(dst[start + k])

    return bytes(dst)


def decode_lz4_frames(data: bytes) -> list[bytes]:
    payloads: list[bytes] = []
    offset = 0
    while offset + 8 <= len(data) and struct.unpack_from("<I", data, offset)[0] == LZ4_LEGACY_MAGIC:
        offset += 4
        (compressed_size,) = struct.unpack_from("<I", data, offset)
        offset += 4
        if compressed_size == 0 or offset + compressed_size > len(data):
            raise ValueError(f"truncated LZ4 frame at offset {offset - 8}")
        payloads.append(lz4_block_decompress(data[offset:offset + compressed_size]))
        offset += compressed_size

    if offset != len(data):
        # Trailing bytes are not part of any frame; keep them visible to the caller.
        payloads.append(data[offset:])
    return payloads


def decode_container(data: bytes) -> tuple[str, list[bytes]]:
    if len(data) >= 4 and struct.unpack_from("<I", data, 0)[0] == LZ4_LEGACY_MAGIC:
        frames = decode_lz4_frames(data)
        return f"lz4-legacy({len(frames)} frames)", frames

    if data[:2] == b"\x1f\x8b":
        return "gzip", [gzip.decompress(data)]

    if data.startswith(CPIO_MAGIC):
        return "raw-cpio", [data]

    return "unknown", [data]


def parse_cpio_segment(data: bytes) -> list[dict]:
    entries = []
    offset = 0
    if data[:6] != CPIO_MAGIC:
        return entries

    while offset + 110 <= len(data):
        if data[offset:offset + 6] != CPIO_MAGIC:
            break
        fields = []
        for i in range(13):
            raw = data[offset + 6 + i * 8:offset + 6 + (i + 1) * 8]
            fields.append(int(raw, 16))
        mode, filesize, namesize = fields[1], fields[6], fields[11]
        name = data[offset + 110:offset + 110 + namesize - 1].decode("utf-8", "replace")
        data_start = (offset + 110 + namesize + 3) & ~3
        entries.append({
            "name": name,
            "mode": mode,
            "size": filesize,
            "data_offset": data_start,
            "segment": data,
        })
        if name == "TRAILER!!!":
            break
        offset = (data_start + filesize + 3) & ~3
    return entries


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("ramdisk")
    parser.add_argument("--extract")
    args = parser.parse_args()

    data = open(args.ramdisk, "rb").read()
    print(f"file            : {args.ramdisk}")
    print(f"size            : {len(data)} bytes")
    print(f"sha256          : {hashlib.sha256(data).hexdigest()}")

    container, payloads = decode_container(data)
    print(f"container       : {container}")
    for index, payload in enumerate(payloads):
        entries = parse_cpio_segment(payload)
        print(f"  frame {index}: {len(payload)} bytes, {len(entries)} cpio entries, "
              f"sha256 {hashlib.sha256(payload).hexdigest()[:16]}")
        for entry in entries:
            mode = entry["mode"]
            kind = "dir" if (mode & 0o170000) == 0o040000 else "file"
            print(f"    {entry['name']:<40} {kind:<4} {entry['size']} bytes")

    all_names = [e["name"] for payload in payloads for e in parse_cpio_segment(payload)]
    print("magisk-relevant :")
    for wanted in ("init", "init.rc", "first_stage_ramdisk", "overlay.d"):
        hits = sum(1 for name in all_names if name == wanted or name.startswith(wanted + "/"))
        print(f"  {wanted:<32} {'present' if hits else 'absent'}")

    if args.extract:
        os.makedirs(args.extract, exist_ok=True)
        for payload in payloads:
            for entry in parse_cpio_segment(payload):
                if entry["name"] == "TRAILER!!!":
                    continue
                target = os.path.join(args.extract, entry["name"].lstrip("/"))
                if (entry["mode"] & 0o170000) == 0o040000:
                    os.makedirs(target, exist_ok=True)
                    continue
                os.makedirs(os.path.dirname(target), exist_ok=True)
                start = entry["data_offset"]
                with open(target, "wb") as handle:
                    handle.write(payload[start:start + entry["size"]])
        print(f"extracted to    : {args.extract}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
