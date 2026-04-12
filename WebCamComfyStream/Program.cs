using System.Diagnostics;
using System.Text.Json.Nodes;
using ComfyStreamWorkflow;
using OpenCvSharp;

namespace WebCamComfyStream
{
    internal class Program
    {
        private const string DefaultWorkflowPath = "WebCamCanny.json";
        private const int DefaultCameraIndex = 0;
        private const string DefaultComfyUiBaseUrl = "http://localhost:8000/";
        private const string InputImageFilenamePrefix = "webcam_frame_";
        private const string InputImageSubfolder = "webcam";
        private const string InputImageType = "temp";
        private const string InputImageContentType = "image/jpeg";
        private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(33);
        private static readonly int[] InputImageEncodingParameters =
        {
            (int)ImwriteFlags.JpegQuality,
            90
        };

        static async Task Main(string[] args)
        {
            WebcamStreamOptions streamOptions = WebcamStreamOptions.Parse(args);
            ComfyStreamWorkflowOptions comfyOptions = CreateComfyOptions(streamOptions);

            await using var comfyUI = new ComfyStreamWorkflowRunner(comfyOptions);

            var workflow = ComfyWorkflowInMemoryTransformer.ReplaceFileOutputsWithWebSocketOutputs(
                await comfyUI.GetWorkflowAsync(streamOptions.WorkflowPath));

            using var camera = new VideoCapture(streamOptions.CameraIndex);
            if (!camera.IsOpened())
            {
                Console.WriteLine($"Could not open webcam with index {streamOptions.CameraIndex}.");
                return;
            }

            camera.Set(VideoCaptureProperties.FrameWidth, 640);
            camera.Set(VideoCaptureProperties.FrameHeight, 480);
            camera.Set(VideoCaptureProperties.Fps, 30);

            using var frame = new Mat();
            var stats = new PerformanceStats();

            Console.WriteLine("Press ESC in the preview window to stop.");
            Console.WriteLine($"Workflow: {streamOptions.WorkflowPath}");
            Console.WriteLine($"ComfyUI URL: {streamOptions.ComfyUiBaseUrl}");
            Console.WriteLine($"ComfyUI workspace: {comfyOptions.ComfyUiWorkspacePath ?? "not provided"}");
            Console.WriteLine($"Stats: {(streamOptions.ShowStats ? "on" : "off")} (pass --stats to enable)");

            UploadedFrame currentFrame = await CaptureAndUploadFrameAsync(comfyUI, camera, frame, frameIndex: 0);
            int frameIndex = 1;

            while (true)
            {
                var frameStart = Stopwatch.GetTimestamp();

                JsonObject frameWorkflow = workflow.DeepClone().AsObject();
                SetWebcamInput(frameWorkflow, currentFrame.Image.LoadImagePath, currentFrame.Width, currentFrame.Height);

                var executeStart = Stopwatch.GetTimestamp();
                Task<ComfyStreamWorkflowResult> executeTask = comfyUI.ExecutePreparedWorkflowAndWaitAsync(frameWorkflow);
                Task<UploadedFrame> nextFrameTask = CaptureAndUploadFrameAsync(comfyUI, camera, frame, frameIndex++);

                ComfyStreamWorkflowResult result = await executeTask;
                ComfyGeneratedImage? outputImage = result.Images.LastOrDefault();
                stats.AddExecution(Stopwatch.GetElapsedTime(executeStart), result);

                var decodeStart = Stopwatch.GetTimestamp();
                if (outputImage is not null)
                {
                    using var processedFrame = Cv2.ImDecode(outputImage.Bytes, ImreadModes.Color);
                    Cv2.ImShow("WebCamComfyStream", processedFrame);
                }
                else
                {
                    Cv2.ImShow("WebCamComfyStream", frame);
                }
                stats.AddDecode(Stopwatch.GetElapsedTime(decodeStart));

                int key = Cv2.WaitKey(1);
                UploadedFrame nextFrame = await nextFrameTask;
                if (key == 27)
                {
                    break;
                }

                stats.AddUpload(nextFrame);
                currentFrame = nextFrame;

                TimeSpan elapsed = Stopwatch.GetElapsedTime(frameStart);
                stats.CompleteFrame(elapsed, streamOptions.ShowStats);

                TimeSpan delay = FrameInterval - elapsed;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay);
                }
            }

            Cv2.DestroyAllWindows();
        }

        private static ComfyStreamWorkflowOptions CreateComfyOptions(WebcamStreamOptions streamOptions)
        {
            var launchOptions = new ComfyStreamWorkflowOptions
            {
                BaseUri = new Uri(streamOptions.ComfyUiBaseUrl),
                PythonExecutable = "python",
                ExtraComfyUiArguments = new[] { "--disable-cuda-malloc" }
            };

            return new ComfyStreamWorkflowOptions
            {
                BaseUri = launchOptions.BaseUri,
                ComfyUiWorkspacePath = ComfyUiWorkspaceResolver.Resolve(streamOptions.ComfyUiWorkspacePath, launchOptions),
                PythonExecutable = launchOptions.PythonExecutable,
                ExtraComfyUiArguments = launchOptions.ExtraComfyUiArguments
            };
        }

        private static void SetWebcamInput(JsonObject workflow, string imagePath, int width, int height)
        {
            foreach (var (_, node) in workflow)
            {
                if (node is not JsonObject nodeObject)
                {
                    continue;
                }

                string? classType = nodeObject["class_type"]?.GetValue<string>();
                if (!string.Equals(classType, "LoadImage", StringComparison.Ordinal)
                    && !string.Equals(classType, "PrimaryInputLoadImage", StringComparison.Ordinal)
                    && !string.Equals(classType, "WebcamCapture", StringComparison.Ordinal))
                {
                    continue;
                }

                if (nodeObject["inputs"] is not JsonObject inputs)
                {
                    inputs = new JsonObject();
                    nodeObject["inputs"] = inputs;
                }

                inputs["image"] = imagePath;
                if (string.Equals(classType, "WebcamCapture", StringComparison.Ordinal))
                {
                    inputs["width"] = width;
                    inputs["height"] = height;
                    inputs["capture_on_queue"] = false;
                }

                return;
            }

            throw new InvalidOperationException("The workflow must contain a WebcamCapture, LoadImage or PrimaryInputLoadImage node for the webcam frame.");
        }

        private static async Task<UploadedFrame> CaptureAndUploadFrameAsync(
            ComfyStreamWorkflowRunner comfyUI,
            VideoCapture camera,
            Mat frame,
            int frameIndex)
        {
            if (!camera.Read(frame) || frame.Empty())
            {
                throw new InvalidOperationException("No frame was captured from the webcam.");
            }

            var encodeStart = Stopwatch.GetTimestamp();
            Cv2.ImEncode(".jpg", frame, out byte[] frameBytes, InputImageEncodingParameters);
            double encodeMilliseconds = Stopwatch.GetElapsedTime(encodeStart).TotalMilliseconds;

            string filename = $"{InputImageFilenamePrefix}{frameIndex % 2}.jpg";

            var uploadStart = Stopwatch.GetTimestamp();
            ComfyUploadedImage image = await comfyUI.UploadImageAsync(
                frameBytes,
                filename: filename,
                subfolder: InputImageSubfolder,
                type: InputImageType,
                overwrite: true,
                contentType: InputImageContentType);
            double uploadMilliseconds = Stopwatch.GetElapsedTime(uploadStart).TotalMilliseconds;

            return new UploadedFrame(
                image,
                frame.Width,
                frame.Height,
                encodeMilliseconds,
                uploadMilliseconds);
        }

        private sealed record UploadedFrame(
            ComfyUploadedImage Image,
            int Width,
            int Height,
            double EncodeMilliseconds,
            double UploadMilliseconds);

        private sealed record WebcamStreamOptions(
            string WorkflowPath,
            int CameraIndex,
            string ComfyUiBaseUrl,
            string? ComfyUiWorkspacePath,
            bool ShowStats)
        {
            public static WebcamStreamOptions Parse(string[] args)
            {
                string[] positionals = args
                    .Where(arg => !arg.StartsWith("--", StringComparison.Ordinal))
                    .ToArray();

                int cameraIndex = positionals.Length > 1 && int.TryParse(positionals[1], out int parsedCameraIndex)
                    ? parsedCameraIndex
                    : DefaultCameraIndex;

                return new WebcamStreamOptions(
                    WorkflowPath: positionals.Length > 0 ? positionals[0] : DefaultWorkflowPath,
                    CameraIndex: cameraIndex,
                    ComfyUiBaseUrl: positionals.Length > 2 ? positionals[2] : DefaultComfyUiBaseUrl,
                    ComfyUiWorkspacePath: positionals.Length > 3 ? positionals[3] : null,
                    ShowStats: args.Any(arg => string.Equals(arg, "--stats", StringComparison.OrdinalIgnoreCase)));
            }
        }

        private sealed class PerformanceStats
        {
            private static readonly TimeSpan ReportingInterval = TimeSpan.FromSeconds(2);

            private readonly Stopwatch window = Stopwatch.StartNew();
            private int processedFrames;
            private double encodeMilliseconds;
            private double uploadMilliseconds;
            private double executeMilliseconds;
            private double queueMilliseconds;
            private double waitMilliseconds;
            private double decodeMilliseconds;

            public void AddUpload(UploadedFrame frame)
            {
                encodeMilliseconds += frame.EncodeMilliseconds;
                uploadMilliseconds += frame.UploadMilliseconds;
            }

            public void AddExecution(TimeSpan elapsed, ComfyStreamWorkflowResult result)
            {
                executeMilliseconds += elapsed.TotalMilliseconds;
                queueMilliseconds += result.QueueDuration.TotalMilliseconds;
                waitMilliseconds += result.WaitDuration.TotalMilliseconds;
            }

            public void AddDecode(TimeSpan elapsed)
            {
                decodeMilliseconds += elapsed.TotalMilliseconds;
            }

            public void CompleteFrame(TimeSpan elapsed, bool showStats)
            {
                processedFrames++;

                if (window.Elapsed < ReportingInterval)
                {
                    return;
                }

                if (showStats && processedFrames > 0)
                {
                    double fps = processedFrames / window.Elapsed.TotalSeconds;
                    Console.WriteLine($"FPS: {fps:0.0} | Last frame: {elapsed.TotalMilliseconds:0} ms");
                    Console.WriteLine(
                        $"Avg ms | encode: {encodeMilliseconds / processedFrames:0.0} | upload: {uploadMilliseconds / processedFrames:0.0} | comfy: {executeMilliseconds / processedFrames:0.0} | decode/show: {decodeMilliseconds / processedFrames:0.0}");
                    Console.WriteLine(
                        $"Comfy detail | queue: {queueMilliseconds / processedFrames:0.0} | wait: {waitMilliseconds / processedFrames:0.0}");
                }

                Reset();
            }

            private void Reset()
            {
                window.Restart();
                processedFrames = 0;
                encodeMilliseconds = 0;
                uploadMilliseconds = 0;
                executeMilliseconds = 0;
                queueMilliseconds = 0;
                waitMilliseconds = 0;
                decodeMilliseconds = 0;
            }
        }
    }
}
