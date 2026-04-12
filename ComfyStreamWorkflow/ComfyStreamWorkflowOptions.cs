namespace ComfyStreamWorkflow;

public sealed class ComfyStreamWorkflowOptions
{
    public Uri BaseUri { get; init; } = new("http://localhost:8000/");

    public string? ComfyUiWorkspacePath { get; init; }

    public string PythonExecutable { get; init; } = "python";

    public bool StartComfyUiIfWorkspaceProvided { get; init; } = true;

    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(90);

    public IReadOnlyList<string> ExtraComfyUiArguments { get; init; } = Array.Empty<string>();

    public int ImageBinaryHeaderBytes { get; init; } = 8;
}
