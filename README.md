# ComfyUI Workflow Runner

C#/.NET client and sample apps for running ComfyUI workflows from code, receiving results over WebSocket, and experimenting with image pipelines from both text prompts and a webcam feed.

This repository does not reimplement ComfyUI. It uses ComfyUI as the Python engine and adds a .NET layer to:

- load ComfyUI workflow JSON files;
- submit prompts through the HTTP/WebSocket API;
- transform `SaveImage` and `PreviewImage` outputs into `SaveImageWebsocket`;
- collect generated images as in-memory PNG bytes;
- start ComfyUI headlessly when a valid workspace is available;
- send webcam frames to ComfyUI and display the processed result.

## Projects

| Project | Purpose |
| --- | --- |
| `ComfyStreamWorkflow` | Main library. Manages ComfyUI, transforms workflows, and returns images in memory. |
| `ComfyUIWorkflowRun` | Console sample that runs `Text2Img.json` and saves `result.png`. |
| `WebCamComfyStream` | Webcam console sample using OpenCvSharp to send frames to ComfyUI and display the output. |
| `API` | Lower-level ComfyUI client built around `/prompt`, `/history`, `/view`, and WebSocket. |

## Requirements

- .NET 8 SDK.
- A working ComfyUI installation with its dependencies and models.
- Windows for the included webcam sample, because it uses `OpenCvSharp4.runtime.win`.
- A webcam if you want to run `WebCamComfyStream`.

ComfyUI can already be running at `http://localhost:8000/`, or you can provide a workspace path so `ComfyStreamWorkflow` can start it as a child process.

The workspace resolver looks in roughly this order:

- the path provided in code;
- the `COMFYUI_WORKSPACE` environment variable;
- the desktop installation under `%LOCALAPPDATA%\Programs\ComfyUI`;
- nearby `ComfyUI` or `comfyui` folders from the current directory or user profile.

## Quick Start

```powershell
git clone <repository-url>
cd ComfyUIWorkflow
dotnet restore
dotnet build
```

If ComfyUI is not already running, configure the workspace:

```powershell
$env:COMFYUI_WORKSPACE = "C:\path\to\ComfyUI"
```

Run the text-to-image sample:

```powershell
dotnet run --project ComfyUIWorkflowRun.csproj
```

The sample loads `Text2Img.json`, updates the prompt from `Program.cs`, waits for ComfyUI to finish, and downloads the first generated image as `result.png` in the output directory.

Run the webcam sample:

```powershell
dotnet run --project WebCamComfyStream\WebCamComfyStream.csproj
```

Press `ESC` in the preview window or terminal to stop it.

## Library Usage

```csharp
await using var runner = new ComfyStreamWorkflowRunner(new ComfyStreamWorkflowOptions
{
    BaseUri = new Uri("http://localhost:8000/"),
    ComfyUiWorkspacePath = ComfyUiWorkspaceResolver.Resolve(),
    PythonExecutable = "python",
    ExtraComfyUiArguments = new[] { "--disable-cuda-malloc" }
});

ComfyStreamWorkflowResult result =
    await runner.ExecuteWorkflowAsync("Text2Img.json");

byte[] pngBytes = result.Images[0].Bytes.ToArray();
await runner.SaveImageAsync(result.Images[0], "outputs/result.png");
```

Before submitting the prompt, the library clones the workflow and replaces file output nodes (`SaveImage` and `PreviewImage`) with `SaveImageWebsocket`. This lets the output come back over WebSocket as PNG bytes, without depending on `/view` downloads or writing the image to disk first.

## Included Workflows

- `Text2Img.json`: text-to-image workflow for the console sample.
- `ComfyStreamWorkflow/Text2Img.json`: workflow copy used by the library project.
- `WebCamComfyStream/WebCamCanny.json`: webcam workflow focused on Canny-style processing.
- `WebCamComfyStream/WebcamFluxKlein2.json`: alternative workflow for the webcam demo.

To use your own workflows, export them from ComfyUI as API JSON. Webcam workflows must contain at least one `WebcamCapture`, `LoadImage`, or `PrimaryInputLoadImage` node, because that is where the current frame is injected.

## Common Configuration

In `WebCamComfyStream/Program.cs`, you can change:

```csharp
private static readonly WebcamStreamHelper.Options StreamOptions = new(
    WorkflowPath: "WebcamFluxKlein2.json",
    CameraIndex: 0,
    ComfyUiBaseUrl: "http://localhost:8000/",
    ComfyUiWorkspacePath: null,
    ShowStats: false);
```

- `WorkflowPath`: workflow used to process each frame.
- `CameraIndex`: local camera index.
- `ComfyUiBaseUrl`: ComfyUI backend URL.
- `ComfyUiWorkspacePath`: explicit ComfyUI path, if you do not want to use `COMFYUI_WORKSPACE`.
- `ShowStats`: prints performance stats every few seconds.

## Structure

```text
.
|-- API/                         # Basic ComfyUI client
|-- ComfyStreamWorkflow/          # Main library
|-- WebCamComfyStream/            # Webcam demo
|-- Program.cs                    # Classic Text2Img demo
|-- Text2Img.json                 # Sample workflow
|-- ComfyUIWorkflowRun.csproj
`-- ComfyUIWorkflowRun.sln
```

## Notes

- If ComfyUI is already running at `BaseUri`, the runner reuses it.
- If ComfyUI is not running and a valid workspace is available, the runner tries to start it with Python.
- The first webcam streaming version uploads each frame through `/upload/image`; the output already returns over WebSocket.
- Any models and custom nodes used by a workflow must exist in your ComfyUI installation.

## More Documentation

- `ComfyStreamWorkflow/README.md`
- `WebCamComfyStream/README.md`
