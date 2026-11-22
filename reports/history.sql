CREATE TABLE IF NOT EXISTS analyses (
    id INTEGER PRIMARY KEY,
    analyzed_at TEXT NOT NULL,
    label TEXT NOT NULL,
    report_json TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS analysis_details (
    analysis_id INTEGER PRIMARY KEY REFERENCES analyses(id),
    kind TEXT NOT NULL,
    diagnostic_count INTEGER NOT NULL
);
