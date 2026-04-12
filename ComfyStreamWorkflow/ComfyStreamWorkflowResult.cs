namespace ComfyStreamWorkflow;

public sealed class ComfyStreamWorkflowResult
{
    public ComfyStreamWorkflowResult(string promptId, IReadOnlyList<ComfyGeneratedImage> images)
    {
        PromptId = promptId;
        Images = images;
    }

    public string PromptId { get; }

    public IReadOnlyList<ComfyGeneratedImage> Images { get; }
}
