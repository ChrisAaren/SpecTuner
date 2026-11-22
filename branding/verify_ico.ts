// Parses icon.ico back and lists its entries. Exits non-zero if anything is inconsistent.
// bun verify_ico.ts [path]   (default: icon.ico next to this script)
import path from "node:path"

const file = process.argv[2] ?? path.join(import.meta.dirname, "icon.ico")
const buf = Buffer.from(await Bun.file(file).arrayBuffer())
const fail = (msg: string) => {
  console.error(`FAIL: ${msg}`)
  process.exit(1)
}

const reserved = buf.readUInt16LE(0)
const type = buf.readUInt16LE(2)
const count = buf.readUInt16LE(4)
if (reserved !== 0 || type !== 1) fail(`not an icon file (reserved=${reserved}, type=${type})`)
console.log(`${file}: ${buf.length} bytes, ${count} entries`)

const seen: number[] = []
for (let i = 0; i < count; i++) {
  const d = 6 + 16 * i
  const w = buf[d] || 256
  const h = buf[d + 1] || 256
  const planes = buf.readUInt16LE(d + 4)
  const bpp = buf.readUInt16LE(d + 6)
  const size = buf.readUInt32LE(d + 8)
  const offset = buf.readUInt32LE(d + 12)
  if (offset + size > buf.length) fail(`entry ${i} runs past the end of the file`)
  const data = buf.subarray(offset, offset + size)
  let format: string
  if (data.subarray(0, 8).equals(Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]))) {
    const pw = data.readUInt32BE(16)
    const ph = data.readUInt32BE(20)
    const depth = data[24]
    const colour = data[25]
    if (pw !== w || ph !== h) fail(`entry ${i}: PNG is ${pw}x${ph}, directory says ${w}x${h}`)
    if (colour !== 6 || depth !== 8) fail(`entry ${i}: PNG is not 8-bit RGBA (depth ${depth}, colour type ${colour})`)
    format = "PNG RGBA8"
  } else {
    const hdr = data.readUInt32LE(0)
    const bw = data.readInt32LE(4)
    const bh = data.readInt32LE(8)
    const bbpp = data.readUInt16LE(14)
    if (hdr !== 40) fail(`entry ${i}: unknown image header (${hdr})`)
    if (bw !== w || bh !== 2 * h) fail(`entry ${i}: DIB is ${bw}x${bh}, expected ${w}x${2 * h}`)
    const mask = Math.ceil(w / 32) * 4 * h
    if (size !== 40 + w * h * 4 + mask) fail(`entry ${i}: DIB size ${size} does not match ${w}x${h} 32-bit + mask`)
    format = `DIB ${bbpp}-bit BGRA + AND mask`
  }
  console.log(`  #${i}  ${w}x${h}  planes=${planes} bpp=${bpp}  ${size} bytes @${offset}  ${format}`)
  seen.push(w)
}
const want = [16, 24, 32, 48, 64, 128, 256]
const missing = want.filter((s) => !seen.includes(s))
if (missing.length) fail(`missing sizes: ${missing.join(", ")}`)
console.log("OK: all sizes present:", want.join(", "))
