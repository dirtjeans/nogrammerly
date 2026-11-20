// Logic that doesn't need Word: talking to the Nogrammerly bridge and turning its issues into
// Word annotations. Kept apart from taskpane.js so it can be tested with plain Node (npm test).

/** The bridge refuses more paragraphs than this in one request. */
export const MAX_PARAGRAPHS = 200;
/** Stay well under the bridge's 1 MB body limit; JSON escaping and UTF-8 can double a string's size. */
const MAX_CHARS_PER_REQUEST = 200_000;

export const KINDS = ["correctness", "clarity", "delivery", "style"];

/** Splits the key copied from the Nogrammerly tray menu ("port.token"). Returns null if it isn't one. */
export function parseKey(key) {
  const match = /^\s*(\d{4,5})\.([0-9a-f]{32,})\s*$/i.exec(key ?? "");
  if (!match) return null;
  const port = Number(match[1]);
  return port > 1023 && port < 65536 ? { port, token: match[2] } : null;
}

/** Why a bridge call failed: "offline", "key", "origin" or "error". */
export class BridgeError extends Error {
  constructor(reason, message) {
    super(message);
    this.reason = reason;
  }
}

export const BRIDGE_MESSAGES = {
  offline: "Can't reach Nogrammerly. Check that it is running, and that add-ins are turned on in its ✦ menu.",
  key: "Nogrammerly didn't accept the key. Copy it again from the ✦ menu.",
  origin: "Nogrammerly doesn't allow this add-in's address. Add it to \"origins\" in bridge.json.",
  error: "Nogrammerly couldn't handle that request.",
};

/** Calls the bridge the tray app serves on this PC. Text is only ever sent to 127.0.0.1. */
export class BridgeClient {
  constructor({ port, token }, fetchImpl = globalThis.fetch.bind(globalThis)) {
    this.base = `http://127.0.0.1:${port}`;
    this.token = token;
    this.fetch = fetchImpl;
  }

  status() {
    return this.#call("GET", "/status");
  }

  /** One list of issues per paragraph, in order. Splits large documents over several requests. */
  async check(paragraphs, ai = true) {
    const results = [];
    for (const batch of batches(paragraphs)) {
      const reply = await this.#call("POST", "/check", { paragraphs: batch, ai });
      if (!Array.isArray(reply.paragraphs) || reply.paragraphs.length !== batch.length)
        throw new BridgeError("error", BRIDGE_MESSAGES.error);
      results.push(...reply.paragraphs);
    }
    return results;
  }

  rewrite(text, tone) {
    return this.#call("POST", "/rewrite", { text, tone });
  }

  ignore(ignoreKey) {
    return this.#call("POST", "/ignore", { ignoreKey });
  }

  async #call(method, path, body) {
    let response;
    try {
      response = await this.fetch(this.base + path, {
        method,
        headers: { Authorization: `Bearer ${this.token}`, ...(body ? { "Content-Type": "application/json" } : {}) },
        body: body ? JSON.stringify(body) : undefined,
      });
    } catch {
      throw new BridgeError("offline", BRIDGE_MESSAGES.offline);
    }
    if (response.status === 401) throw new BridgeError("key", BRIDGE_MESSAGES.key);
    if (response.status === 403) throw new BridgeError("origin", BRIDGE_MESSAGES.origin);
    let reply = null;
    try {
      reply = await response.json();
    } catch {
      // Handled below: a reply that isn't JSON is an error whatever its status.
    }
    if (!response.ok || reply === null) throw new BridgeError("error", reply?.error ?? BRIDGE_MESSAGES.error);
    return reply;
  }
}

/** Groups paragraphs so each request stays inside the bridge's limits. */
export function* batches(paragraphs) {
  let batch = [];
  let chars = 0;
  for (const paragraph of paragraphs) {
    if (batch.length > 0 && (batch.length === MAX_PARAGRAPHS || chars + paragraph.length > MAX_CHARS_PER_REQUEST)) {
      yield batch;
      batch = [];
      chars = 0;
    }
    batch.push(paragraph);
    chars += paragraph.length;
  }
  if (batch.length > 0) yield batch;
}

/**
 * Which paragraphs need checking: those that are new or whose text changed since `known` (a Map of
 * paragraph id to the text last checked). Also drops paragraphs that left the document from `known`.
 */
export function changedParagraphs(known, current) {
  const present = new Set(current.map((p) => p.id));
  for (const id of [...known.keys()]) if (!present.has(id)) known.delete(id);
  return current.filter((p) => known.get(p.id) !== p.text);
}

/** True while the paragraph still holds the text the issue was found in, at the same place. */
export function stillApplies(issue, text) {
  return issue.start >= 0 && issue.length > 0 && text.substr(issue.start, issue.length) === issue.original
    && issue.start + issue.length <= text.length;
}

/**
 * Word finds text by searching, not by position. This is how many times the issue's text appears in the
 * paragraph before the issue itself, so the right match can be picked from the search results.
 */
export function occurrenceIndex(issue, text) {
  let count = 0;
  for (let at = text.indexOf(issue.original); at !== -1 && at < issue.start; at = text.indexOf(issue.original, at + 1)) count++;
  return count;
}

/** Word's search reads "^" as the start of a special code such as "^p"; "^^" means the character itself. */
export function searchText(text) {
  return text.replaceAll("^", "^^");
}

// Word offers five underline colours. These are the closest to the tray app's red, blue, purple and orange.
const COLOURS = { correctness: "Red", clarity: "Blue", delivery: "Lavender", style: "Berry" };

/**
 * Turns one paragraph's issues into Word critiques. Issues that no longer fit the text, or that overlap one
 * already kept, are left out. Returns the kept issues alongside, in the same order as the critiques.
 * A critique's pop-up title comes from the manifest, so there is one per kind; see manifest.xml.
 */
export function toCritiques(issues, text) {
  const kept = [];
  const critiques = [];
  let end = 0;
  for (const issue of [...issues].sort((a, b) => a.start - b.start)) {
    if (!stillApplies(issue, text) || issue.start < end) continue;
    end = issue.start + issue.length;
    const kind = KINDS.includes(issue.kind) ? issue.kind : "correctness";
    const critique = { colorScheme: COLOURS[kind], start: issue.start, length: issue.length };
    // A pop-up needs text to offer. Issues with no fix, or whose fix is to delete, are handled in the task pane.
    if (issue.replacement) {
      critique.popupOptions = {
        brandingTextResourceId: "Nog.Brand",
        titleResourceId: `Nog.${kind}.Title`,
        subtitleResourceId: `Nog.${kind}.Subtitle`,
        suggestions: [issue.replacement],
      };
    }
    kept.push(issue);
    critiques.push(critique);
  }
  return { kept, critiques };
}

/** Runs `action` once things have been quiet for `wait` ms, with everything added in the meantime. */
export function collector(action, wait, timers = globalThis) {
  const pending = new Set();
  let timer = null;
  return (items) => {
    for (const item of items) pending.add(item);
    if (timer !== null) timers.clearTimeout(timer);
    timer = timers.setTimeout(() => {
      timer = null;
      const batch = [...pending];
      pending.clear();
      action(batch);
    }, wait);
  };
}
