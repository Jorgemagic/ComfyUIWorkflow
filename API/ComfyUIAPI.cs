using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.WebSockets;
using System.Text;

namespace ComfyUIWorkflowRun.API
{
    public sealed partial class ComfyUIAPI : IDisposable
    {
        private readonly HttpClient httpClient;
        private readonly Uri baseUri;
        private readonly string clientId;

        public ComfyUIAPI(string baseUrl = "http://localhost:8000/")
        {
            this.baseUri = new Uri(baseUrl);
            this.clientId = Guid.NewGuid().ToString();

            this.httpClient = new HttpClient
            {
                BaseAddress = this.baseUri
            };
        }

        public async Task<Dictionary<string, ComfyNode>> GetWorkflowAsync(
            string workflowPath,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(workflowPath))
            {
                throw new ArgumentException("Workflow path cannot be null or empty.", nameof(workflowPath));
            }

            string resolvedPath = Path.IsPathRooted(workflowPath)
                ? workflowPath
                : Path.GetFullPath(workflowPath, Environment.CurrentDirectory);

            if (!File.Exists(resolvedPath))
            {
                throw new FileNotFoundException("Workflow file was not found.", resolvedPath);
            }

            string json = await File.ReadAllTextAsync(resolvedPath, cancellationToken);

            return JsonConvert.DeserializeObject<Dictionary<string, ComfyNode>>(json)
                ?? throw new InvalidOperationException("Invalid workflow JSON.");
        }

        public async Task<ComfyPromptResponse> ExecuteWorkflowAndWaitAsync(
                    Dictionary<string, ComfyNode> workflow,
                    TimeSpan? timeout = null,
                    CancellationToken cancellationToken = default)
        {
            if (workflow is null || workflow.Count == 0)
            {
                throw new ArgumentException("Workflow cannot be null or empty.", nameof(workflow));
            }

            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(this.BuildWebSocketUri(), cancellationToken);

            var promptResponse = await this.ExecuteWorkflowAsync(workflow, cancellationToken);

            using var timeoutCts = timeout.HasValue
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;

            if (timeoutCts != null)
            {
                timeoutCts.CancelAfter(timeout!.Value);
            }

            CancellationToken waitToken = timeoutCts?.Token ?? cancellationToken;

            await WaitForPromptCompletionAsync(ws, promptResponse.PromptId, waitToken);

            return promptResponse;
        }

        public async Task<ComfyPromptResponse> ExecuteWorkflowAsync(
            Dictionary<string, ComfyNode> workflow,
            CancellationToken cancellationToken = default)
        {
            if (workflow is null || workflow.Count == 0)
            {
                throw new ArgumentException("Workflow cannot be null or empty.", nameof(workflow));
            }

            var requestBody = new
            {
                prompt = workflow,
                client_id = this.clientId,
            };

            string json = JsonConvert.SerializeObject(requestBody);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await this.httpClient.PostAsync("prompt", content, cancellationToken);
            response.EnsureSuccessStatusCode();

            string responseText = await response.Content.ReadAsStringAsync(cancellationToken);

            return JsonConvert.DeserializeObject<ComfyPromptResponse>(responseText)
                ?? throw new InvalidOperationException("Invalid response received from ComfyUI /prompt.");
        }

        public async Task<Dictionary<string, ComfyHistoryEntry>> GetHistoryAsync(
            string? promptId = null,
            CancellationToken cancellationToken = default)
        {
            string path = string.IsNullOrWhiteSpace(promptId)
                ? "history"
                : $"history/{Uri.EscapeDataString(promptId)}";

            using var response = await this.httpClient.GetAsync(path, cancellationToken);
            response.EnsureSuccessStatusCode();

            string responseText = await response.Content.ReadAsStringAsync(cancellationToken);

            return JsonConvert.DeserializeObject<Dictionary<string, ComfyHistoryEntry>>(responseText)
                ?? throw new InvalidOperationException("Invalid response received from ComfyUI /history.");
        }

        public async Task DownloadImageAsync(
            string filename,
            string? subfolder,
            string type,
            string outputPath,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filename))
            {
                throw new ArgumentException("Filename cannot be null or empty.", nameof(filename));
            }

            if (string.IsNullOrWhiteSpace(type))
            {
                throw new ArgumentException("Image type cannot be null or empty.", nameof(type));
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("Output path cannot be null or empty.", nameof(outputPath));
            }

            string requestUri = $"view?filename={Uri.EscapeDataString(filename)}&type={Uri.EscapeDataString(type)}";
            if (!string.IsNullOrWhiteSpace(subfolder))
            {
                requestUri += $"&subfolder={Uri.EscapeDataString(subfolder)}";
            }

            using var response = await this.httpClient.GetAsync(
                requestUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            string? directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = File.Create(outputPath);
            await source.CopyToAsync(destination, cancellationToken);
        }

        private Uri BuildWebSocketUri()
        {
            string scheme = this.baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "ws";

            return new UriBuilder(this.baseUri)
            {
                Scheme = scheme,
                Path = "ws",
                Query = $"clientId={Uri.EscapeDataString(this.clientId)}"
            }.Uri;
        }

        private static async Task WaitForPromptCompletionAsync(ClientWebSocket ws, string promptId, CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            while (ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        throw new InvalidOperationException("WebSocket closed before prompt completion.");
                    }
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }
                string json = Encoding.UTF8.GetString(ms.ToArray());
                var message = JObject.Parse(json);
                string? type = message["type"]?.Value<string>();
                var data = message["data"];
                if (data == null)
                {
                    continue;
                }
                string? messagePromptId = data["prompt_id"]?.Value<string>();
                if (!string.Equals(messagePromptId, promptId, StringComparison.Ordinal))
                {
                    continue;
                }
                if (type == "execution_error")
                {
                    throw new InvalidOperationException($"ComfyUI execution error: {json}");
                }
                if (type == "execution_success")
                {
                    return;
                }
                if (type == "executing" && data["node"]?.Type == JTokenType.Null)
                {
                    return;
                }
            }

            throw new InvalidOperationException("WebSocket disconnected before prompt completion.");
        }

        public void Dispose()
        {
            this.httpClient.Dispose();
        }
    }
}
