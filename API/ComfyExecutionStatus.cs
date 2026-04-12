using Newtonsoft.Json;

namespace ComfyUIWorkflowRun.API
{
    public sealed class ComfyExecutionStatus
    {
        [JsonProperty("status_str")]
        public string Status { get; set; } = string.Empty;

        [JsonProperty("completed")]
        public bool Completed { get; set; }

        [JsonProperty("messages")]
        public List<object> Messages { get; set; } = new();
    }
}
