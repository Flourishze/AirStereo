using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace AirStereo2;

/// <summary>
/// Pure in-process client adapter connecting the WinUI 3 Shell directly to <see cref="CoreEngineHost"/>
/// (two-process architecture: AirStereo2.exe + AirStereo2.Engine.exe).
/// </summary>
internal sealed class BridgeClient : IDisposable
{
    readonly CoreEngineHost host;
    readonly DispatcherQueue dispatcher;
    readonly Dictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    int next;
    bool disposed;

    internal JsonElement Snapshot { get; private set; }
    internal bool Ready => Snapshot.ValueKind == JsonValueKind.Object;
    internal event Action? Changed;
    internal event Action<string>? Error;
    internal event Action<JsonElement>? Dialog;

    internal BridgeClient(DispatcherQueue queue)
    {
        dispatcher = queue;
        AirStereo2.Services.LocalizationManager.Instance.ChangeLanguage(WinUiLanguage.English ? "en-US" : "zh-Hans");
        WinUiLanguage.Changed += OnWinUiLanguageChanged;
        host = new CoreEngineHost();
        host.MessageReceived += OnHostMessage;
        host.EmitInitialSnapshot();
    }

    static void OnWinUiLanguageChanged()
    {
        AirStereo2.Services.LocalizationManager.Instance.ChangeLanguage(WinUiLanguage.English ? "en-US" : "zh-Hans");
    }

    void OnHostMessage(JsonElement message)
    {
        if (disposed) return;
        var clone = message.Clone();
        dispatcher.TryEnqueue(() =>
        {
            if (disposed) return;
            string kind = clone.GetProperty("kind").GetString()!;
            if (kind == "snapshot") { Snapshot = clone; Changed?.Invoke(); }
            else if (kind == "reply" && pending.Remove(clone.GetProperty("id").GetInt32(), out var completion)) completion.TrySetResult(clone);
            else if (kind == "fatal") Error?.Invoke(clone.GetProperty("error").GetString()!);
            else if (kind.StartsWith("dialog", StringComparison.Ordinal)) Dialog?.Invoke(clone);
        });
    }

    internal async Task<JsonElement?> SendReply(string op, string target = "settings", string? name = null, object? value = null, int index = 0)
    {
        if (disposed) return null;
        int id = Interlocked.Increment(ref next);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            string line = JsonSerializer.Serialize(new { id, op, target, name, value, index });
            using var doc = JsonDocument.Parse(line);
            host.PostRequest(doc.RootElement.Clone());
            var reply = await completion.Task.WaitAsync(TimeSpan.FromMinutes(10));
            if (reply.GetProperty("ok").GetBoolean()) return reply;
            Error?.Invoke(reply.GetProperty("error").GetString()!);
        }
        catch (Exception ex) { if (!disposed) Error?.Invoke(ex.Message); }
        finally { pending.Remove(id); }
        return null;
    }

    internal async Task<bool> Send(string op, string target = "settings", string? name = null, object? value = null, int index = 0) =>
        (await SendReply(op, target, name, value, index)).HasValue;

    internal Task<bool> Set(string name, object? value, string target = "settings") => Send("set", target, name, value);
    internal Task<bool> Command(string name, string target = "settings") => Send("command", target, name);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        WinUiLanguage.Changed -= OnWinUiLanguageChanged;
        try { host.MessageReceived -= OnHostMessage; } catch { }
        try { host.Dispose(); } catch { }
        foreach (var task in pending.Values) task.TrySetCanceled();
        pending.Clear();
        Changed = null; Error = null; Dialog = null;
    }
}

internal static class JsonRead
{
    internal static JsonElement Field(this JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
    internal static string Text(this JsonElement e, string name, string fallback = "") => e.Field(name) is { ValueKind: JsonValueKind.String } v ? v.GetString() ?? fallback : fallback;
    internal static bool Flag(this JsonElement e, string name) => e.Field(name).ValueKind == JsonValueKind.True;
    internal static double Number(this JsonElement e, string name, double fallback = 0) => e.Field(name) is { ValueKind: JsonValueKind.Number } v ? v.GetDouble() : fallback;
    internal static JsonElement[] List(this JsonElement e, string name) => e.Field(name) is { ValueKind: JsonValueKind.Array } v ? v.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
}

