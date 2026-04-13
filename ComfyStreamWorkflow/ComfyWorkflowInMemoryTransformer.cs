using Newtonsoft.Json.Linq;

namespace ComfyStreamWorkflow;

public static class ComfyWorkflowInMemoryTransformer
{
    private static readonly HashSet<string> FileOutputNodeTypes = new(StringComparer.Ordinal)
    {
        "SaveImage",
        "PreviewImage",
    };

    public static JObject ReplaceFileOutputsWithWebSocketOutputs(JObject workflow)
    {
        return ReplaceFileOutputsWithWebSocketOutputs(workflow, outputWidth: null, outputHeight: null);
    }

    public static JObject ReplaceFileOutputsWithWebSocketOutputs(
        JObject workflow,
        int? outputWidth,
        int? outputHeight)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var clone = (JObject)workflow.DeepClone();
        foreach (JProperty property in clone.Properties().ToArray())
        {
            if (property.Value is not JObject nodeObject)
            {
                continue;
            }

            string? classType = nodeObject["class_type"]?.Value<string>();
            if (classType is null || !FileOutputNodeTypes.Contains(classType))
            {
                continue;
            }

            JToken? imagesInput = nodeObject["inputs"]?["images"];
            if (imagesInput is null)
            {
                throw new InvalidOperationException($"Output node '{classType}' does not contain an 'images' input.");
            }

            nodeObject["class_type"] = "SaveImageWebsocket";
            nodeObject["inputs"] = new JObject
            {
                ["images"] = imagesInput.DeepClone(),
            };
            nodeObject["_meta"] = new JObject
            {
                ["title"] = "SaveImageWebsocket",
            };
        }

        return clone;
    }

    public static async Task<JObject> LoadWorkflowAsync(
        string workflowPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workflowPath))
        {
            throw new ArgumentException("Workflow path cannot be null or empty.", nameof(workflowPath));
        }

        string resolvedPath = ResolveWorkflowPath(workflowPath);

        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException("Workflow file was not found.", resolvedPath);
        }

        string workflowJson = await File.ReadAllTextAsync(resolvedPath, cancellationToken);

        return JObject.Parse(workflowJson)
            ?? throw new InvalidOperationException("Workflow JSON is not a valid object.");
    }

    private static string ResolveWorkflowPath(string workflowPath)
    {
        if (Path.IsPathRooted(workflowPath))
        {
            return Path.GetFullPath(workflowPath);
        }

        foreach (string basePath in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            string candidate = Path.GetFullPath(workflowPath, basePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.GetFullPath(workflowPath, Environment.CurrentDirectory);
    }
}
