using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ApexPing;

public sealed record InputEvent(string Type, int X = 0, int Y = 0, bool Enabled = true, string? Message = null);

public sealed class InputBridge : IDisposable
{
    private readonly Dispatcher dispatcher;
    private Process? process;
    private bool closing;
    public event Action<InputEvent>? Received;
    public bool Ready { get; private set; }

    public InputBridge(Dispatcher dispatcher) => this.dispatcher = dispatcher;

    public void Start()
    {
        string helper = Path.Combine(AppContext.BaseDirectory, "hook-helper.exe");
        if (!File.Exists(helper)) throw new FileNotFoundException("缺少输入组件，请先运行 build.ps1。", helper);
        process = new Process { StartInfo = new ProcessStartInfo(helper)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        } };
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => Post(new("error", Message: "输入组件已停止，请退出后重新打开 ApexPing。"));
        if (!process.Start()) throw new IOException("无法启动输入组件。 ");
        _ = ReadEvents(process);
        _ = ReadErrors(process);
    }

    public void Configure(UserSettings settings) => Send(new
    {
        type = "config", trigger = settings.Trigger, enabled = settings.Enabled,
        clickPing = false, emoteTrigger = "off", gameCheck = settings.PauseFullscreen,
        dragThresholdPx = 8, toggleMods = 3, toggleVk = 0x50
    });

    private void Send(object command)
    {
        if (closing || process == null) return;
        try { process.StandardInput.WriteLine(JsonSerializer.Serialize(command)); process.StandardInput.Flush(); }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
        { Post(new("error", Message: "输入组件通信失败：" + e.Message)); }
    }

    private async Task ReadEvents(Process child)
    {
        try
        {
            while (await child.StandardOutput.ReadLineAsync() is string line)
            {
                using var json = JsonDocument.Parse(line);
                var data = json.RootElement;
                string type = data.GetProperty("type").GetString() ?? "";
                var input = new InputEvent(type,
                    data.TryGetProperty("x", out var x) ? x.GetInt32() : 0,
                    data.TryGetProperty("y", out var y) ? y.GetInt32() : 0,
                    !data.TryGetProperty("enabled", out var on) || on.GetBoolean(),
                    data.TryGetProperty("message", out var message) ? message.GetString() : null);
                Post(input);
            }
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or ObjectDisposedException)
        { Post(new("error", Message: "输入组件返回无效数据：" + e.Message)); }
    }

    private async Task ReadErrors(Process child)
    {
        try
        {
            string error = await child.StandardError.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(error)) Post(new("error", Message: error.Trim()));
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    private void Post(InputEvent input)
    {
        if (closing || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(() =>
        {
            if (closing) return;
            if (input.Type == "ready") Ready = true;
            if (input.Type == "error") Ready = false;
            Received?.Invoke(input);
        });
    }

    public void Dispose()
    {
        if (closing) return;
        if (process != null)
        {
            Send(new { type = "shutdown" });
            closing = true;
            try { process.StandardInput.Close(); if (!process.WaitForExit(500)) process.Kill(); }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
        closing = true;
    }
}
