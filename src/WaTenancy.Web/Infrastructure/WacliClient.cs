using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace WaTenancy.Web.Infrastructure;

public sealed record CliResult(int ExitCode, string Output, string Error, bool TimedOut = false);
public sealed record LifecycleEvent(string Event, JsonElement Data);
public interface ICliSession : IAsyncDisposable
{
    Task<int> Completion { get; }
    Task Stop();
}
public interface IWacliClient
{
    Task<CliResult> Run(string store, IReadOnlyList<string> args, CancellationToken ct);
    ICliSession Start(string store, IReadOnlyList<string> args, Func<LifecycleEvent, Task> onEvent, CancellationToken ct);
}
public static class CliJson
{
    public static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return root.TryGetProperty("data", out var data) ? data.Clone() : root.Clone();
    }
    public static string String(JsonElement value, params string[] names)
    {
        foreach (var name in names) if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String) return p.GetString() ?? "";
        return "";
    }
    public static bool Bool(JsonElement value, params string[] names)
    {
        foreach (var name in names) if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False) return p.GetBoolean();
        return false;
    }
    public static LifecycleEvent? Lifecycle(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var eventName = String(doc.RootElement, "event");
            if (eventName.Length == 0) return null;
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d.Clone() : JsonSerializer.SerializeToElement(new { });
            return new(eventName, data);
        }
        catch (JsonException) { return null; }
    }
}
public sealed class WacliClient(IOptions<AppOptions> options, ShortStorePaths storePaths, ILogger<WacliClient> logger) : IWacliClient
{
    private readonly AppOptions settings = options.Value;
    public static ProcessStartInfo BuildStartInfo(string executable, string store, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        info.ArgumentList.Add("--store"); info.ArgumentList.Add(store);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        // The server controls the store; inherited user settings must not override it.
        info.Environment.Remove("WACLI_STORE_DIR");
        info.Environment.Remove("WACLI_READONLY");
        return info;
    }
    private Process Launch(string store, IReadOnlyList<string> args)
    {
        Directory.CreateDirectory(store);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(store, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var info = BuildStartInfo(settings.WacliExecutable, storePaths.ForCli(store), args);
        info.Environment["WACLI_SYNC_MAX_MESSAGES"] = settings.MaxMessagesPerAccount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        info.Environment["WACLI_SYNC_MAX_DB_SIZE"] = settings.MaxDatabaseSize;
        var process = new Process { StartInfo = info };
        try { process.Start(); process.StandardInput.Close(); return process; }
        catch { process.Dispose(); throw; }
    }
    public async Task<CliResult> Run(string store, IReadOnlyList<string> args, CancellationToken ct)
    {
        using var process = Launch(store, args);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.CommandTimeoutSeconds));
        var stdout = Capture(process.StandardOutput); var stderr = Capture(process.StandardError);
        var timedOut = false;
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { timedOut = true; Kill(process); await process.WaitForExitAsync(CancellationToken.None); }
        return new(process.ExitCode, await stdout, await stderr, timedOut);
    }
    private static async Task<string> Capture(StreamReader reader)
    {
        var text = new StringBuilder(); var buffer = new char[4096]; int read;
        while ((read = await reader.ReadAsync(buffer)) > 0) if (text.Length < 1024 * 1024) text.Append(buffer, 0, Math.Min(read, 1024 * 1024 - text.Length));
        return text.ToString();
    }
    private static void Kill(Process p) { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    public ICliSession Start(string store, IReadOnlyList<string> args, Func<LifecycleEvent, Task> onEvent, CancellationToken ct) => new Session(Launch(store, args), onEvent, ct, logger);
    private sealed class Session : ICliSession
    {
        private readonly Process process;
        private readonly CancellationTokenRegistration registration;
        public Task<int> Completion { get; }
        public Session(Process process, Func<LifecycleEvent, Task> onEvent, CancellationToken ct, ILogger logger)
        {
            this.process = process; registration = ct.Register(() => Kill(process));
            Completion = Complete(onEvent, logger);
        }
        private async Task<int> Complete(Func<LifecycleEvent, Task> onEvent, ILogger logger)
        {
            // QR stdout is intentionally discarded; --events provides structured, private pairing data on stderr.
            var stdout = Capture(process.StandardOutput);
            var stderr = Consume();
            await process.WaitForExitAsync(); await stdout; await stderr; return process.ExitCode;
            async Task Consume()
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                {
                    if (line.Length > 65536) continue;
                    var evt = CliJson.Lifecycle(line); if (evt is null) continue;
                    try { await onEvent(evt); }
                    catch (Exception ex) { logger.LogError("Worker event handler failed ({ExceptionType}); event will be reconciled where supported.", ex.GetType().Name); }
                }
            }
        }
        public async Task Stop() { Kill(process); await Completion; }
        public async ValueTask DisposeAsync() { await Stop(); registration.Dispose(); process.Dispose(); }
    }
}
