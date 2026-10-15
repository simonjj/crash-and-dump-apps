# Datadog-wrapped crash-dump app

Runs the same .NET 10/native crash harness as `direct`, with
`datadog/serverless-init:1.10.3` as PID 1 and .NET as its child. `GET /crash`
deliberately raises a native `SIGSEGV`; the child runtime writes a heap dump and
crash report to `/dumps`.

`GET /` reports the child/parent commands, dump settings, and dump files.
`GET /probe` checks that `/dumps` is writable.

No Datadog API key is needed. Trace, APM, and log collection are disabled: this is
a process-supervision comparison, not a Datadog telemetry integration test.
See the repository README for setup and the pinned wrapper's ARM64 limitation.
