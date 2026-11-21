type Group = "fixed" | "new" | "existing";
type ReportRow = {
    group: Group;
    severity: string;
    language: string;
    file: string | null;
    line: number | null;
    code: string | null;
    message: string;
};

function isRecord(value: unknown): value is Record<string, unknown> {
    return typeof value === "object" && value !== null && !Array.isArray(value);
}

function parseReport(value: unknown): ReportRow[] {
    if (!isRecord(value)) throw new Error("Expected an ErrorHunt comparison report.");
    const groups: Group[] = ["fixed", "new", "existing"];
    return groups.flatMap(group => {
        const entries = value[group];
        if (!Array.isArray(entries)) throw new Error(`Missing ${group} diagnostics.`);
        return entries.map((entry: unknown) => {
            if (!isRecord(entry)) throw new Error("Invalid diagnostic.");
            const { severity, language, file, line, code, message } = entry;
            if (typeof severity !== "string" || typeof language !== "string" ||
                typeof message !== "string" || (file !== null && typeof file !== "string") ||
                (code !== null && typeof code !== "string") ||
                (line !== null && (typeof line !== "number" || !Number.isSafeInteger(line) || line < 0))) {
                throw new Error("Invalid diagnostic fields.");
            }
            return { group, severity, language, file, line, code, message };
        });
    });
}

function filterReport(rows: ReportRow[], group: string, query: string): ReportRow[] {
    const search = query.trim().toLowerCase();
    return rows.filter(row => (group === "all" || row.group === group) &&
        [row.file, row.code, row.message].some(value => value?.toLowerCase().includes(search)));
}

function initialize() {
    const picker = document.querySelector<HTMLInputElement>("#file")!;
    const query = document.querySelector<HTMLInputElement>("#query")!;
    const group = document.querySelector<HTMLSelectElement>("#group")!;
    const status = document.querySelector<HTMLElement>("#status")!;
    const report = document.querySelector<HTMLElement>("#report")!;
    const body = document.querySelector<HTMLTableSectionElement>("#rows")!;
    const more = document.querySelector<HTMLButtonElement>("#more")!;
    const pageSize = 250;
    const largestReport = 64 * 1024 * 1024;
    let rows: ReportRow[] = [];
    let shown = pageSize;
    let request = 0;

    function render() {
        const matches = filterReport(rows, group.value, query.value);
        const fragment = document.createDocumentFragment();
        for (const row of matches.slice(0, shown)) {
            const line = row.line === null ? "" : `:${row.line}`;
            const tr = document.createElement("tr");
            for (const text of [row.group, row.severity, row.language,
                row.file === null ? "—" : row.file + line,
                [row.code, row.message].filter(Boolean).join(" ")]) {
                const cell = tr.insertCell();
                cell.textContent = text;
            }
            fragment.append(tr);
        }
        body.replaceChildren(fragment);
        more.hidden = shown >= matches.length;
        status.textContent = `Showing ${Math.min(shown, matches.length)} of ${matches.length} matching diagnostics`;
    }

    picker.addEventListener("change", async () => {
        const id = ++request;
        const file = picker.files?.[0];
        report.hidden = true;
        status.textContent = "";
        if (!file) return;
        try {
            if (file.size > largestReport) throw new Error("Report exceeds 64 MiB.");
            const nextRows = parseReport(JSON.parse(await file.text()));
            if (id !== request) return;
            rows = nextRows;
            group.value = "all";
            query.value = "";
            shown = pageSize;
            report.hidden = false;
            render();
        } catch (error) {
            if (id === request) status.textContent = error instanceof Error ? error.message : "Unable to open the report.";
        }
    });

    const changeFilter = () => {
        shown = pageSize;
        render();
    };
    group.addEventListener("change", changeFilter);
    query.addEventListener("input", changeFilter);
    more.addEventListener("click", () => {
        shown += pageSize;
        render();
    });
}

if (typeof document !== "undefined") initialize();
