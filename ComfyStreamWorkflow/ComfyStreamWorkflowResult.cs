namespace ComfyStreamWorkflow;

public sealed class ComfyStreamWorkflowResult
{
    public ComfyStreamWorkflowResult(
        string promptId,
        IReadOnlyList<ComfyGeneratedImage> images,
        TimeSpan queueDuration = default,
        TimeSpan waitDuration = default)
    {
        PromptId = promptId;
        Images = images;
        QueueDuration = queueDuration;
        WaitDuration = waitDuration;
    }

    public string PromptId { get; }

    public IReadOnlyList<ComfyGeneratedImage> Images { get; }

    public TimeSpan QueueDuration { get; }

    public TimeSpan WaitDuration { get; }
}
