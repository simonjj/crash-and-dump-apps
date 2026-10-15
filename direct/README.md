# Direct .NET crash-dump app

Runs a .NET 10 web app directly as PID 1. `GET /crash` calls a native C++ library
through P/Invoke and deliberately raises `SIGSEGV`. The .NET runtime writes a heap
dump and crash report to `/dumps` before the process exits.

`GET /` reports process IDs, dump settings, and files in `/dumps`. `GET /probe`
checks that the directory is writable.

The Dockerfile compiles both the app and native library and runs them as UID 1001.
See the repository README for build, testing, persistence, and troubleshooting.
