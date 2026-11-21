from errorhunt import parse_log, compare_logs

def test_typescript_alternate_and_dotnet():
    items = parse_log("src/app.ts:42:15 - error TS2322: Type 'string' is not assignable\nProgram.cs(3,4): error CS1002: ; expected [demo.csproj]")
    assert [(x.language, x.line, x.code) for x in items] == [("typescript", 42, "TS2322"), ("csharp", 3, "CS1002")]

def test_native_and_go():
    items = parse_log("src/main.c:9:3: error: unknown identifier\nsrc/main.cpp:4:1: warning: unused variable\n./main.go:5:8: undefined: missing")
    assert [x.language for x in items] == ["c", "cpp", "go"]

def test_python_and_rust():
    items = parse_log('Traceback (most recent call last):\n  File "app.py", line 2, in <module>\n    nope()\nNameError: name \'nope\' is not defined\nerror[E0308]: mismatched types\n --> src/main.rs:9:5')
    assert [(x.language, x.line) for x in items] == [("python", 2), ("rust", 9)]

def test_sql_php_lua_java_swift():
    items = parse_log("ERROR: relation x does not exist\nPHP Fatal error: boom in C:\\app\\index.php on line 8\nmain.lua:4: attempt to call nil\nFoo.java:12: error: cannot find symbol\nSources/app.swift:11:2: error: cannot find 'x' in scope")
    assert [x.language for x in items] == ["sql", "php", "lua", "java", "swift"]

def test_compare_tolerates_moved_lines():
    before = parse_log("a.ts(1,2): error TS2322: mismatched\nold.ts(3,1): error TS9999: old")
    after = parse_log("a.ts(7,2): error TS2322: mismatched\nnew.ts(2,1): error TS1000: new")
    result = compare_logs(before, after)
    assert {k: len(v) for k, v in result.items()} == {"fixed": 1, "new": 1, "existing": 1}

def test_duplicate_log_lines():
    items = parse_log("a.go:1:1: undefined: x\na.go:1:1: undefined: x")
    assert len(items) == 1
