using Newtonsoft.Json;

namespace ComfyUIWorkflowRun.API
{
    public sealed class ComfyHistoryEntry
    {
        [JsonProperty("prompt")]
        public object[] Prompt { get; set; } = Array.Empty<object>();

        [JsonProperty("outputs")]
        public Dictionary<string, ComfyOutputNode> Outputs { get; set; } = new();

        [JsonProperty("status")]
        public ComfyExecutionStatus? Status { get; set; }
    }
}
