using System.Diagnostics;
using System.Text.Json.Nodes;
using ComfyStreamWorkflow;
using OpenCvSharp;

namespace WebCamComfyStream
{
    internal class Program
    {
        private const string DefaultComfyUiWorkspacePath = @"C:\Users\jferrero\AppData\Local\Programs\ComfyUI";
        private const string WorkflowPath = "WebCamCanny.json";
        private const int CameraIndex = 0;
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
            string workflowPath = args.Length > 0 ? args[0] : WorkflowPath;
            int cameraIndex = args.Length > 1 && int.TryParse(args[1], out int parsedCameraIndex)
                ? parsedCameraIndex
                : CameraIndex;
            string comfyUiBaseUrl = args.Length > 2 ? args[2] : "http://localhost:8000/";
            bool showStats = args.Any(arg => string.Equals(arg, "--stats", StringComparison.OrdinalIgnoreCase));

            await using var comfyUI = new ComfyStreamWorkflowRunner(new ComfyStreamWorkflowOptions
            {
                BaseUri = new Uri(comfyUiBaseUrl),
                ComfyUiWorkspacePath = DefaultComfyUiWorkspacePath,
                PythonExecutable = "python",
                ExtraComfyUiArguments = new[] { "--disable-cuda-malloc" }
            });

            var workflow = ComfyWorkflowInMemoryTransformer.ReplaceFileOutputsWithWebSocketOutputs(
                await comfyUI.GetWorkflowAsync(workflowPath));

            using var camera = new VideoCapture(cameraIndex);
            if (!camera.IsOpened())
            {
                Console.WriteLine($"Could not open webcam with index {cameraIndex}.");
                return;
            }

            camera.Set(VideoCaptureProperties.FrameWidth, 640);
            camera.Set(VideoCaptureProperties.FrameHeight, 480);
            camera.Set(VideoCaptureProperties.Fps, 30);

            using var frame = new Mat();
            var fpsWindow = Stopwatch.StartNew();
            int processedFrames = 0;
            double encodeMilliseconds = 0;
            double uploadMilliseconds = 0;
            double executeMilliseconds = 0;
            double queueMilliseconds = 0;
            double waitMilliseconds = 0;
            double decodeMilliseconds = 0;

            Console.WriteLine("Press ESC in the preview window to stop.");
            Console.WriteLine($"Workflow: {workflowPath}");
            Console.WriteLine($"ComfyUI URL: {comfyUiBaseUrl}");
            Console.WriteLine($"Stats: {(showStats ? "on" : "off")} (pass --stats to enable)");

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
                executeMilliseconds += Stopwatch.GetElapsedTime(executeStart).TotalMilliseconds;
                queueMilliseconds += result.QueueDuration.TotalMilliseconds;
                waitMilliseconds += result.WaitDuration.TotalMilliseconds;

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
                decodeMilliseconds += Stopwatch.GetElapsedTime(decodeStart).TotalMilliseconds;

                int key = Cv2.WaitKey(1);
                UploadedFrame nextFrame = await nextFrameTask;
                if (key == 27)
                {
                    break;
                }

                encodeMilliseconds += nextFrame.EncodeMilliseconds;
                uploadMilliseconds += nextFrame.UploadMilliseconds;
                currentFrame = nextFrame;

                TimeSpan elapsed = Stopwatch.GetElapsedTime(frameStart);
                processedFrames++;
                if (showStats && fpsWindow.Elapsed >= TimeSpan.FromSeconds(2))
                {
                    double fps = processedFrames / fpsWindow.Elapsed.TotalSeconds;
                    Console.WriteLine($"FPS: {fps:0.0} | Last frame: {elapsed.TotalMilliseconds:0} ms");
                    Console.WriteLine(
                        $"Avg ms | encode: {encodeMilliseconds / processedFrames:0.0} | upload: {uploadMilliseconds / processedFrames:0.0} | comfy: {executeMilliseconds / processedFrames:0.0} | decode/show: {decodeMilliseconds / processedFrames:0.0}");
                    Console.WriteLine(
                        $"Comfy detail | queue: {queueMilliseconds / processedFrames:0.0} | wait: {waitMilliseconds / processedFrames:0.0}");
                    fpsWindow.Restart();
                    processedFrames = 0;
                    encodeMilliseconds = 0;
                    uploadMilliseconds = 0;
                    executeMilliseconds = 0;
                    queueMilliseconds = 0;
                    waitMilliseconds = 0;
                    decodeMilliseconds = 0;
                }
                else if (!showStats && fpsWindow.Elapsed >= TimeSpan.FromSeconds(2))
                {
                    fpsWindow.Restart();
                    processedFrames = 0;
                    encodeMilliseconds = 0;
                    uploadMilliseconds = 0;
                    executeMilliseconds = 0;
                    queueMilliseconds = 0;
                    waitMilliseconds = 0;
                    decodeMilliseconds = 0;
                }

                TimeSpan delay = FrameInterval - elapsed;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay);
                }
            }

            Cv2.DestroyAllWindows();
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
    }
}
