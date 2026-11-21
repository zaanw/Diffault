CREATE TABLE IF NOT EXISTS analyses (
    id INTEGER PRIMARY KEY,
    analyzed_at TEXT NOT NULL,
    label TEXT NOT NULL,
    report_json TEXT NOT NULL
);
