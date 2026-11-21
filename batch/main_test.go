package main

import (
	"bytes"
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

func TestBatch(t *testing.T) {
	engine := os.Getenv("ERRORHUNT_TEST_ENGINE")
	if engine == "" {
		t.Skip("provide ERRORHUNT_TEST_ENGINE to use the Rust command")
	}
	directory := t.TempDir()
	first := filepath.Join(directory, "雪.log")
	second := filepath.Join(directory, "empty.log")
	missing := filepath.Join(directory, "missing.log")
	if err := os.WriteFile(first, []byte("main.go:4:2: undefined: name"), 0600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(second, nil, 0600); err != nil {
		t.Fatal(err)
	}
	var output, errors bytes.Buffer
	code := run(context.Background(), []string{"-engine", engine, "-jobs", "2", first, missing, second}, &output, &errors)
	if code != 1 {
		t.Fatalf("exit code %d: %s", code, errors.String())
	}
	var results []scan
	if err := json.Unmarshal(output.Bytes(), &results); err != nil {
		t.Fatal(err)
	}
	if len(results) != 3 || results[0].File != first || results[1].File != missing || results[2].File != second {
		t.Fatalf("unexpected result order: %s", output.Bytes())
	}
	if !bytes.Contains(results[0].Diagnostics, []byte("undefined: name")) ||
		results[1].Error == "" || string(results[2].Diagnostics) != "[]" {
		t.Fatalf("unexpected scan output: %s", output.Bytes())
	}
}
