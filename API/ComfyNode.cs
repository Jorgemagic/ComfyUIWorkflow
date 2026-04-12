using Newtonsoft.Json;

namespace ComfyUIWorkflowRun.API
{
    public sealed class ComfyNode
    {
        [JsonProperty("inputs")]
        public Dictionary<string, object?> Inputs { get; set; } = new();

        [JsonProperty("class_type")]
        public string ClassType { get; set; } = string.Empty;

        [JsonProperty("_meta")]
        public Dictionary<string, object?> Meta { get; set; } = new();
    }
}
