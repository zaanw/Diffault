use errorhunt_engine::{compare, decode, parse};

#[test]
fn recognizes_each_supported_language() {
    let fixtures = [
        (
            "csharp",
            r"C:\Проект\Main.cs(3,4): error CS1002: ; expected [demo.csproj]",
        ),
        (
            "typescript",
            "src/app.ts:42:15 - error TS2322: Type mismatch",
        ),
        ("c", "src/main.c:9:3: error: unknown identifier"),
        ("cpp", "src/main.cpp:4:1: warning: unused variable"),
        ("go", "./main.go:5:8: undefined: missing"),
        (
            "java",
            "[ERROR] /tmp/File.java:12: error: cannot find symbol",
        ),
        (
            "php",
            r"PHP Fatal error: boom in C:\app\index.php on line 8",
        ),
        ("lua", "lua: main.lua:4: attempt to call nil"),
        (
            "swift",
            "Sources/app.swift:11:2: error: cannot find 'x' in scope",
        ),
        ("sql", "ERROR: relation x does not exist"),
        (
            "rust",
            "error[E0308]: mismatched types\n --> src/main.rs:9:5",
        ),
        (
            "python",
            "Traceback (most recent call last):\n  File \"a.py\", line 3\n  File \"b.py\", line 8\n    f()\nNameError: name 'f' is not defined",
        ),
    ];
    for (language, input) in fixtures {
        let diagnostics = parse(input);
        assert_eq!(diagnostics.len(), 1, "{language}");
        assert_eq!(diagnostics[0].language, language);
        assert!(
            diagnostics[0]
                .original
                .lines()
                .all(|line| input.contains(line))
        );
    }
    let python = parse(fixtures[11].1);
    assert_eq!(python[0].file.as_deref(), Some("b.py"));
    assert_eq!(python[0].line, Some(8));
    let sql = parse(fixtures[9].1);
    assert_eq!((sql[0].file.as_deref(), sql[0].line), (None, None));
}

#[test]
fn compares_groups_and_preserves_after_location() {
    let before = parse(
        "C:/Repo/Main.cs(2,1): error CS1002: missing  token\nC:/Repo/Main.cs(3,1): error CS1002: missing token\nold.go:2: undefined: old",
    );
    let after = parse(r"c:\repo\main.cs(8,9): error CS1002: missing token");
    let after = [after, parse("new.go:2: undefined: new")].concat();
    assert_eq!(before[0].count, 2);
    let result = compare(before, after);
    assert_eq!(result.fixed[0].file.as_deref(), Some("old.go"));
    assert_eq!(result.new[0].file.as_deref(), Some("new.go"));
    assert_eq!(result.existing[0].line, Some(8));
    assert_eq!(result.existing[0].count, 1);
    assert_eq!(result.existing[0].previous_count, Some(2));
}

#[test]
fn compares_repeat_counts_without_changing_diagnostic_categories() {
    let before = parse("a.go:5: broken\nb.go:1: stable\nold.go:2: fixed\n");
    let after =
        parse("a.go:8: broken\na.go:9: broken\na.go:11: broken\nb.go:3: stable\nnew.go:2: new\n");
    let result = compare(before, after);
    assert_eq!(result.fixed[0].previous_count, None);
    assert_eq!(result.new[0].previous_count, None);
    assert_eq!(
        result
            .existing
            .iter()
            .map(|item| (item.previous_count, item.count))
            .collect::<Vec<_>>(),
        vec![(Some(1), 3), (Some(1), 1)]
    );
}

#[test]
fn handles_boms_empty_logs_and_corrupt_input() {
    assert!(parse("").is_empty());
    assert!(parse(&decode(&[0xff, 0x00, 0xfc])).is_empty());
    assert_eq!(decode(&[0xff, 0xfe, 0x41]), "�");
    let text = "C:/é/雪.go:2: undefined: café";
    for (bom, endian) in [
        ([0xff, 0xfe], u16::to_le_bytes as fn(u16) -> [u8; 2]),
        ([0xfe, 0xff], u16::to_be_bytes),
    ] {
        let bytes: Vec<_> = bom
            .into_iter()
            .chain(text.encode_utf16().flat_map(endian))
            .collect();
        assert_eq!(
            parse(&decode(&bytes))[0].file.as_deref(),
            Some("C:/é/雪.go")
        );
    }
    assert_eq!(
        decode(&[&[0xef, 0xbb, 0xbf], text.as_bytes()].concat()),
        text
    );
    assert_eq!(parse("a.go:9999999999999999999: bad")[0].line, None);
}

#[test]
fn leaves_renames_and_changed_messages_unmatched() {
    let before = parse("old.go:1: undefined: name\napp.go:4: undefined: before");
    let after = parse("renamed.go:9: undefined: name\napp.go:4: undefined: after");
    let result = compare(before, after);
    assert_eq!(result.fixed.len(), 2);
    assert_eq!(result.new.len(), 2);
    assert!(result.existing.is_empty());
}
