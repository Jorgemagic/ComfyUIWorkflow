using Newtonsoft.Json;

namespace ComfyUIWorkflowRun.API
{
    public sealed class ComfyOutputNode
    {
        [JsonProperty("images")]
        public List<ComfyImageReference> Images { get; set; } = new();

        [JsonProperty("gifs")]
        public List<ComfyImageReference> Gifs { get; set; } = new();

        [JsonProperty("text")]
        public List<string> Text { get; set; } = new();
    }
}
