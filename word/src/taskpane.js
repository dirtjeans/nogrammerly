// The task pane: reads paragraphs from Word, sends them to the Nogrammerly app on this PC,
// underlines what comes back, and lists every issue with its fix. See core.js for the parts
// that don't need Word.
/* global Office, Word */
import { BridgeClient, BridgeError, changedParagraphs, collector, occurrenceIndex, parseKey, searchText, stillApplies, toCritiques } from "./core.js";

const KEY_STORE = "nogrammerly.key";
/** The on-device model takes seconds per paragraph, so its pass goes a few paragraphs at a time. */
const AI_BATCH = 4;
/** Word's search refuses longer text. */
const MAX_SEARCH = 255;

const $ = (id) => document.getElementById(id);

let client = null;
let aiReady = false;
/** Underlines need WordApi 1.8 and a Microsoft 365 subscription; without them the pane still lists every issue. */
let underlines = false;
/** Paragraph id → the text that was last checked. */
const known = new Map();
/** Paragraph id → the issues found in it. */
const found = new Map();
/** Paragraph ids in document order, for listing. */
let order = [];
/** Word annotation id → { id: paragraph id, issue }. */
const annotated = new Map();

// Work on the document runs one job at a time, so two checks never interleave their edits.
let queue = Promise.resolve();
const enqueue = (job) => (queue = queue.then(job).catch(report));

Office.onReady((info) => {
  $("connect").addEventListener("click", () => saveKey($("key").value));
  $("key").addEventListener("keydown", (e) => { if (e.key === "Enter") saveKey($("key").value); });
  $("change-key").addEventListener("click", () => show("setup"));
  $("recheck").addEventListener("click", () => enqueue(() => checkDocument(true)));
  $("rewrite").addEventListener("click", () => enqueue(rewriteSelection));
  $("retry").addEventListener("click", () => enqueue(connect));
  if (info.host !== Office.HostType.Word) return say("Open this add-in in Word.");
  underlines = Office.context.requirements.isSetSupported("WordApi", "1.8");
  let saved = null;
  try { saved = localStorage.getItem(KEY_STORE); } catch { /* Storage is blocked; ask for the key each time. */ }
  if (parseKey(saved)) { client = new BridgeClient(parseKey(saved)); enqueue(connect); }
  else show("setup");
});

function saveKey(text) {
  const key = parseKey(text);
  if (!key) return say("That doesn't look like a Nogrammerly key. In the ✦ menu, choose Add-ins for other apps › Copy add-in key.");
  try { localStorage.setItem(KEY_STORE, text.trim()); } catch { /* Works for this session only. */ }
  $("key").value = "";
  client = new BridgeClient(key);
  enqueue(connect);
}

let listening = false;

async function connect() {
  say("");
  const status = await client.status();
  aiReady = status.ai === "Ready";
  $("ai").textContent = aiReady ? "On-device AI is on" : "Spelling and style rules (on-device AI is off)";
  show("main");
  if (!listening) {
    await Word.run(async (context) => {
      const later = collector((ids) => enqueue(() => checkIds(ids)), 800);
      context.document.onParagraphAdded.add(async (e) => later(e.uniqueLocalIds));
      context.document.onParagraphChanged.add(async (e) => later(e.uniqueLocalIds));
      context.document.onParagraphDeleted.add(async (e) => { for (const id of e.uniqueLocalIds) forget(id); render(); });
      if (underlines) {
        context.document.onAnnotationPopupAction.add(async (e) => enqueue(() => popupAction(e)));
        context.document.onAnnotationClicked.add(async (e) => highlight(annotated.get(e.id)));
      }
      await context.sync();
    });
    listening = true;
  }
  await checkDocument(true);
}

// Checking.

async function checkDocument(everything) {
  const current = await Word.run(async (context) => {
    const paragraphs = context.document.body.paragraphs;
    paragraphs.load("items/text,items/uniqueLocalId");
    await context.sync();
    return paragraphs.items.map((p) => ({ id: p.uniqueLocalId, text: p.text }));
  });
  order = current.map((p) => p.id);
  if (everything) known.clear();
  const present = new Set(order);
  for (const id of [...found.keys()]) if (!present.has(id)) forget(id);
  await check(changedParagraphs(known, current));
}

async function checkIds(ids) {
  // An edit can add or move paragraphs, so refresh the order as well as the changed text.
  for (const id of ids) known.delete(id);
  await checkDocument(false);
}

async function check(paragraphs) {
  paragraphs = paragraphs.filter((p) => p.text.trim() !== "" || found.has(p.id));
  if (paragraphs.length === 0) return render();
  busy(`Checking ${paragraphs.length} paragraph${paragraphs.length === 1 ? "" : "s"}…`);
  // A quick pass with spelling and style rules first, so underlines appear at once.
  await apply(paragraphs, await client.check(paragraphs.map((p) => p.text), false));
  if (!aiReady) return busy("");
  // Then the on-device model. Each batch is its own job, so new edits don't wait behind a long document.
  for (let i = 0; i < paragraphs.length; i += AI_BATCH) {
    const batch = paragraphs.slice(i, i + AI_BATCH);
    const last = i + AI_BATCH >= paragraphs.length;
    enqueue(async () => {
      busy(`AI check: ${Math.min(i + AI_BATCH, paragraphs.length)} of ${paragraphs.length}…`);
      const fresh = batch.filter((p) => known.get(p.id) === p.text);
      if (fresh.length > 0) await apply(fresh, await client.check(fresh.map((p) => p.text), true));
      if (last) busy("");
    });
  }
}

/** Shows the issues for each paragraph, unless it changed while the check was running. */
async function apply(paragraphs, results) {
  try {
    await Word.run((context) => applyIn(context, paragraphs, results));
  } catch (error) {
    if (paragraphs.length > 1) {
      // One paragraph that has since been deleted fails the whole batch; do the rest one by one.
      for (let i = 0; i < paragraphs.length; i++) await apply([paragraphs[i]], [results[i]]);
    } else if (underlines && error?.code !== "ItemNotFound") {
      // Most likely no Microsoft 365 subscription: carry on with the list alone.
      underlines = false;
      $("no-underlines").hidden = false;
      await apply(paragraphs, results);
    } else {
      forget(paragraphs[0].id);
    }
  }
  render();
}

async function applyIn(context, paragraphs, results) {
  const handles = paragraphs.map((p) => {
    const paragraph = context.document.getParagraphByUniqueLocalId(p.id);
    paragraph.load("text");
    const old = underlines ? paragraph.getAnnotations() : null;
    old?.load("items/id");
    return { paragraph, old };
  });
  await context.sync();
  const inserted = [];
  handles.forEach(({ paragraph, old }, i) => {
    const { id, text } = paragraphs[i];
    if (paragraph.text !== text) return; // Edited meanwhile; the change event will check it again.
    const { kept, critiques } = toCritiques(results[i], text);
    for (const annotation of old?.items ?? []) { annotated.delete(annotation.id); annotation.delete(); }
    const ids = underlines && critiques.length > 0 ? paragraph.insertAnnotations({ critiques }) : null;
    inserted.push({ id, text, kept, ids });
  });
  await context.sync();
  for (const { id, text, kept, ids } of inserted) {
    known.set(id, text);
    found.set(id, kept);
    kept.forEach((issue, i) => { if (ids?.value[i]) annotated.set(ids.value[i], { id, issue }); });
  }
}

function forget(id) {
  known.delete(id);
  found.delete(id);
  for (const [annotation, entry] of annotated) if (entry.id === id) annotated.delete(annotation);
}

// Acting on an issue.

/** The issue's text in the document, or null if it has changed or Word can't find it. */
async function locate(context, id, issue) {
  const paragraph = context.document.getParagraphByUniqueLocalId(id);
  paragraph.load("text");
  await context.sync();
  if (!stillApplies(issue, paragraph.text) || issue.original.length > MAX_SEARCH) return null;
  const matches = paragraph.search(searchText(issue.original), { matchCase: true });
  matches.load("items");
  await context.sync();
  return matches.items[occurrenceIndex(issue, paragraph.text)] ?? null;
}

async function fix(id, issue, quiet = false) {
  const done = await Word.run(async (context) => {
    const range = await locate(context, id, issue);
    if (!range) return false;
    range.insertText(issue.replacement, "Replace");
    await context.sync();
    return true;
  });
  if (!done && !quiet) say("The text changed, so that fix was skipped.");
  await checkIds([id]);
}

async function reveal(id, issue) {
  await Word.run(async (context) => {
    const range = await locate(context, id, issue) ?? context.document.getParagraphByUniqueLocalId(id).getRange("Whole");
    range.select();
    await context.sync();
  });
}

async function ignore(issue) {
  await client.ignore(issue.ignoreKey);
  // The app now leaves this issue out everywhere, so check again wherever it was showing.
  const ids = [...found].filter(([, issues]) => issues.some((i) => i.ignoreKey === issue.ignoreKey)).map(([id]) => id);
  await checkIds(ids);
}

async function popupAction(event) {
  const entry = annotated.get(event.id);
  if (!entry) return;
  annotated.delete(event.id);
  if (event.action === "Accept") {
    // Make the change ourselves if Word hasn't already made it.
    await fix(entry.id, entry.issue, true);
  } else {
    found.set(entry.id, (found.get(entry.id) ?? []).filter((i) => i !== entry.issue));
    render();
  }
}

// Rewriting.

async function rewriteSelection() {
  const original = await Word.run(async (context) => {
    const selection = context.document.getSelection();
    selection.load("text");
    await context.sync();
    return selection.text;
  });
  if (original.trim() === "") return say("Select some text to rewrite first.");
  busy("Rewriting…");
  const result = await client.rewrite(original, $("tone").value).finally(() => busy(""));
  $("preview-text").textContent = result.text;
  $("preview-note").textContent = result.ai ? "" : "Rewritten with built-in rules, because on-device AI is off.";
  $("preview").hidden = false;
  $("preview-cancel").onclick = () => { $("preview").hidden = true; };
  $("preview-replace").onclick = () => enqueue(async () => {
    $("preview").hidden = true;
    const replaced = await Word.run(async (context) => {
      const selection = context.document.getSelection();
      selection.load("text");
      await context.sync();
      if (selection.text !== original) return false;
      selection.insertText(result.text, "Replace");
      await context.sync();
      return true;
    });
    if (!replaced) say("The selection changed, so the rewrite was not applied.");
  });
}

// The pane.

function render() {
  const list = $("issues");
  list.replaceChildren();
  let count = 0;
  for (const id of order) {
    for (const issue of found.get(id) ?? []) {
      count++;
      list.append(card(id, issue));
    }
  }
  $("count").textContent = count === 0 ? "No issues found" : `${count} suggestion${count === 1 ? "" : "s"}`;
}

function card(id, issue) {
  const item = element("li", `issue ${issue.kind}`);
  item.dataset.key = `${id}|${issue.start}|${issue.ignoreKey}`;
  item.append(element("div", "title", issue.title));
  const change = element("div", "change");
  // Spaces on their own can't be seen, so an issue that is only spaces shows them as dots.
  const visible = (text) => (text.trim() === "" ? text.replaceAll(" ", "·") : text);
  change.append(element("span", "original", visible(issue.original.length > 80 ? issue.original.slice(0, 80) + "…" : issue.original)));
  if (issue.replacement !== null) change.append(" → ", element("span", "replacement", issue.replacement === "" ? "(remove)" : visible(issue.replacement)));
  item.append(change, element("div", "explanation", issue.explanation));
  const actions = element("div", "actions");
  if (issue.replacement !== null) actions.append(button("Fix", () => fix(id, issue), "primary"));
  actions.append(button("Show", () => reveal(id, issue)), button("Ignore", () => ignore(issue)));
  item.append(actions);
  return item;
}

function highlight(entry) {
  if (!entry) return;
  const key = `${entry.id}|${entry.issue.start}|${entry.issue.ignoreKey}`;
  for (const item of $("issues").children) {
    const match = item.dataset.key === key;
    item.classList.toggle("current", match);
    if (match) item.scrollIntoView({ block: "nearest" });
  }
}

function element(tag, className, text) {
  const node = document.createElement(tag);
  node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

function button(label, action, className = "") {
  const node = element("button", className, label);
  node.type = "button";
  node.addEventListener("click", () => enqueue(action));
  return node;
}

function show(view) {
  for (const name of ["setup", "main"]) $(name).hidden = name !== view;
}

function busy(text) {
  $("busy").textContent = text;
}

function say(text) {
  $("message").textContent = text;
  $("message").hidden = text === "";
  $("retry").hidden = true;
}

function report(error) {
  busy("");
  if (error instanceof BridgeError) {
    say(error.message);
    if (error.reason === "key") show("setup");
    $("retry").hidden = error.reason !== "offline";
  } else {
    console.error(error);
    say("Something went wrong in Word. Choose Check again to retry.");
  }
}
