const { createHash } = require("node:crypto");
const { readFileSync, writeFileSync } = require("node:fs");

const hash = value => createHash("sha256").update(value).digest("base64");
const script = readFileSync("dist/viewer.js", "utf8").replaceAll("\r\n", "\n");
const styles = readFileSync("viewer.css", "utf8").replaceAll("\r\n", "\n");
const html = readFileSync("index.html", "utf8")
    .replace("style-src 'self'; script-src 'self'", () =>
        `style-src 'sha256-${hash(styles)}'; script-src 'sha256-${hash(script)}'`)
    .replace('<link rel="stylesheet" href="viewer.css">', () => `<style>${styles}</style>`)
    .replace('<script src="dist/viewer.js" defer></script>', () => `<script>${script}</script>`);
writeFileSync("dist/index.html", html);
