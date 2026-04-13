using System.Net.WebSockets;
using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ComfyStreamWorkflow;

public sealed class ComfyStreamWorkflowRunner : IAsyncDisposable
{
    private readonly ComfyStreamWorkflowOptions options;
    private readonly HttpClient httpClient;
    private readonly ComfyUiHeadlessProcess headlessProcess;
    private readonly string clientId = Guid.NewGuid().ToString();
    private ClientWebSocket? webSocket;

    public ComfyStreamWorkflowRunner(ComfyStreamWorkflowOptions? options = null)
    {
        this.options = options ?? new ComfyStreamWorkflowOptions();
        httpClient = new HttpClient
        {
            BaseAddress = this.options.BaseUri,
        };
        headlessProcess = new ComfyUiHeadlessProcess(this.options, httpClient);
    }

    public async Task<JObject> GetWorkflowAsync(
        string workflowPath,
        CancellationToken cancellationToken = default)
    {
        return await ComfyWorkflowInMemoryTransformer.LoadWorkflowAsync(workflowPath, cancellationToken);
    }

    public async Task<ComfyStreamWorkflowResult> ExecuteWorkflowAndWaitAsync(
        JObject workflow,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = timeout.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;

        if (timeoutCts != null)
        {
            timeoutCts.CancelAfter(timeout.GetValueOrDefault());
        }

        CancellationToken waitToken = timeoutCts?.Token ?? cancellationToken;

        return await ExecuteWorkflowAsync(workflow, waitToken);
    }

    public async Task<ComfyStreamWorkflowResult> ExecuteWorkflowAndWaitAsync(
        string workflowPath,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        JObject workflow = await GetWorkflowAsync(workflowPath, cancellationToken);
        return await ExecuteWorkflowAndWaitAsync(workflow, timeout, cancellationToken);
    }

    public async Task<ComfyStreamWorkflowResult> ExecuteWorkflowAsync(
        string workflowPath,
        CancellationToken cancellationToken = default)
    {
        JObject workflow = await GetWorkflowAsync(workflowPath, cancellationToken);
        return await ExecuteWorkflowAsync(workflow, cancellationToken);
    }

    public async Task<ComfyStreamWorkflowResult> ExecuteWorkflowAsync(
        JObject workflow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        JObject inMemoryWorkflow = ComfyWorkflowInMemoryTransformer.ReplaceFileOutputsWithWebSocketOutputs(workflow);
        return await ExecutePreparedWorkflowAsync(inMemoryWorkflow, cancellationToken);
    }

    public async Task<ComfyStreamWorkflowResult> ExecutePreparedWorkflowAndWaitAsync(
        JObject workflow,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = timeout.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;

        if (timeoutCts != null)
        {
            timeoutCts.CancelAfter(timeout.GetValueOrDefault());
        }

        CancellationToken waitToken = timeoutCts?.Token ?? cancellationToken;

        return await ExecutePreparedWorkflowAsync(workflow, waitToken);
    }

    public async Task<ComfyStreamWorkflowResult> ExecutePreparedWorkflowAsync(
        JObject workflow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        await headlessProcess.EnsureStartedAsync(cancellationToken);

        ClientWebSocket activeWebSocket = await GetOrCreateWebSocketAsync(cancellationToken);

        var queueStart = Stopwatch.GetTimestamp();
        string promptId = await QueuePromptAsync(workflow, cancellationToken);
        TimeSpan queueDuration = Stopwatch.GetElapsedTime(queueStart);

        var waitStart = Stopwatch.GetTimestamp();
        IReadOnlyList<ComfyGeneratedImage> images = await WaitForImagesAsync(activeWebSocket, promptId, cancellationToken);
        TimeSpan waitDuration = Stopwatch.GetElapsedTime(waitStart);

        return new ComfyStreamWorkflowResult(promptId, images, queueDuration, waitDuration);
    }

    public async Task SaveImageAsync(
        ComfyGeneratedImage image,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Output path cannot be null or empty.", nameof(outputPath));
        }

        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(outputPath, image.Bytes, cancellationToken);
    }

    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        await headlessProcess.EnsureStartedAsync(cancellationToken);

        using var response = await httpClient.PostAsync("interrupt", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task KillStartedComfyUiAsync()
    {
        await headlessProcess.KillStartedProcessAsync();
    }

    public async Task<ComfyUploadedImage> UploadImageAsync(
        byte[] imageBytes,
        string filename,
        string? subfolder = null,
        string type = "input",
        bool overwrite = true,
        string contentType = "image/png",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);

        if (string.IsNullOrWhiteSpace(filename))
        {
            throw new ArgumentException("Filename cannot be null or empty.", nameof(filename));
        }

        await headlessProcess.EnsureStartedAsync(cancellationToken);

        using var form = new MultipartFormDataContent();
        using var imageContent = new ByteArrayContent(imageBytes);

        imageContent.Headers.ContentType = new(contentType);
        form.Add(imageContent, "image", filename);
        form.Add(new StringContent(type), "type");
        form.Add(new StringContent(overwrite ? "true" : "false"), "overwrite");

        if (!string.IsNullOrWhiteSpace(subfolder))
        {
            form.Add(new StringContent(subfolder), "subfolder");
        }

        using var response = await httpClient.PostAsync("upload/image", form, cancellationToken);
        response.EnsureSuccessStatusCode();

        string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        JObject json = JObject.Parse(responseJson);

        string uploadedFilename = json["name"]?.Value<string>() ?? filename;
        string uploadedSubfolder = json["subfolder"]?.Value<string>() ?? string.Empty;
        string uploadedType = json["type"]?.Value<string>() ?? type;

        return new ComfyUploadedImage(uploadedFilename, uploadedSubfolder, uploadedType);
    }

    public async ValueTask DisposeAsync()
    {
        if (webSocket is not null)
        {
            if (webSocket.State == WebSocketState.Open)
            {
                try
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "ComfyStreamWorkflowRunner disposed.",
                        CancellationToken.None);
                }
                catch (WebSocketException)
                {
                }
            }

            webSocket.Dispose();
        }

        httpClient.Dispose();
        await headlessProcess.DisposeAsync();
    }

    private async Task<ClientWebSocket> GetOrCreateWebSocketAsync(CancellationToken cancellationToken)
    {
        if (webSocket?.State == WebSocketState.Open)
        {
            return webSocket;
        }

        webSocket?.Dispose();
        webSocket = new ClientWebSocket();
        await webSocket.ConnectAsync(BuildWebSocketUri(), cancellationToken);

        return webSocket;
    }

    private async Task<string> QueuePromptAsync(JObject workflow, CancellationToken cancellationToken)
    {
        var requestBody = new JObject
        {
            ["prompt"] = workflow.DeepClone(),
            ["client_id"] = clientId,
        };

        using var content = new StringContent(requestBody.ToString(Formatting.None), Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync("prompt", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        JObject json = JObject.Parse(responseJson);

        string? promptId = json["prompt_id"]?.Value<string>();
        if (promptId is null)
        {
            throw new InvalidOperationException("ComfyUI /prompt response did not include a prompt_id.");
        }

        return promptId;
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

        JObject root = JObject.Parse(Encoding.UTF8.GetString(payload));

        string? type = root["type"]?.Value<string>();

        if (root["data"] is not JObject data)
        {
            return false;
        }

        string? messagePromptId = data["prompt_id"]?.Value<string>();

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
            && data["node"]?.Type == JTokenType.Null;
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
