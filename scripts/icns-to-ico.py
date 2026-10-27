#!/usr/bin/env python3
"""Regenerate the Windows exe icon from the macOS app's AppIcon.icns.

The two projects deliberately share one icon: the Windows `PTMonitor.exe`
embeds `src/PTMonitor.App/app.ico`, and this script rebuilds that file from
the macOS original so the artwork can never drift. Run it whenever
`Resources/AppIcon.icns` changes in the sibling macOS repo:

    python3 scripts/icns-to-ico.py [path-to-icns] [path-to-ico]

Defaults:
    icns: ../pt-monitor/Resources/AppIcon.icns   (relative to this repo)
    ico:  src/PTMonitor.App/app.ico

Stdlib only. The .ico uses PNG-compressed records (supported by Windows
since Vista) at the icns's native sizes 32/64/128/256 — no resampling, so
the output is byte-for-byte deterministic.
"""

import pathlib
import struct
import sys

# OSType code -> square pixel size, for the PNG-bearing icns entries.
ICNS_PNG_SIZES = {
    b"ic11": 32,
    b"ic12": 64,
    b"ic07": 128,
    b"ic08": 256,
    b"ic09": 512,
    b"ic13": 256,
    b"ic14": 512,
    b"ic10": 1024,
}
ICO_SIZES = (32, 64, 128, 256)
PNG_MAGIC = b"\x89PNG\r\n\x1a\n"


def extract_pngs(icns_path: pathlib.Path) -> dict[int, bytes]:
    data = icns_path.read_bytes()
    if data[:4] != b"icns":
        raise SystemExit(f"{icns_path} is not an icns file")
    found: dict[int, bytes] = {}
    offset = 8  # magic + total length
    while offset < len(data):
        code, length = data[offset:offset + 4], struct.unpack(">I", data[offset + 4:offset + 8])[0]
        payload = data[offset + 8:offset + length]
        size = ICNS_PNG_SIZES.get(code)
        # icns files list entries from largest to smallest; keep the first
        # (highest-quality) payload for each size.
        if size is not None and payload.startswith(PNG_MAGIC) and size not in found:
            found[size] = payload
        offset += length
    missing = [size for size in ICO_SIZES if size not in found]
    if missing:
        raise SystemExit(f"{icns_path} is missing PNG entries for {missing}")
    return {size: found[size] for size in ICO_SIZES}


def write_ico(pngs: dict[int, bytes], out_path: pathlib.Path) -> None:
    entries = sorted(pngs.items())
    directory_size = 6 + 16 * len(entries)
    header = struct.pack("<HHH", 0, 1, len(entries))

    directory = b""
    payload = b""
    offset = directory_size
    for size, png in entries:
        # The ICO directory stores width/height in one byte each; 256 (which
        # does not fit) is encoded as 0.
        dimension = 0 if size == 256 else size
        directory += struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(png), offset)
        payload += png
        offset += len(png)

    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_bytes(header + directory + payload)
    print(f"wrote {out_path} ({offset} bytes, sizes {list(pngs)})")


def main(argv: list[str]) -> None:
    root = pathlib.Path(__file__).resolve().parent.parent
    icns = pathlib.Path(argv[1]) if len(argv) > 1 else root.parent / "pt-monitor" / "Resources" / "AppIcon.icns"
    ico = pathlib.Path(argv[2]) if len(argv) > 2 else root / "src" / "PTMonitor.App" / "app.ico"
    if not icns.exists():
        raise SystemExit(f"icns not found: {icns}")
    write_ico(extract_pngs(icns), ico)


if __name__ == "__main__":
    main(sys.argv)
