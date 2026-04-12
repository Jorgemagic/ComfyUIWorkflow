using System.Text.Json.Nodes;

namespace ComfyStreamWorkflow;

public static class ComfyWorkflowInMemoryTransformer
{
    private static readonly HashSet<string> FileOutputNodeTypes = new(StringComparer.Ordinal)
    {
        "SaveImage",
        "PreviewImage",
    };

    public static JsonObject ReplaceFileOutputsWithWebSocketOutputs(JsonObject workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var clone = JsonNode.Parse(workflow.ToJsonString())?.AsObject()
            ?? throw new ArgumentException("Workflow JSON is not a valid object.", nameof(workflow));

        foreach (var (_, node) in clone.ToArray())
        {
            if (node is not JsonObject nodeObject)
            {
                continue;
            }

            string? classType = nodeObject["class_type"]?.GetValue<string>();
            if (classType is null || !FileOutputNodeTypes.Contains(classType))
            {
                continue;
            }

            JsonNode? imagesInput = nodeObject["inputs"]?["images"];
            if (imagesInput is null)
            {
                throw new InvalidOperationException($"Output node '{classType}' does not contain an 'images' input.");
            }

            nodeObject["class_type"] = "SaveImageWebsocket";
            nodeObject["inputs"] = new JsonObject
            {
                ["images"] = imagesInput.DeepClone()
            };
            nodeObject["_meta"] = new JsonObject
            {
                ["title"] = "SaveImageWebsocket"
            };
        }

        return clone;
    }

    public static async Task<JsonObject> LoadWorkflowAsync(
        string workflowPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workflowPath))
        {
            throw new ArgumentException("Workflow path cannot be null or empty.", nameof(workflowPath));
        }

        string resolvedPath = Path.IsPathRooted(workflowPath)
            ? workflowPath
            : Path.GetFullPath(workflowPath, Environment.CurrentDirectory);

        await using var file = File.OpenRead(resolvedPath);
        JsonNode? workflow = await JsonNode.ParseAsync(file, cancellationToken: cancellationToken);

        return workflow?.AsObject()
            ?? throw new InvalidOperationException("Workflow JSON is not a valid object.");
    }
}
