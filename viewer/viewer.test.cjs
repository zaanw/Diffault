const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");

const api = vm.runInNewContext(
    fs.readFileSync("dist/viewer.js", "utf8") + "\n({parseReport, filterReport})"
);
const item = { language: "go", severity: "error", file: "雪.go", line: 5, code: null, message: "<img> café" };

test("imports a Rust/WPF report and filters by category and Unicode text", () => {
    const rows = api.parseReport({ fixed: [item], new: [], existing: [item] });
    assert.equal(rows.length, 2);
    assert.equal(api.filterReport(rows, "existing", " CAFÉ ")[0].file, "雪.go");
    assert.equal(api.filterReport(rows, "new", "CAFÉ").length, 0);
    assert.equal(rows[0].message, "<img> café");
});

test("rejects missing groups and malformed diagnostics", () => {
    assert.throws(() => api.parseReport({ fixed: [] }), /Missing new/);
    assert.throws(() => api.parseReport({ fixed: [], new: [{ ...item, line: "8" }], existing: [] }), /fields/);
});
