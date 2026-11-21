// Command errorhunt-batch scans multiple logs with the ErrorHunt engine.
package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"os"
	"os/exec"
	"os/signal"
	"runtime"
	"sync"
)

type scan struct {
	File        string          `json:"file"`
	Diagnostics json.RawMessage `json:"diagnostics,omitempty"`
	Error       string          `json:"error,omitempty"`
}

func batch(ctx context.Context, engine string, paths []string, parallelism int) []scan {
	results := make([]scan, len(paths))
	jobs := make(chan int, len(paths))
	for index := range paths {
		jobs <- index
	}
	close(jobs)

	var workers sync.WaitGroup
	for range min(parallelism, len(paths)) {
		workers.Go(func() {
			for index := range jobs {
				file := paths[index]
				result := scan{File: file}
				command := exec.CommandContext(ctx, engine, "scan", file)
				output, err := command.Output()
				switch {
				case err != nil:
					result.Error = err.Error()
					if failure, ok := err.(*exec.ExitError); ok && len(failure.Stderr) > 0 {
						result.Error = string(failure.Stderr)
					}
				case !json.Valid(output) || output[0] != '[':
					result.Error = "engine did not emit a diagnostic array"
				default:
					result.Diagnostics = output
				}
				results[index] = result
			}
		})
	}
	workers.Wait()
	return results
}

func run(ctx context.Context, args []string, output, errors io.Writer) int {
	flags := flag.NewFlagSet("errorhunt-batch", flag.ContinueOnError)
	flags.SetOutput(errors)
	engine := flags.String("engine", "errorhunt-engine", "ErrorHunt engine executable")
	workers := flags.Int("jobs", min(runtime.NumCPU(), 4), "number of concurrent scans")
	flags.Usage = func() {
		fmt.Fprintln(errors, "Usage: errorhunt-batch [options] LOG [LOG ...]")
		flags.PrintDefaults()
	}
	if err := flags.Parse(args); err != nil {
		if err == flag.ErrHelp {
			return 0
		}
		return 2
	}
	if flags.NArg() == 0 || *workers < 1 || *workers > 32 {
		flags.Usage()
		return 2
	}
	results := batch(ctx, *engine, flags.Args(), *workers)
	if err := json.NewEncoder(output).Encode(results); err != nil {
		fmt.Fprintln(errors, err)
		return 1
	}
	for _, result := range results {
		if result.Error != "" {
			return 1
		}
	}
	return 0
}

func main() {
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt)
	defer cancel()
	os.Exit(run(ctx, os.Args[1:], os.Stdout, os.Stderr))
}
