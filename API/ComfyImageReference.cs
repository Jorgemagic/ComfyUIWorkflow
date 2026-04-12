using Newtonsoft.Json;

namespace ComfyUIWorkflowRun.API
{
    public sealed class ComfyImageReference
    {
        [JsonProperty("filename")]
        public string Filename { get; set; } = string.Empty;

        [JsonProperty("subfolder")]
        public string Subfolder { get; set; } = string.Empty;

        [JsonProperty("type")]
        public string Type { get; set; } = string.Empty;
    }
}
