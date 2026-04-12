using System.Diagnostics;
using System.Text.Json;

namespace ComfyStreamWorkflow;

internal sealed class ComfyUiLaunchInfo
{
    private ComfyUiLaunchInfo(string workingDirectory, string executablePath, IReadOnlyList<string> arguments, bool redirectOutput)
    {
        WorkingDirectory = workingDirectory;
        ExecutablePath = executablePath;
        Arguments = arguments;
        RedirectOutput = redirectOutput;
    }

    public string WorkingDirectory { get; }

    public string ExecutablePath { get; }

    public IReadOnlyList<string> Arguments { get; }

    public bool RedirectOutput { get; }

    public static bool TryCreate(
        string path,
        ComfyStreamWorkflowOptions options,
        out ComfyUiLaunchInfo? launchInfo)
    {
        launchInfo = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fullPath = Path.GetFullPath(path);

        if (File.Exists(fullPath) && Path.GetFileName(fullPath).Equals("ComfyUI.exe", StringComparison.OrdinalIgnoreCase))
        {
            launchInfo = CreatePackagedApp(fullPath);
            return true;
        }

        if (!Directory.Exists(fullPath))
        {
            return false;
        }

        string mainPath = Path.Combine(fullPath, "main.py");
        if (File.Exists(mainPath))
        {
            launchInfo = CreatePythonWorkspace(fullPath, options);
            return true;
        }

        string bundledWorkspace = Path.Combine(fullPath, "resources", "ComfyUI");
        string bundledMainPath = Path.Combine(bundledWorkspace, "main.py");
        if (File.Exists(bundledMainPath))
        {
            launchInfo = CreateDesktopAppBackend(fullPath, bundledWorkspace, options);
            return true;
        }

        string executablePath = Path.Combine(fullPath, "ComfyUI.exe");
        if (File.Exists(executablePath))
        {
            launchInfo = CreatePackagedApp(executablePath);
            return true;
        }

        return false;
    }

    public ProcessStartInfo CreateProcessStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            WorkingDirectory = WorkingDirectory,
            UseShellExecute = !RedirectOutput,
            RedirectStandardOutput = RedirectOutput,
            RedirectStandardError = RedirectOutput,
            CreateNoWindow = RedirectOutput,
        };

        foreach (string argument in Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static ComfyUiLaunchInfo CreatePythonWorkspace(string workspace, ComfyStreamWorkflowOptions options)
    {
        var arguments = new List<string>
        {
            "main.py",
            "--listen",
            options.BaseUri.Host,
            "--port",
            GetPort(options.BaseUri).ToString(),
        };

        arguments.AddRange(options.ExtraComfyUiArguments);

        return new ComfyUiLaunchInfo(
            workingDirectory: workspace,
            executablePath: options.PythonExecutable,
            arguments: arguments,
            redirectOutput: true);
    }

    private static ComfyUiLaunchInfo CreatePackagedApp(string executablePath)
    {
        return new ComfyUiLaunchInfo(
            workingDirectory: Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
            executablePath: executablePath,
            arguments: Array.Empty<string>(),
            redirectOutput: false);
    }

    private static ComfyUiLaunchInfo CreateDesktopAppBackend(
        string installationDirectory,
        string bundledWorkspace,
        ComfyStreamWorkflowOptions options)
    {
        string appDataConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ComfyUI",
            "config.json");

        string basePath = TryReadDesktopBasePath(appDataConfigPath)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ComfyUI");

        string pythonPath = Path.Combine(basePath, ".venv", "Scripts", "python.exe");
        if (!File.Exists(pythonPath))
        {
            return CreatePythonWorkspace(bundledWorkspace, options);
        }

        string userDirectory = Path.Combine(basePath, "user");
        string inputDirectory = Path.Combine(basePath, "input");
        string outputDirectory = Path.Combine(basePath, "output");
        string databaseUrl = $"sqlite:///{Path.Combine(userDirectory, "comfyui.db").Replace('\\', '/')}";
        string frontEndRoot = Path.Combine(bundledWorkspace, "web_custom_versions", "desktop_app");
        string extraModelsConfig = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ComfyUI",
            "extra_models_config.yaml");

        var arguments = new List<string>
        {
            Path.Combine(bundledWorkspace, "main.py"),
            "--user-directory",
            userDirectory,
            "--input-directory",
            inputDirectory,
            "--output-directory",
            outputDirectory,
            "--front-end-root",
            frontEndRoot,
            "--base-directory",
            basePath,
            "--database-url",
            databaseUrl,
        };

        if (File.Exists(extraModelsConfig))
        {
            arguments.Add("--extra-model-paths-config");
            arguments.Add(extraModelsConfig);
        }

        arguments.AddRange(new[]
        {
            "--log-stdout",
            "--listen",
            options.BaseUri.Host,
            "--port",
            GetPort(options.BaseUri).ToString(),
            "--enable-manager",
        });

        arguments.AddRange(options.ExtraComfyUiArguments);

        return new ComfyUiLaunchInfo(
            workingDirectory: basePath,
            executablePath: pythonPath,
            arguments: arguments,
            redirectOutput: true);
    }

    private static string? TryReadDesktopBasePath(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return null;
            }

            using var config = JsonDocument.Parse(File.ReadAllText(configPath));
            if (config.RootElement.TryGetProperty("basePath", out JsonElement basePath)
                && basePath.GetString() is { Length: > 0 } value)
            {
                return value;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static int GetPort(Uri uri)
    {
        if (!uri.IsDefaultPort)
        {
            return uri.Port;
        }

        return uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;
    }
}
