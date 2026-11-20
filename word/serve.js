// Serves the add-in's files to Word on https://localhost:3000, for development and personal use.
// Word only loads add-ins over HTTPS, so run "npm run certs" once first to make a local certificate.
import { readFile } from "node:fs/promises";
import { readFileSync } from "node:fs";
import { createServer } from "node:https";
import { homedir } from "node:os";
import { extname, join, normalize, sep } from "node:path";
import { fileURLToPath } from "node:url";

const PORT = 3000;
const root = fileURLToPath(new URL(".", import.meta.url));
const served = ["src", "assets"].map((folder) => join(root, folder) + sep);
const types = { ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".css": "text/css; charset=utf-8", ".png": "image/png" };

const certs = join(homedir(), ".office-addin-dev-certs");
let options;
try {
  options = { key: readFileSync(join(certs, "localhost.key")), cert: readFileSync(join(certs, "localhost.crt")) };
} catch {
  console.error(`No certificate found in ${certs}. Run "npm run certs" first.`);
  process.exit(1);
}

async function handle(request, response) {
  let path;
  try {
    path = normalize(join(root, decodeURIComponent(new URL(request.url, "https://localhost").pathname)));
  } catch {
    path = "";
  }
  // Only the add-in's own pages and icons; nothing else in the folder, and no path can climb out of it.
  if (request.method !== "GET" || !served.some((folder) => path.startsWith(folder)) || !(extname(path) in types)) {
    response.writeHead(404).end();
    return;
  }
  try {
    const body = await readFile(path);
    response.writeHead(200, { "Content-Type": types[extname(path)], "Cache-Control": "no-cache", "X-Content-Type-Options": "nosniff" }).end(body);
  } catch {
    response.writeHead(404).end();
  }
}

// Loopback only: other machines on the network can't load the add-in from this PC.
for (const host of ["127.0.0.1", "::1"]) {
  createServer(options, handle)
    .on("error", (error) => { if (host === "127.0.0.1") { console.error(`Couldn't listen on port ${PORT}: ${error.message}`); process.exit(1); } })
    .listen(PORT, host);
}
console.log(`Nogrammerly for Word is being served at https://localhost:${PORT}/src/taskpane.html`);
