export * as SpecTunerJsonc from "./jsonc.ts"

// A small JSONC editor for the server's own config file: it sets or removes one member at a path of object keys and
// leaves every other byte (comments, formatting, other members, trailing commas) as it was. The plugin folder may
// import only node builtins, `effect` and `@spectuner/plugin`, so the server's `jsonc-parser` is not available here.

interface Member {
  readonly key: string
  readonly keyStart: number
  readonly valueStart: number
  readonly valueEnd: number
  readonly value: Value
}

type Value =
  | { readonly kind: "object"; readonly start: number; readonly end: number; readonly members: readonly Member[] }
  | { readonly kind: "other"; readonly start: number; readonly end: number }

/** Thrown when the file is not a JSONC object; the caller refuses to touch it. */
export class JsoncError extends Error {}

/** The JSON text of the member at `path`, or undefined when there is none. Comments inside it are kept. */
export function read(text: string, path: readonly string[]) {
  if (!text.trim()) return undefined

  const member = find(document(text), path)

  return member ? text.slice(member.valueStart, member.valueEnd) : undefined
}

/**
 * The text with the member at `path` set to `value` (any JSON value), creating missing parent objects, or removed
 * when `value` is undefined. An empty text counts as `{}`.
 */
export function set(text: string, path: readonly string[], value: unknown) {
  if (!text.trim()) {
    if (value === undefined) return text

    return `${JSON.stringify(nest(path, value), null, 2)}\n`
  }

  const root = document(text)

  return value === undefined ? remove(text, root, path) : put(text, root, path, value)
}

function put(text: string, object: Value & { kind: "object" }, path: readonly string[], value: unknown): string {
  const [key, ...rest] = path
  const member = object.members.find((item) => item.key === key)

  if (member && rest.length > 0 && member.value.kind === "object") return put(text, member.value, rest, value)

  const indent = memberIndent(text, object)

  if (member) {
    const json = format(rest.length > 0 ? nest(rest, value) : value, indent)

    return text.slice(0, member.valueStart) + json + text.slice(member.valueEnd)
  }

  const entry = `${JSON.stringify(key)}: ${format(rest.length > 0 ? nest(rest, value) : value, indent)}`
  const last = object.members.at(-1)

  if (!last) {
    const closing = object.end - 1
    const outer = lineIndent(text, object.start)

    return `${text.slice(0, object.start + 1)}\n${indent}${entry}\n${outer}${text.slice(closing)}`
  }

  const comma = nextToken(text, last.valueEnd)

  // A trailing comma after the last member stays a trailing comma after the new one.
  if (text[comma] === ",") return `${text.slice(0, comma + 1)}\n${indent}${entry},${text.slice(comma + 1)}`

  return `${text.slice(0, last.valueEnd)},\n${indent}${entry}${text.slice(last.valueEnd)}`
}

function remove(text: string, object: Value & { kind: "object" }, path: readonly string[]): string {
  const [key, ...rest] = path
  const index = object.members.findIndex((item) => item.key === key)
  const member = object.members[index]

  if (!member) return text

  if (rest.length > 0) return member.value.kind === "object" ? remove(text, member.value, rest) : text

  const after = nextToken(text, member.valueEnd)

  // Take the member with its own comma and the rest of its line; else the comma before it.
  if (text[after] === ",") {
    const own = lineStart(text, member.keyStart)
    const start = text.slice(own, member.keyStart).trim() ? member.keyStart : own

    return text.slice(0, start) + text.slice(start === own ? lineEnd(text, after + 1) : nextToken(text, after + 1))
  }

  const previous = object.members[index - 1]

  if (previous) return text.slice(0, previous.valueEnd) + text.slice(member.valueEnd)

  return text.slice(0, object.start + 1) + text.slice(nextToken(text, member.valueEnd))
}

function find(object: Value, path: readonly string[]): Member | undefined {
  const [key, ...rest] = path

  if (object.kind !== "object") return undefined

  const member = object.members.find((item) => item.key === key)

  if (!member || rest.length === 0) return member

  return find(member.value, rest)
}

function nest(path: readonly string[], value: unknown): unknown {
  return path.reduceRight((inner, key) => ({ [key]: inner }), value)
}

/** JSON for a member value, indented to sit at `indent`. */
function format(value: unknown, indent: string) {
  return JSON.stringify(value, null, 2).replaceAll("\n", `\n${indent}`)
}

function memberIndent(text: string, object: Value & { kind: "object" }) {
  const first = object.members[0]

  // Members on the object's own line (`{ "a": 1 }`) give no indent to copy.
  if (!first || lineStart(text, first.keyStart) === lineStart(text, object.start))
    return `${lineIndent(text, object.start)}  `

  return lineIndent(text, first.keyStart)
}

function lineStart(text: string, index: number) {
  return text.lastIndexOf("\n", index - 1) + 1
}

function lineIndent(text: string, index: number) {
  return /^[ \t]*/.exec(text.slice(lineStart(text, index)))?.[0] ?? ""
}

/** After the end of the line at `index` when only spaces follow there, else `index`. */
function lineEnd(text: string, index: number) {
  const match = /^[ \t]*\r?\n/.exec(text.slice(index))

  return match ? index + match[0].length : index
}

/** The index of the next character that is not whitespace or a comment. */
function nextToken(text: string, index: number) {
  let at = index

  while (at < text.length) {
    if (/\s/.test(text[at]!)) {
      at++
      continue
    }

    if (text.startsWith("//", at)) {
      const end = text.indexOf("\n", at)
      at = end === -1 ? text.length : end
      continue
    }

    if (text.startsWith("/*", at)) {
      const end = text.indexOf("*/", at + 2)

      if (end === -1) throw new JsoncError("unterminated comment")
      at = end + 2
      continue
    }

    return at
  }

  return at
}

function document(text: string) {
  const start = nextToken(text, 0)
  const value = parseValue(text, start)

  if (value.kind !== "object") throw new JsoncError("the file is not a JSON object")

  if (nextToken(text, value.end) !== text.length) throw new JsoncError("unexpected text after the object")

  return value
}

function parseValue(text: string, start: number): Value {
  const char = text[start]

  if (char === "{") return parseObject(text, start)

  if (char === "[") return { kind: "other", start, end: parseArray(text, start) }

  if (char === '"') return { kind: "other", start, end: parseString(text, start) }

  const literal = /^(?:true|false|null|-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)/.exec(text.slice(start))

  if (!literal) throw new JsoncError(`unexpected character at ${start}`)

  return { kind: "other", start, end: start + literal[0].length }
}

function parseObject(text: string, start: number): Value {
  const members: Member[] = []
  let at = nextToken(text, start + 1)

  while (text[at] !== "}") {
    if (text[at] !== '"') throw new JsoncError(`expected a key at ${at}`)

    const keyEnd = parseString(text, at)
    const key: unknown = JSON.parse(text.slice(at, keyEnd))
    const colon = nextToken(text, keyEnd)

    if (text[colon] !== ":") throw new JsoncError(`expected ":" at ${colon}`)

    const valueStart = nextToken(text, colon + 1)
    const value = parseValue(text, valueStart)

    members.push({ key: String(key), keyStart: at, valueStart, valueEnd: value.end, value })
    at = nextToken(text, value.end)

    if (text[at] === ",") at = nextToken(text, at + 1)
    else if (text[at] !== "}") throw new JsoncError(`expected "," or "}" at ${at}`)
  }

  return { kind: "object", start, end: at + 1, members }
}

function parseArray(text: string, start: number) {
  let at = nextToken(text, start + 1)

  while (text[at] !== "]") {
    at = nextToken(text, parseValue(text, at).end)

    if (text[at] === ",") at = nextToken(text, at + 1)
    else if (text[at] !== "]") throw new JsoncError(`expected "," or "]" at ${at}`)
  }

  return at + 1
}

function parseString(text: string, start: number) {
  let at = start + 1

  while (at < text.length) {
    if (text[at] === "\\") at += 2
    else if (text[at] === '"') return at + 1
    else at++
  }

  throw new JsoncError("unterminated string")
}
