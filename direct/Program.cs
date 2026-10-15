using System.Runtime.InteropServices;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var instanceId = Guid.NewGuid().ToString("N");
var startedAtUtc = DateTimeOffset.UtcNow;

app.MapGet("/", () =>
{
    var processId = Environment.ProcessId;
    var parentProcessId = ReadParentProcessId(processId);

    return Results.Ok(new
    {
        instanceId,
        startedAtUtc,
        processId,
        parentProcessId,
        processCommand = ReadCommandLine(processId),
        parentCommand = parentProcessId > 0 ? ReadCommandLine(parentProcessId) : null,
        dumpEnabled = Environment.GetEnvironmentVariable("DOTNET_DbgEnableMiniDump"),
        dumpType = Environment.GetEnvironmentVariable("DOTNET_DbgMiniDumpType"),
        dumpName = Environment.GetEnvironmentVariable("DOTNET_DbgMiniDumpName"),
        dumpDirectory = Directory.Exists("/dumps")
            ? Directory.GetFiles("/dumps").Select(Path.GetFileName).Order().ToArray()
            : []
    });
});

app.MapGet("/probe", () =>
{
    var path = $"/dumps/probe.{instanceId}.txt";
    File.WriteAllText(path, $"Written by instance {instanceId} at {DateTimeOffset.UtcNow:O}.");
    return Results.Ok(new { path, length = new FileInfo(path).Length });
});

app.MapGet("/crash", () =>
{
    Console.Error.WriteLine(
        $"Triggering native SIGSEGV from PID {Environment.ProcessId}; " +
        $"parent PID {ReadParentProcessId(Environment.ProcessId)}.");
    Console.Error.Flush();

    NativeCrash.TriggerSegmentationFault();
    return Results.Problem("The native call unexpectedly returned.");
});

app.Run();

static int ReadParentProcessId(int processId)
{
    var statusPath = $"/proc/{processId}/status";
    if (!File.Exists(statusPath))
    {
        return -1;
    }

    var parentLine = File.ReadLines(statusPath)
        .FirstOrDefault(line => line.StartsWith("PPid:", StringComparison.Ordinal));

    return parentLine is null ||
           !int.TryParse(parentLine.AsSpan("PPid:".Length).Trim(), out var parentProcessId)
        ? -1
        : parentProcessId;
}

static string? ReadCommandLine(int processId)
{
    var commandLinePath = $"/proc/{processId}/cmdline";
    if (!File.Exists(commandLinePath))
    {
        return null;
    }

    return File.ReadAllText(commandLinePath).Replace('\0', ' ').Trim();
}

internal static class NativeCrash
{
    [DllImport("nativecrash", EntryPoint = "trigger_segfault")]
    internal static extern void TriggerSegmentationFault();
}
