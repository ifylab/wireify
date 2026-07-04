# SPDX-License-Identifier: Apache-2.0
# Rebuilds the derived icon artifacts from the canvas-designed brand assets (pure stdlib).
# Source of truth: the .ify design-system project (Claude Design); vector masters mirrored
# in assets/brand/. This script only wraps the shipped panel PNG into the .ico Rhino's
# Panels.RegisterPanel needs - it draws nothing.
import struct
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

SOURCES = {
    "gh component (24px)": ROOT / "src" / "WireifyGh" / "Resources" / "wireify-24.png",
    "gh ribbon (16px)": ROOT / "src" / "WireifyGh" / "Resources" / "wireify-16.png",
    "rhino panel (32px)": ROOT / "src" / "Wireify" / "Resources" / "wireify-panel-light.png",
}


def png_size(data):
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("not a PNG")
    width, height = struct.unpack(">II", data[16:24])
    return width, height


def ico_from_png(png, size):
    entry = struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(png), 22)
    return struct.pack("<HHH", 0, 1, 1) + entry + png


def main():
    for label, path in SOURCES.items():
        data = path.read_bytes()
        print(f"{label}: {path.name} {png_size(data)}")

    panel = (ROOT / "src" / "Wireify" / "Resources" / "wireify-panel-light.png").read_bytes()
    size = png_size(panel)[0]
    ico_path = ROOT / "src" / "Wireify" / "Resources" / "wireify.ico"
    ico_path.write_bytes(ico_from_png(panel, size))
    print("rebuilt", ico_path)


if __name__ == "__main__":
    main()
