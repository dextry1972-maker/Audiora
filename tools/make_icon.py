"""Generates the Sonos Flow app icon (ICO with PNG frames) with no third-party libraries.

Design: graphite rounded tile, emerald source dot and two sound-wave arcs.
Usage: python -I tools/make_icon.py <output.ico> [<preview.png>]
"""
import math
import struct
import sys
import zlib

SIZE = 256
SS = 4  # supersampling per axis


def lerp(a, b, t):
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(len(a)))


def inside_round_rect(x, y, size, r):
    cx = min(max(x, r), size - r)
    cy = min(max(y, r), size - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r


def shade(x, y):
    """Colour (r, g, b, a) at a point in 0..256 space."""
    if not inside_round_rect(x, y, SIZE, 58):
        return (0, 0, 0, 0)
    t = y / SIZE
    base = lerp((22, 30, 35), (8, 11, 13), t)
    # Soft emerald ambient glow from the lower left.
    glow = max(0.0, 1 - math.hypot(x - 70, y - 200) / 190) ** 2 * 0.35
    base = lerp(base, (40, 200, 120), glow)

    cx, cy = 96, 128
    d = math.hypot(x - cx, y - cy)
    ang = abs(math.degrees(math.atan2(y - cy, x - cx)))
    emerald = lerp((70, 235, 145), (24, 170, 96), t)
    if d <= 24:
        return (*emerald, 255)
    for r0, r1 in ((58, 76), (100, 118)):
        if r0 <= d <= r1 and ang <= 48:
            # Round the arc ends.
            return (*emerald, 255)
        for sign in (1, -1):
            ex = cx + (r0 + r1) / 2 * math.cos(math.radians(48))
            ey = cy + sign * (r0 + r1) / 2 * math.sin(math.radians(48))
            if math.hypot(x - ex, y - ey) <= (r1 - r0) / 2:
                return (*emerald, 255)
    return (*base, 255)


def render():
    px = []
    for py in range(SIZE):
        row = []
        for pxi in range(SIZE):
            acc = [0.0, 0.0, 0.0, 0.0]
            for sy in range(SS):
                for sx in range(SS):
                    r, g, b, a = shade(pxi + (sx + 0.5) / SS, py + (sy + 0.5) / SS)
                    acc[0] += r * a
                    acc[1] += g * a
                    acc[2] += b * a
                    acc[3] += a
            n = SS * SS
            a = acc[3] / n
            if a > 0:
                row.append((acc[0] / acc[3], acc[1] / acc[3], acc[2] / acc[3], a))
            else:
                row.append((0, 0, 0, 0))
        px.append(row)
    return px


def downsample(px, size):
    f = SIZE // size
    out = []
    for y in range(size):
        row = []
        for x in range(size):
            acc = [0.0] * 4
            for yy in range(f):
                for xx in range(f):
                    r, g, b, a = px[y * f + yy][x * f + xx]
                    acc[0] += r * a
                    acc[1] += g * a
                    acc[2] += b * a
                    acc[3] += a
            a = acc[3] / (f * f)
            row.append((acc[0] / acc[3], acc[1] / acc[3], acc[2] / acc[3], a) if acc[3] else (0, 0, 0, 0))
        out.append(row)
    return out


def png_bytes(px):
    h, w = len(px), len(px[0])
    raw = b"".join(b"\x00" + bytes(int(round(max(0, min(255, c)))) for p in row for c in p) for row in px)

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def main():
    out_ico = sys.argv[1]
    full = render()
    frames = [(s, png_bytes(full if s == SIZE else downsample(full, s))) for s in (256, 64, 48, 32, 16)]
    header = struct.pack("<HHH", 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    entries, blobs = b"", b""
    for s, data in frames:
        entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
        blobs += data
    with open(out_ico, "wb") as f:
        f.write(header + entries + blobs)
    if len(sys.argv) > 2:
        with open(sys.argv[2], "wb") as f:
            f.write(frames[0][1])


if __name__ == "__main__":
    main()
