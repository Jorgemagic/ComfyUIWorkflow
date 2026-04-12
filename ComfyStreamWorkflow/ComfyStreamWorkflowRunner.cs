using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ComfyStreamWorkflow;

public sealed class ComfyStreamWorkflowRunner : IAsyncDisposable
{
    private readonly ComfyStreamWorkflowOptions options;
    private readonly HttpClient httpClient;
    private readonly ComfyUiHeadlessProcess headlessProcess;
    private readonly string clientId = Guid.NewGuid().ToString();

    public ComfyStreamWorkflowRunner(ComfyStreamWorkflowOptions? options = null)
    {
        this.options = options ?? new ComfyStreamWorkflowOptions();
        httpClient = new HttpClient
        {
            BaseAddress = this.options.BaseUri,
        };
        headlessProcess = new ComfyUiHeadlessProcess(this.options, httpClient);
    }

    public async Task<ComfyStreamWorkflowResult> ExecuteWorkflowAsync(
        string workflowPath,
        CancellationToken cancellationToken = default)
    {
        JsonObject workflow = await ComfyWorkflowInMemoryTransformer.LoadWorkflowAsync(workflowPath, cancellationToken);
        return await ExecuteWorkflowAsync(workflow, cancellationToken);
    }

    public async Task<ComfyStreamWorkflowResult> ExecuteWorkflowAsync(
        JsonObject workflow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        await headlessProcess.EnsureStartedAsync(cancellationToken);

        JsonObject inMemoryWorkflow = ComfyWorkflowInMemoryTransformer.ReplaceFileOutputsWithWebSocketOutputs(workflow);

        using var webSocket = new ClientWebSocket();
        await webSocket.ConnectAsync(BuildWebSocketUri(), cancellationToken);

        string promptId = await QueuePromptAsync(inMemoryWorkflow, cancellationToken);
        IReadOnlyList<ComfyGeneratedImage> images = await WaitForImagesAsync(webSocket, promptId, cancellationToken);

        return new ComfyStreamWorkflowResult(promptId, images);
    }

    public async ValueTask DisposeAsync()
    {
        httpClient.Dispose();
        await headlessProcess.DisposeAsync();
    }

    private async Task<string> QueuePromptAsync(JsonObject workflow, CancellationToken cancellationToken)
    {
        var requestBody = new JsonObject
        {
            ["prompt"] = workflow.DeepClone(),
            ["client_id"] = clientId,
        };

        using var content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync("prompt", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);

        if (!json.RootElement.TryGetProperty("prompt_id", out JsonElement promptId))
        {
            throw new InvalidOperationException("ComfyUI /prompt response did not include a prompt_id.");
        }

        return promptId.GetString()
            ?? throw new InvalidOperationException("ComfyUI /prompt response included an empty prompt_id.");
    }

    private async Task<IReadOnlyList<ComfyGeneratedImage>> WaitForImagesAsync(
        ClientWebSocket webSocket,
        string promptId,
        CancellationToken cancellationToken)
    {
        var images = new List<ComfyGeneratedImage>();

        while (webSocket.State == WebSocketState.Open)
        {
            WebSocketMessage message = await ReceiveFullMessageAsync(webSocket, cancellationToken);

            if (message.MessageType == WebSocketMessageType.Binary)
            {
                byte[] imageBytes = StripComfyBinaryHeader(message.Payload);
                images.Add(new ComfyGeneratedImage(imageBytes, "image/png"));
                continue;
            }

            if (message.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            if (IsPromptFinished(message.Payload, promptId, out string? error))
            {
                if (error is not null)
                {
                    throw new InvalidOperationException(error);
                }

                return images;
            }
        }

        throw new InvalidOperationException("ComfyUI WebSocket closed before the prompt finished.");
    }

    private byte[] StripComfyBinaryHeader(byte[] payload)
    {
        if (payload.Length <= options.ImageBinaryHeaderBytes)
        {
            return payload;
        }

        return payload[options.ImageBinaryHeaderBytes..];
    }

    private static bool IsPromptFinished(byte[] payload, string promptId, out string? error)
    {
        error = null;

        using var json = JsonDocument.Parse(payload);
        JsonElement root = json.RootElement;

        string? type = root.TryGetProperty("type", out JsonElement typeElement)
            ? typeElement.GetString()
            : null;

        if (!root.TryGetProperty("data", out JsonElement data))
        {
            return false;
        }

        string? messagePromptId = data.TryGetProperty("prompt_id", out JsonElement promptElement)
            ? promptElement.GetString()
            : null;

        if (!string.Equals(messagePromptId, promptId, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(type, "execution_error", StringComparison.Ordinal))
        {
            error = $"ComfyUI execution error: {Encoding.UTF8.GetString(payload)}";
            return true;
        }

        if (string.Equals(type, "execution_success", StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(type, "executing", StringComparison.Ordinal)
            && data.TryGetProperty("node", out JsonElement node)
            && node.ValueKind == JsonValueKind.Null;
    }

    private Uri BuildWebSocketUri()
    {
        string scheme = options.BaseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            ? "wss"
            : "ws";

        return new UriBuilder(options.BaseUri)
        {
            Scheme = scheme,
            Path = "ws",
            Query = $"clientId={Uri.EscapeDataString(clientId)}",
        }.Uri;
    }

    private static async Task<WebSocketMessage> ReceiveFullMessageAsync(
        ClientWebSocket webSocket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var stream = new MemoryStream();

        WebSocketReceiveResult result;
        do
        {
            result = await webSocket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("ComfyUI WebSocket closed.");
            }

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return new WebSocketMessage(result.MessageType, stream.ToArray());
    }

    private sealed record WebSocketMessage(WebSocketMessageType MessageType, byte[] Payload);
}
