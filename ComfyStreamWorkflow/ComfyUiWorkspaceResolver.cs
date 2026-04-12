namespace ComfyStreamWorkflow;

public static class ComfyUiWorkspaceResolver
{
    private const string EnvironmentVariableName = "COMFYUI_WORKSPACE";

    public static string? Resolve(
        string? preferredPath = null,
        ComfyStreamWorkflowOptions? options = null)
    {
        ComfyStreamWorkflowOptions effectiveOptions = options ?? new ComfyStreamWorkflowOptions();

        foreach (string? candidate in GetCandidates(preferredPath))
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (ComfyUiLaunchInfo.TryCreate(candidate, effectiveOptions, out _))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static IEnumerable<string?> GetCandidates(string? preferredPath)
    {
        yield return preferredPath;
        yield return Environment.GetEnvironmentVariable(EnvironmentVariableName);

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Programs", "ComfyUI");
        }

        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory != null; directory = directory.Parent)
        {
            yield return Path.Combine(directory.FullName, "ComfyUI");
            yield return Path.Combine(directory.FullName, "comfyui");
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            yield return Path.Combine(userProfile, "ComfyUI");
            yield return Path.Combine(userProfile, "comfyui");
        }
    }
}
