// Generates app/Windex/Assets/Windex.ico (16–256 px) and Windex-256.png: an original Windex mark,
// a rounded tile with the aurora gradient (violet → orchid → amber) holding two glass pane cards,
// the focused one with a ">_" prompt. Small sizes drop the cards and keep a bold ">_".
//   bun scripts/make-icon.ts [out.ico]
import { deflateSync, crc32 } from "zlib";

type RGBA = [number, number, number, number];

const lerp = (a: number, b: number, t: number) => a + (b - a) * t;
const mix = (a: RGBA, b: RGBA, t: number): RGBA => [lerp(a[0], b[0], t), lerp(a[1], b[1], t), lerp(a[2], b[2], t), lerp(a[3], b[3], t)];
const over = (dst: RGBA, src: RGBA): RGBA => {
  const a = src[3] + dst[3] * (1 - src[3]);
  if (a <= 0) return [0, 0, 0, 0];
  const c = (i: number) => (src[i] * src[3] + dst[i] * dst[3] * (1 - src[3])) / a;
  return [c(0), c(1), c(2), a];
};

// Signed distance to a rounded rectangle (negative inside).
function sdRoundRect(x: number, y: number, x0: number, y0: number, x1: number, y1: number, r: number) {
  const cx = (x0 + x1) / 2, cy = (y0 + y1) / 2, hx = (x1 - x0) / 2 - r, hy = (y1 - y0) / 2 - r;
  const dx = Math.abs(x - cx) - hx, dy = Math.abs(y - cy) - hy;
  return Math.hypot(Math.max(dx, 0), Math.max(dy, 0)) + Math.min(Math.max(dx, dy), 0) - r;
}

// Distance to a segment.
function sdSegment(x: number, y: number, ax: number, ay: number, bx: number, by: number) {
  const px = x - ax, py = y - ay, vx = bx - ax, vy = by - ay;
  const t = Math.max(0, Math.min(1, (px * vx + py * vy) / (vx * vx + vy * vy)));
  return Math.hypot(px - vx * t, py - vy * t);
}

const violet: RGBA = [0x6d / 255, 0x5b / 255, 0xd0 / 255, 1];
const orchid: RGBA = [0xc4 / 255, 0x6b / 255, 0xb5 / 255, 1];
const amber: RGBA = [0xf2 / 255, 0xa6 / 255, 0x5a / 255, 1];
const ink: RGBA = [0x4a / 255, 0x3a / 255, 0xb0 / 255, 1];

function sample(u: number, v: number, small: boolean): RGBA {
  let out: RGBA = [0, 0, 0, 0];
  // Tile: a rounded square with the diagonal aurora gradient and a soft top sheen.
  if (sdRoundRect(u, v, 0.04, 0.04, 0.96, 0.96, 0.22) > 0) return out;
  const t = Math.min(1, Math.max(0, (u * 0.55 + v * 0.45 - 0.04) / 0.92));
  let bg = t < 0.5 ? mix(violet, orchid, t / 0.5) : mix(orchid, amber, (t - 0.5) / 0.5);
  if (v < 0.5) bg = mix(bg, [1, 1, 1, 1], 0.12 * (1 - v / 0.5));
  out = over(out, bg);

  const prompt = (ox: number, oy: number, s: number, color: RGBA, weight: number) => {
    // ">" chevron and "_" underscore, in a box at (ox, oy) of size s.
    const d = Math.min(
      sdSegment(u, v, ox + 0.05 * s, oy + 0.18 * s, ox + 0.38 * s, oy + 0.45 * s),
      sdSegment(u, v, ox + 0.38 * s, oy + 0.45 * s, ox + 0.05 * s, oy + 0.72 * s),
      sdSegment(u, v, ox + 0.5 * s, oy + 0.74 * s, ox + 0.92 * s, oy + 0.74 * s),
    );
    if (d < weight * s) out = over(out, color);
  };

  if (small) {
    prompt(0.2, 0.2, 0.6, [1, 1, 1, 1], 0.075);
    return out;
  }
  // Right card (unfocused glass) and left card (focused, brighter) with the prompt.
  if (sdRoundRect(u, v, 0.585, 0.25, 0.82, 0.75, 0.055) < 0) out = over(out, [1, 1, 1, 0.42]);
  if (sdRoundRect(u, v, 0.18, 0.25, 0.555, 0.75, 0.055) < 0) out = over(out, [1, 1, 1, 0.94]);
  prompt(0.235, 0.33, 0.27, ink, 0.07);
  return out;
}

function png(size: number): Buffer {
  const small = size <= 24;
  const ss = 5;
  const rows: Buffer[] = [];
  for (let y = 0; y < size; y++) {
    const row = Buffer.alloc(1 + size * 4);
    for (let x = 0; x < size; x++) {
      let acc: RGBA = [0, 0, 0, 0];
      for (let sy = 0; sy < ss; sy++)
        for (let sx = 0; sx < ss; sx++) {
          const c = sample((x + (sx + 0.5) / ss) / size, (y + (sy + 0.5) / ss) / size, small);
          acc = [acc[0] + c[0] * c[3], acc[1] + c[1] * c[3], acc[2] + c[2] * c[3], acc[3] + c[3]];
        }
      const n = ss * ss, a = acc[3] / n;
      const o = 1 + x * 4;
      row[o] = a > 0 ? Math.round((acc[0] / acc[3]) * 255) : 0;
      row[o + 1] = a > 0 ? Math.round((acc[1] / acc[3]) * 255) : 0;
      row[o + 2] = a > 0 ? Math.round((acc[2] / acc[3]) * 255) : 0;
      row[o + 3] = Math.round(a * 255);
    }
    rows.push(row);
  }
  const chunk = (t: string, d: Buffer) => {
    const len = Buffer.alloc(4); len.writeUInt32BE(d.length);
    const td = Buffer.concat([Buffer.from(t, "ascii"), d]);
    const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(td) >>> 0);
    return Buffer.concat([len, td, crc]);
  };
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(size, 0); ihdr.writeUInt32BE(size, 4); ihdr[8] = 8; ihdr[9] = 6;
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk("IHDR", ihdr), chunk("IDAT", deflateSync(Buffer.concat(rows), { level: 9 })), chunk("IEND", Buffer.alloc(0))]);
}

const sizes = [16, 20, 24, 32, 40, 48, 64, 256];
const imgs = sizes.map(png);
const hdr = Buffer.alloc(6); hdr.writeUInt16LE(0, 0); hdr.writeUInt16LE(1, 2); hdr.writeUInt16LE(sizes.length, 4);
let off = 6 + 16 * sizes.length;
const ents = sizes.map((s, i) => { const e = Buffer.alloc(16); e[0] = s % 256; e[1] = s % 256; e.writeUInt16LE(1, 4); e.writeUInt16LE(32, 6); e.writeUInt32LE(imgs[i].length, 8); e.writeUInt32LE(off, 12); off += imgs[i].length; return e; });
const out = process.argv[2] ?? "app/Windex/Assets/Windex.ico";
await Bun.write(out, Buffer.concat([hdr, ...ents, ...imgs]));
await Bun.write(out.replace(/\.ico$/, "-256.png"), imgs[imgs.length - 1]);
console.log("wrote", out);
