using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace WpfApp20.Services;

/// <summary>一轮模型回复中的一次函数调用。</summary>
public sealed class LlmToolCall
{
    public required string Name { get; init; }
    public JsonElement Args { get; init; }
}

/// <summary>发给模型的一条消息（role 可为 system / user / assistant / tool）。</summary>
public sealed class LlmMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public List<LlmToolCall>? ToolCalls { get; set; }
}

/// <summary>模型的一轮完整输出。</summary>
public sealed class LlmReply
{
    public string Content { get; init; } = "";
    public List<LlmToolCall> ToolCalls { get; init; } = [];
}

/// <summary>本地 Ollama 服务的探测、拉起与带工具调用的流式对话。</summary>
public static class LlmClient
{
    public const string Host = "http://127.0.0.1:11434";
    public const string HostAddress = "127.0.0.1:11434";

    public const string DefaultModel = "qwen2.5:3b";

    // 流式响应是长连接，不能用默认超时
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>项目根目录（含 ollama\ 与 models\ 的那一层）。</summary>
    public static string RootDir { get; } = FindRoot();

    private static string OllamaExe => Path.Combine(RootDir, "ollama", "ollama.exe");
    private static string ModelsDir => Path.Combine(RootDir, "models");

    /// <summary>从程序所在目录逐级向上定位便携版 Ollama。</summary>
    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ollama", "ollama.exe")))
                return dir.FullName;
        }
        return AppContext.BaseDirectory;
    }

    public static bool HasLocalInstall => File.Exists(OllamaExe);

    public static async Task<bool> IsAliveAsync(int timeoutMs = 3000)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var resp = await Http.GetAsync($"{Host}/api/version", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<List<string>> GetModelsAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var resp = await Http.GetAsync($"{Host}/api/tags", cts.Token);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var doc = JsonDocument.Parse(stream);

            var list = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var models))
            {
                foreach (var m in models.EnumerateArray())
                    if (m.TryGetProperty("name", out var n) && n.GetString() is { } name)
                        list.Add(name);
            }
            return list;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>后台拉起 ollama serve（隐藏窗口），模型目录钉在项目内。</summary>
    public static bool StartServer()
    {
        if (!HasLocalInstall) return false;
        try
        {
            var psi = new ProcessStartInfo(OllamaExe, "serve")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = RootDir,
            };
            psi.EnvironmentVariables["OLLAMA_MODELS"] = ModelsDir;
            psi.EnvironmentVariables["OLLAMA_HOST"] = HostAddress;
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>若服务未运行则拉起，轮询等待就绪。</summary>
    public static async Task<bool> EnsureServiceAsync(TimeSpan timeout)
    {
        if (await IsAliveAsync(1500)) return true;
        StartServer();

        var deadline = DateTime.Now + timeout;
        while (DateTime.Now < deadline)
        {
            await Task.Delay(500);
            if (await IsAliveAsync(1500)) return true;
        }
        return false;
    }

    /// <summary>
    /// 流式对话。每收到一段文本就通过 onToken 回调抛出；
    /// 若模型要求调用函数，会在流结束后一并返回。
    /// </summary>
    public static async Task<LlmReply> ChatAsync(
        string model,
        IEnumerable<LlmMessage> history,
        IEnumerable<ToolSpec>? tools,
        Func<string, Task>? onToken,
        CancellationToken ct)
    {
        var json = BuildPayload(model, history, tools);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Host}/api/chat")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            var brief = body.Length > 200 ? body[..200] + "…" : body;
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}：{brief}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var text = new StringBuilder();
        // 流式响应里工具调用可能分块到达，按 index 归并
        var calls = new Dictionary<int, (string Name, StringBuilder Args)>();

        while (true)
        {
            if (ct.IsCancellationRequested) break;

            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (line.Length == 0) continue;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue; // 偶发的半行数据
            }

            using (doc)
            {
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var err))
                    throw new InvalidOperationException(err.GetString() ?? "模型返回未知错误");

                if (!root.TryGetProperty("message", out var msg)) continue;

                if (msg.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String &&
                    content.GetString() is { Length: > 0 } piece)
                {
                    text.Append(piece);
                    if (onToken is not null) await onToken(piece);
                }

                if (msg.TryGetProperty("tool_calls", out var toolCalls) &&
                    toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var call in toolCalls.EnumerateArray())
                    {
                        if (!call.TryGetProperty("function", out var fn)) continue;

                        var name = fn.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (string.IsNullOrEmpty(name)) continue;

                        var argsJson = fn.TryGetProperty("arguments", out var a)
                            ? a.ValueKind == JsonValueKind.String ? a.GetString() ?? "{}" : a.GetRawText()
                            : "{}";

                        var index = call.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var i)
                            ? i
                            : calls.Count;

                        if (!calls.TryGetValue(index, out var acc))
                            acc = (name, new StringBuilder());

                        if (acc.Name != name) acc = (name, acc.Args);
                        acc.Args.Append(argsJson);
                        calls[index] = acc;
                    }
                }
            }
        }

        var result = new List<LlmToolCall>();
        foreach (var kv in calls.OrderBy(k => k.Key))
        {
            JsonElement args;
            try
            {
                using var argsDoc = JsonDocument.Parse(kv.Value.Args.Length == 0 ? "{}" : kv.Value.Args.ToString());
                args = argsDoc.RootElement.Clone();
            }
            catch (JsonException)
            {
                args = JsonDocument.Parse("{}").RootElement.Clone();
            }
            result.Add(new LlmToolCall { Name = kv.Value.Name, Args = args });
        }

        return new LlmReply { Content = text.ToString(), ToolCalls = result };
    }

    private static string BuildPayload(string model, IEnumerable<LlmMessage> history, IEnumerable<ToolSpec>? tools)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteBoolean("stream", true);

            // 工业问答场景：压低随机性、限制篇幅，既更快也不会跑偏
            w.WriteStartObject("options");
            w.WriteNumber("temperature", 0.2);
            w.WriteNumber("num_predict", 512);
            w.WriteEndObject();

            if (tools is not null)
            {
                var list = tools.ToList();
                if (list.Count > 0)
                {
                    w.WriteStartArray("tools");
                    foreach (var t in list)
                    {
                        w.WriteStartObject();
                        w.WriteString("type", "function");
                        w.WriteStartObject("function");
                        w.WriteString("name", t.Name);
                        w.WriteString("description", t.Description);
                        w.WritePropertyName("parameters");
                        using (var paramsDoc = JsonDocument.Parse(t.ParametersJson))
                            paramsDoc.RootElement.WriteTo(w);
                        w.WriteEndObject();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
            }

            w.WriteStartArray("messages");
            foreach (var m in history)
            {
                w.WriteStartObject();
                w.WriteString("role", m.Role);
                if (m.Content.Length > 0 || m.ToolCalls is null)
                    w.WriteString("content", m.Content);

                if (m.ToolCalls is { Count: > 0 })
                {
                    w.WriteStartArray("tool_calls");
                    foreach (var call in m.ToolCalls)
                    {
                        w.WriteStartObject();
                        w.WriteStartObject("function");
                        w.WriteString("name", call.Name);
                        w.WritePropertyName("arguments");
                        call.Args.WriteTo(w);
                        w.WriteEndObject();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
