# Crash and dump apps

Two small Linux container apps test whether .NET captures an unhandled native
crash and writes the resulting dump to storage that survives a process restart.
One runs .NET directly; the other runs the identical application behind the
Datadog serverless-init wrapper.

The distinction matters: a failed HTTP request or restarted container does not
prove that a useful crash dump was captured. This reproduction checks the native
crash, dump generation, process restart, and file persistence separately.

**These apps intentionally crash.** Use isolated test containers only. The HTTP
endpoints have no authentication; keep them bound to loopback and do not expose
them publicly. Dumps can contain sensitive application memory.

## Apps and layout

| Folder | Process layout | Purpose |
| --- | --- | --- |
| `direct` | `dotnet CoreDumpRepro.dll` is PID 1 | Establish the direct .NET baseline. |
| `datadog` | `datadog-init` is PID 1; .NET is its child | Check whether the wrapper changes native-crash dump behavior. |

Each folder is independently buildable and contains its own Dockerfile, .NET
project, native C++ source, and short README. Both apps use .NET 10 and run as
non-root UID 1001. The Datadog wrapper is pinned to version `1.10.3`.

## Quickstart

### Prerequisites

- Windows with an up-to-date WSL installation and `wslc`, or a Docker-compatible
  Linux container engine.
- PowerShell 7 and curl for the commands below.
- Internet access to pull .NET and Datadog base images and install the native
  compiler during the image build. A host .NET SDK or C++ compiler is not needed.

The examples use the WSL-native `wslc` CLI. With Docker, substitute `docker` for
`wslc` in the same build, run, volume, exec, start, stop, logs, and inspect commands.
Use native images for your machine's architecture rather than emulation when
evaluating crash and dump behavior.

The pinned Datadog wrapper emits an unsupported-architecture warning on ARM64.
The process-supervision test can still be exercised there, but that is not a claim
of Datadog monitoring support. Use a supported native host for monitoring
qualification.

### 1. Build both images

Run from the repository root:

```powershell
wslc build -f .\direct\Dockerfile -t crash-dump-direct:local .\direct
wslc build -f .\datadog\Dockerfile -t crash-dump-datadog:local .\datadog
```

Both Dockerfiles publish the .NET app and compile `libnativecrash.so` in a build
stage. Only the application, native library, and runtime enter the final image;
the Datadog variant additionally copies the wrapper executable.

### 2. Create persistent storage and start the apps

```powershell
wslc volume create crash-dump-direct-data
wslc volume create crash-dump-datadog-data

wslc run -d --name crash-dump-direct --memory 1G `
  -p 127.0.0.1:8080:8080 `
  --mount type=volume,source=crash-dump-direct-data,target=/dumps `
  crash-dump-direct:local

wslc run -d --name crash-dump-datadog --memory 1G `
  -p 127.0.0.1:8081:8080 `
  --mount type=volume,source=crash-dump-datadog-data,target=/dumps `
  crash-dump-datadog:local
```

Wait until each endpoint responds:

```powershell
Invoke-RestMethod http://127.0.0.1:8080/
Invoke-RestMethod http://127.0.0.1:8081/
```

Verify that the direct app reports `processId: 1`. The wrapped app should report
a different process ID and a parent command containing `datadog-init`.

### 3. Trigger a crash

Start with the direct app:

```powershell
$before = Invoke-RestMethod http://127.0.0.1:8080/
Invoke-RestMethod http://127.0.0.1:8080/probe
curl.exe --max-time 120 http://127.0.0.1:8080/crash
wslc container logs crash-dump-direct
wslc container list --all
```

On a non-Windows host, use `curl` instead of `curl.exe`.

An empty reply, connection reset, or other failed request is expected: `/crash`
terminates the application rather than returning a normal response. Wait for the
dump writer to finish and the container to stop. Do not stop or kill it yourself
while it is collecting the dump.

### 4. Restart and inspect

```powershell
wslc container start crash-dump-direct
# Wait for startup before requesting the endpoint again.
$after = Invoke-RestMethod http://127.0.0.1:8080/
$before.instanceId
$after.instanceId
$after.dumpDirectory
wslc exec crash-dump-direct ls -lh /dumps
```

The instance ID should change, while the probe file and newly generated `.dmp`
and `.dmp.crashreport.json` remain. Look for the native frame in the crash report:

```powershell
wslc exec crash-dump-direct sh -c 'grep -F trigger_segfault /dumps/*.crashreport.json'
wslc exec crash-dump-direct sh -c 'grep -F libnativecrash.so /dumps/*.crashreport.json'
```

Some ARM64 runtime builds omit these native frames from the JSON report even
though the ELF dump contains them. An empty grep result is therefore inconclusive,
not sufficient proof that capture failed. Use the native-debugger check below.

Repeat steps 3 and 4 with port `8081` and container `crash-dump-datadog`.
Only the process wrapper should differ between the two tests.

To stop the apps afterward:

```powershell
wslc container stop crash-dump-direct
wslc container stop crash-dump-datadog
```

The named volumes retain dumps after the containers stop or are removed.
Deleting a volume deletes its dumps; retain it until you have finished analysis.

## Evaluating success and failure

| Check | Success | Failure or inconclusive result |
| --- | --- | --- |
| Storage preflight | `/probe` writes a nonempty file to `/dumps`. | An HTTP error or permission failure means the storage path is not ready. |
| Crash execution | Logs show the native `SIGSEGV` path and runtime dump collection. | A healthy `/crash` response, missing library, or ordinary managed error does not establish the intended native crash. |
| Dump capture | A new, nonempty `.dmp` and matching crash-report JSON appear. | A restart alone, a probe file alone, an old dump, or a zero-byte dump is not success. |
| Native origin | The new report or a native debugger identifies `trigger_segfault` in `libnativecrash.so`. | If the JSON lacks native frames, inspect the ELF core; without either check the native-origin result remains inconclusive. |
| Persistence | After restart, the instance ID changes and the new dump and probe remain. | A missing dump after restart means capture or persistence failed. |
| Wrapper comparison | Both variants pass, with the expected different process layouts. | A direct-only pass narrows investigation to the wrapper variant; compare logs before drawing a conclusion. |

Container exit codes and failed HTTP responses are supporting observations, not
the pass criteria: a wrapper may report its child's termination differently.
Inspect container state to rule out an out-of-memory kill or external termination.
For dump usability validation, open the file with a compatible debugger and check
its threads/stacks. GDB or LLDB can inspect the native C++ fault; managed-state
analysis with `dotnet-dump` is a separate check.

### Native-debugger check

Use a disposable Linux debugging environment with GDB installed, the same
architecture, the matching image binaries, and the dump mounted read-only. The
runtime images intentionally do not include a debugger. In that environment,
replace the filename below with the actual new dump:

```sh
gdb -q -nx -batch \
  -ex 'set debuginfod enabled off' \
  -ex 'info sharedlibrary' \
  -ex 'thread apply all bt 30' \
  /usr/share/dotnet/dotnet /dumps/<new-dump-file>.dmp
```

Confirm that `/app/libnativecrash.so` is loaded with symbols and that a backtrace
contains `trigger_segfault` at `native/crash.cpp`. Do not use unrelated binaries
or a dump from an earlier run to satisfy this check.

These tests do not establish behavior for `SIGKILL`, an OOM kill, node loss, every
Datadog version, or a production telemetry configuration. The Datadog variant has
trace, APM, and log collection disabled and uses no API key.

### Observed local validation

Both images were built and exercised with WSL Containers 3.0.3.0, native Linux
ARM64, .NET runtime 10.0.13, and 1 GiB container limits on October 15, 2026.
Both wrote approximately 184 MiB ELF cores, produced valid JSON crash reports
with a crashed thread, and retained the dumps and probe files across restart.
GDB confirmed the native function and source location in both cores. The direct
container exited with 139; the wrapped container exited with 255.

In this environment the JSON reports omitted the native frames, and the Datadog
wrapper emitted the ARM64 warning described above. These results validate this
specific crash/persistence experiment, not production telemetry or every platform.

## Detailed workings

### Request and crash path

Both `Program.cs` files expose the same three endpoints:

| Endpoint | Behavior |
| --- | --- |
| `GET /` | Returns a per-process instance ID, start time, process/parent IDs and command lines, dump configuration, and filenames in `/dumps`. |
| `GET /probe` | Writes a small file using the same UID and mount as dump generation. It checks writability, not dump capture. |
| `GET /crash` | Calls the exported `trigger_segfault` function through P/Invoke. The native function writes through a null pointer and raises `SIGSEGV`. |

The native library is built with debug symbols and optimization disabled to keep
the intentional fault and native stack easy to identify. `LD_LIBRARY_PATH=/app`
lets .NET resolve the library next to the published application.

In the direct image, .NET runs as the container's main process. In the wrapped
image, the Datadog executable starts the .NET child. The .NET runtime, not the
Datadog wrapper or an HTTP handler, generates the dump when the native crash
reaches the runtime's crash handling.

### Dump settings

Both images set these defaults; override them with container environment variables
when testing another configuration:

| Variable | Default and purpose |
| --- | --- |
| `DOTNET_DbgEnableMiniDump` | `1`: enable automatic dump generation on a crash. |
| `DOTNET_DbgMiniDumpType` | `2`: collect a heap dump rather than the larger full dump. |
| `DOTNET_DbgMiniDumpName` | `/dumps/<variant>.%h.%e.%p.%t.dmp`: include hostname, executable, PID, and timestamp in the filename. |
| `DOTNET_EnableCrashReport` | `1`: generate the accompanying crash-report JSON. |
| `DOTNET_CreateDumpDiagnostics` | `1`: emit dump-collection diagnostics to the container logs. |

The filename prefixes distinguish variants; hostname, process, and time fields
reduce accidental overwrites. Dump size depends on the process's committed memory
and the selected dump type. Do not use an exact byte count as the pass criterion.

### Storage and permissions

The images create `/dumps` owned by UID/GID 1001. A mounted volume can replace
those ownership settings, so `/probe` is an important preflight check.

Use storage whose lifetime is independent of the application container. A named
volume is suitable for this local test; a shared durable filesystem serves the
same purpose in a hosted environment. The app does not mount Blob Storage or
upload dumps itself. If object storage is the final destination, copy completed
dumps there asynchronously and verify the copy before deleting the source.

Treat dumps and crash reports as sensitive data. Apply access, capacity, and
retention controls; never commit generated files to Git.

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| Image build cannot pull a base image or install `g++` | Check container-engine connectivity, proxy/DNS settings, and access to Microsoft Container Registry, Docker Hub, and the base image's package mirrors. |
| `exec format error` or an emulated process fails strangely | Build and run a native-architecture image. The .NET image, compiled library, and Datadog executable must agree. |
| `/` never responds | Inspect container logs/state, confirm the port mapping, and check whether an earlier crash left the container stopped. |
| `/probe` fails or logs report permission denied | Check mount configuration, ownership, free space, and the effective UID. Bind mounts need to be writable by UID 1001. Do not solve this by making sensitive dumps world-writable. |
| `DllNotFoundException` or missing native symbols | Build with the app folder as the build context. Confirm `/app/libnativecrash.so` exists and `LD_LIBRARY_PATH` is `/app`. |
| Crash occurs but no dump appears | Verify dump settings at `/`, examine collection diagnostics, confirm storage is writable, and rule out OOM, `SIGKILL`, disk exhaustion, or container shutdown during collection. |
| Dump collection reports permission or `ptrace` restrictions | Check the host's tracing/security policy and the runtime diagnostics. If Docker requires a tracing capability, test the minimum necessary `SYS_PTRACE` capability; do not default to privileged containers or disable all isolation. |
| Datadog variant behaves differently | Verify that the parent is the pinned `datadog-init` binary; compare child crash logs, dump settings, permissions, and memory headroom against the direct app. |
| Datadog emits API-key or metrics-forwarding warnings | The sample deliberately has no API key. These messages do not by themselves establish a dump failure; inspect the .NET crash diagnostics and generated files. Do not add production credentials just to silence them. |
| JSON report lacks the native fault frame | Inspect the ELF core with matching binaries in GDB/LLDB; ARM64 JSON frame collection can be incomplete. |
| Dump disappears after recreation | Confirm `/dumps` uses the same external volume rather than only the container writable layer or a new empty volume. |
| Old dumps make a test appear successful | Record the pre-crash directory listing and compare new filenames and instance IDs. Inspect the new report, not an older file. |
| Dump exists but analysis fails | Allow collection to finish, check for truncation or storage errors, and use debugger/runtime binaries compatible with the dump's OS, architecture, and runtime. |

The sample is intentionally a small diagnostic harness, not a production crash
collection service. Deployment configuration and platform-specific setup are
outside this repository's quickstart.
