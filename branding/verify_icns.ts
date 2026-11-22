// Parses icon.icns back and lists its entries. Exits non-zero if anything is inconsistent.
// bun verify_icns.ts [path]   (default: icon.icns next to this script)
import path from "node:path"

const file = process.argv[2] ?? path.join(import.meta.dirname, "icon.icns")
const buf = Buffer.from(await Bun.file(file).arrayBuffer())
const fail = (msg: string) => {
  console.error(`FAIL: ${msg}`)
  process.exit(1)
}

// OSType -> pixel size, for the PNG-compressed types make_icon.py writes (macOS 10.7 and later).
const TYPES: Record<string, number> = {
  icp4: 16,
  icp5: 32,
  icp6: 64,
  ic07: 128,
  ic08: 256,
  ic09: 512,
  ic10: 1024,
  ic11: 32,
  ic12: 64,
  ic13: 256,
  ic14: 512,
}
const PNG = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])
// Chunks that could carry text or camera and machine metadata; none may appear.
const METADATA = new Set(["tEXt", "zTXt", "iTXt", "eXIf", "tIME"])

if (buf.subarray(0, 4).toString("latin1") !== "icns") fail("not an icns file")
if (buf.readUInt32BE(4) !== buf.length) fail(`header length ${buf.readUInt32BE(4)} is not the file length ${buf.length}`)
console.log(`${file}: ${buf.length} bytes`)

const seen = new Set<string>()
let offset = 8
while (offset < buf.length) {
  if (offset + 8 > buf.length) fail(`entry header at ${offset} runs past the end of the file`)
  const type = buf.subarray(offset, offset + 4).toString("latin1")
  const length = buf.readUInt32BE(offset + 4)
  if (length < 8 || offset + length > buf.length) fail(`entry ${type} has a bad length ${length}`)
  const data = buf.subarray(offset + 8, offset + length)
  const size = TYPES[type]
  if (size === undefined) fail(`unexpected entry type ${type}`)
  if (seen.has(type)) fail(`entry ${type} appears twice`)
  seen.add(type)
  if (!data.subarray(0, 8).equals(PNG)) fail(`entry ${type} is not a PNG`)
  // IHDR is always the first chunk: width and height at bytes 16 and 20, bit depth 24, colour type 25.
  const width = data.readUInt32BE(16)
  const height = data.readUInt32BE(20)
  if (width !== size || height !== size) fail(`entry ${type} is ${width}x${height}, expected ${size}x${size}`)
  if (data[24] !== 8 || data[25] !== 6) fail(`entry ${type} is not 8-bit RGBA`)
  const chunks: string[] = []
  for (let at = 8; at < data.length; ) {
    const n = data.readUInt32BE(at)
    chunks.push(data.subarray(at + 4, at + 8).toString("latin1"))
    at += 12 + n
  }
  if (chunks.at(-1) !== "IEND") fail(`entry ${type} does not end with IEND`)
  const extra = chunks.filter((chunk) => METADATA.has(chunk))
  if (extra.length) fail(`entry ${type} carries metadata chunks: ${extra.join(", ")}`)
  console.log(`  ${type}  ${size}x${size}  PNG  ${data.length} bytes`)
  offset += length
}

const missing = Object.keys(TYPES).filter((type) => !seen.has(type))
if (missing.length) fail(`missing entries: ${missing.join(", ")}`)
console.log("OK")
