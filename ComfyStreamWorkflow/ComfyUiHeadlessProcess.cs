using System.Diagnostics;

namespace ComfyStreamWorkflow;

internal sealed class ComfyUiHeadlessProcess : IAsyncDisposable
{
    private readonly ComfyStreamWorkflowOptions options;
    private readonly HttpClient httpClient;
    private Process? process;
    private bool startedByThisInstance;
    private bool isKnownReachable;

    public ComfyUiHeadlessProcess(ComfyStreamWorkflowOptions options, HttpClient httpClient)
    {
        this.options = options;
        this.httpClient = httpClient;
    }

    public async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (isKnownReachable && process?.HasExited != true)
        {
            return;
        }

        if (await IsComfyUiReachableAsync(cancellationToken))
        {
            isKnownReachable = true;
            return;
        }

        if (!options.StartComfyUiIfWorkspaceProvided || string.IsNullOrWhiteSpace(options.ComfyUiWorkspacePath))
        {
            throw new InvalidOperationException(
                "ComfyUI is not reachable and no valid ComfyUI path was provided to start it.");
        }

        if (!ComfyUiLaunchInfo.TryCreate(options.ComfyUiWorkspacePath, options, out ComfyUiLaunchInfo? launchInfo) || launchInfo is null)
        {
            throw new InvalidOperationException(
                $"The ComfyUI path is not valid. Expected either a folder with main.py or a ComfyUI.exe installation: {options.ComfyUiWorkspacePath}");
        }

        process = Process.Start(launchInfo.CreateProcessStartInfo())
            ?? throw new InvalidOperationException("ComfyUI process could not be started.");
        startedByThisInstance = true;

        if (launchInfo.RedirectOutput)
        {
            _ = Task.Run(() => DrainAsync(process.StandardOutput, cancellationToken), cancellationToken);
            _ = Task.Run(() => DrainAsync(process.StandardError, cancellationToken), cancellationToken);
        }

        await WaitUntilReachableAsync(cancellationToken);
        isKnownReachable = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (process is null || !startedByThisInstance)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private async Task WaitUntilReachableAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.StartupTimeout);

        while (!timeout.IsCancellationRequested)
        {
            if (process?.HasExited == true)
            {
                throw new InvalidOperationException($"ComfyUI exited before becoming reachable. Exit code: {process.ExitCode}.");
            }

            if (await IsComfyUiReachableAsync(timeout.Token))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);
        }

        throw new TimeoutException($"ComfyUI did not become reachable within {options.StartupTimeout}.");
    }

    private async Task<bool> IsComfyUiReachableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(string.Empty, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            await reader.ReadLineAsync(cancellationToken);
        }
    }
}
