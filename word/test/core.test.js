import assert from "node:assert/strict";
import { test } from "node:test";
import { BridgeClient, BridgeError, MAX_PARAGRAPHS, batches, changedParagraphs, collector, hostKey, occurrenceIndex, parseKey, searchText, stillApplies, toCritiques } from "../src/core.js";

const token = "ab".repeat(32);
const issue = (start, original, replacement, kind = "correctness") => ({ start, length: original.length, original, replacement, kind });

test("parseKey reads the key from the tray menu and refuses anything else", () => {
  assert.deepEqual(parseKey(` 47613.${token}\n`), { port: 47613, token });
  for (const bad of ["", null, token, `80.${token}`, `99999.${token}`, "47613.short", `47613.${token}!`]) assert.equal(parseKey(bad), null);
});

test("hostKey takes a key from the app's own site and nothing else", async () => {
  const key = `47613.${token}`;
  assert.equal(await hostKey(async (url) => (assert.equal(url, "/key.json"), Response.json({ key }))), key);
  assert.equal(await hostKey(async () => new Response("<html>", { status: 404 })), null);
  assert.equal(await hostKey(async () => new Response("<html>not json</html>")), null);
  assert.equal(await hostKey(async () => Response.json({ key: "nope" })), null);
  assert.equal(await hostKey(async () => { throw new TypeError("Failed to fetch"); }), null);
});

test("the client sends the key and paragraphs to 127.0.0.1 only", async () => {
  const calls = [];
  const client = new BridgeClient({ port: 47613, token }, async (url, options) => {
    calls.push({ url, options });
    return Response.json({ paragraphs: [[], [issue(3, "utilize", "use", "clarity")]] });
  });
  const result = await client.check(["Fine.", "We utilize it."], false);
  assert.equal(result[1][0].replacement, "use");
  assert.equal(calls[0].url, "http://127.0.0.1:47613/check");
  assert.equal(calls[0].options.headers.Authorization, `Bearer ${token}`);
  assert.deepEqual(JSON.parse(calls[0].options.body), { paragraphs: ["Fine.", "We utilize it."], ai: false });
});

test("the client says why a call failed", async () => {
  const failing = (reply) => new BridgeClient({ port: 1, token }, reply).status();
  const reason = async (reply) => (await assert.rejects(failing(reply), BridgeError), failing(reply).catch((e) => e.reason));
  assert.equal(await reason(async () => { throw new TypeError("Failed to fetch"); }), "offline");
  assert.equal(await reason(async () => Response.json({ error: "x" }, { status: 401 })), "key");
  assert.equal(await reason(async () => Response.json({ error: "x" }, { status: 403 })), "origin");
  assert.equal(await reason(async () => new Response("oops", { status: 500 })), "error");
  assert.equal(await reason(async () => new Response("not json")), "error");
});

test("a reply with the wrong number of paragraphs is refused", async () => {
  const client = new BridgeClient({ port: 1, token }, async () => Response.json({ paragraphs: [[]] }));
  await assert.rejects(client.check(["a", "b"]), BridgeError);
});

test("large documents are split over several requests and stay in order", async () => {
  const paragraphs = Array.from({ length: MAX_PARAGRAPHS * 2 + 5 }, (_, i) => `p${i}`);
  assert.deepEqual([...batches(paragraphs)].map((b) => b.length), [MAX_PARAGRAPHS, MAX_PARAGRAPHS, 5]);
  assert.deepEqual([...batches(["a".repeat(150_000), "b".repeat(150_000), "c"])].map((b) => b.length), [1, 2]);
  const client = new BridgeClient({ port: 1, token }, async (_, options) =>
    Response.json({ paragraphs: JSON.parse(options.body).paragraphs.map((text) => [{ original: text }]) }));
  assert.deepEqual((await client.check(paragraphs)).map((issues) => issues[0].original), paragraphs);
});

test("only new and edited paragraphs are checked, and removed ones are forgotten", () => {
  const known = new Map([["a", "Same."], ["b", "Old."], ["gone", "Bye."]]);
  const changed = changedParagraphs(known, [{ id: "a", text: "Same." }, { id: "b", text: "New." }, { id: "c", text: "Added." }]);
  assert.deepEqual(changed.map((p) => p.id), ["b", "c"]);
  assert.deepEqual([...known.keys()], ["a", "b"]);
});

test("an issue only applies while its text is still in place", () => {
  assert.ok(stillApplies(issue(3, "utilize", "use"), "We utilize it."));
  assert.ok(!stillApplies(issue(3, "utilize", "use"), "We do utilize it."));
  assert.ok(!stillApplies(issue(12, "it.", "x"), "We utilize"));
  assert.ok(!stillApplies({ ...issue(0, "", "x") }, "We"));
});

test("occurrenceIndex picks the right match among repeats", () => {
  const text = "teh cat and teh dog and teh end";
  assert.deepEqual([0, 12, 24].map((start) => occurrenceIndex(issue(start, "teh", "the"), text)), [0, 1, 2]);
});

test("critiques carry colour, position and a pop-up only when there is a fix", () => {
  const text = "We utilize it very well.";
  const { kept, critiques } = toCritiques([issue(14, "very", null, "style"), issue(3, "utilize", "use", "clarity")], text);
  assert.deepEqual(kept.map((i) => i.original), ["utilize", "very"]);
  assert.deepEqual(critiques[0], {
    colorScheme: "Blue", start: 3, length: 7,
    popupOptions: { brandingTextResourceId: "Nog.Brand", titleResourceId: "Nog.clarity.Title", subtitleResourceId: "Nog.clarity.Subtitle", suggestions: ["use"] },
  });
  assert.deepEqual(critiques[1], { colorScheme: "Berry", start: 14, length: 4 });
});

test("critiques leave out stale and overlapping issues, and removals get no pop-up", () => {
  const text = "the the cat";
  const { kept, critiques } = toCritiques([issue(4, "the ", ""), issue(4, "the", "a"), issue(8, "dog", "cat"), issue(0, "the", "The", "odd")], text);
  assert.deepEqual(kept.map((i) => i.start), [0, 4]);
  assert.equal(critiques[0].colorScheme, "Red");
  assert.equal(critiques[1].popupOptions, undefined);
});

test("searchText escapes Word's special character", () => assert.equal(searchText("2^3 and ^p"), "2^^3 and ^^p"));

test("collector waits for quiet, then hands over everything once", () => {
  const timers = { next: 1, live: new Map(), setTimeout(fn) { this.live.set(this.next, fn); return this.next++; }, clearTimeout(id) { this.live.delete(id); } };
  const seen = [];
  const add = collector((batch) => seen.push(batch), 500, timers);
  add(["a"]); add(["b", "a"]);
  assert.equal(timers.live.size, 1);
  [...timers.live.values()][0]();
  assert.deepEqual(seen, [["a", "b"]]);
});
