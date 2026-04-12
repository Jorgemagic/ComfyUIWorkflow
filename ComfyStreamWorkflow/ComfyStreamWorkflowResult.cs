namespace ComfyStreamWorkflow;

public sealed record ComfyStreamWorkflowResult(
    string PromptId,
    IReadOnlyList<ComfyGeneratedImage> Images,
    TimeSpan QueueDuration = default,
    TimeSpan WaitDuration = default);
