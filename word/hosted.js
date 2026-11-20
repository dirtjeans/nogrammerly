// Writes manifest.hosted.xml: the manifest for an add-in whose files are served from a web address
// instead of the local server. Usage: node hosted.js https://example.github.io/nogrammerly
import { readFileSync, writeFileSync } from "node:fs";

const LOCAL = "https://localhost:3000";
let base;
try {
  base = new URL(process.argv[2]);
} catch {
  base = null;
}
if (base?.protocol !== "https:") {
  console.error("Give the HTTPS address the add-in's files are served from, for example:\n  node hosted.js https://example.github.io/nogrammerly");
  process.exit(1);
}
const address = base.href.replace(/\/+$/, "");

const manifest = readFileSync(new URL("manifest.xml", import.meta.url), "utf8")
  // An app domain is a site, without any folder.
  .replace(`<AppDomain>${LOCAL}</AppDomain>`, `<AppDomain>${base.origin}</AppDomain>`)
  .replaceAll(LOCAL, address)
  .replace(/<!--[\s\S]*?-->\r?\n/, `<!-- Made by hosted.js from manifest.xml for ${address}. Don't edit; run hosted.js again. -->\n`);
writeFileSync(new URL("manifest.hosted.xml", import.meta.url), manifest);

console.log(`Wrote manifest.hosted.xml for ${address}.`);
console.log(`Add "${base.origin}" to "origins" in %LOCALAPPDATA%\\Nogrammerly\\bridge.json, then restart Nogrammerly.`);
