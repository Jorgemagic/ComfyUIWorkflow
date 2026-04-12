using Newtonsoft.Json;

namespace ComfyUIWorkflowRun.API
{
    public sealed class ComfyPromptResponse
    {
        [JsonProperty("prompt_id")]
        public string PromptId { get; set; } = string.Empty;

        [JsonProperty("number")]
        public int? Number { get; set; }

        [JsonProperty("node_errors")]
        public Dictionary<string, object?> NodeErrors { get; set; } = new();
    }
}
