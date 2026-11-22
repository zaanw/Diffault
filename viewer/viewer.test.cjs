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

test("filters increases and retains compatibility with reports without counts", () => {
    const rows = api.parseReport({
        fixed: [{ ...item, count: 3 }],
        new: [{ ...item, count: 5 }],
        existing: [
            { ...item, message: "increased", count: 5, previous_count: 2 },
            { ...item, message: "decreased", count: 1, previous_count: 2 },
            { ...item, message: "equal", count: 2, previous_count: 2 },
            item,
        ]
    });
    const increased = api.filterReport(rows, "all", "", true);
    assert.equal(increased.length, 1);
    assert.equal(increased[0].message, "increased");
    assert.equal(increased[0].previousCount, 2);
    assert.equal(rows.at(-1).previousCount, null);
    assert.throws(() => api.parseReport({ fixed: [], new: [], existing: [{ ...item, count: -1 }] }),
        /invalid diagnostic count/i);
});
