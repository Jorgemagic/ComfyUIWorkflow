using System.Diagnostics;
using ComfyStreamWorkflow;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

namespace WebCamComfyStream
{
    internal class Program
    {
        private const string InputImageFilenamePrefix = "webcam_frame_";
        private const string InputImageSubfolder = "webcam";
        private const string InputImageType = "temp";
        private const string InputImageContentType = "image/jpeg";
        private static readonly TimeSpan ProcessingInterval = TimeSpan.FromMilliseconds(33);

        private static readonly WebcamStreamHelper.Options StreamOptions = new(
            WorkflowPath: "WebcamFluxKlein2.json",
            CameraIndex: 0,
            ComfyUiBaseUrl: "http://localhost:8000/",
            ComfyUiWorkspacePath: null,
            ShowStats: false);

        private static readonly int[] InputImageEncodingParameters =
        {
            (int)ImwriteFlags.JpegQuality,
            90
        };

        static async Task Main()
        {
            WebcamStreamHelper.Options streamOptions = StreamOptions;
            ComfyStreamWorkflowOptions comfyOptions = CreateComfyOptions(streamOptions);

            await using var comfyUI = new ComfyStreamWorkflowRunner(comfyOptions);

            JObject workflow = await comfyUI.GetWorkflowAsync(streamOptions.WorkflowPath);
            WebcamStreamHelper.ApplyTargetResolution(workflow);
            workflow = ComfyWorkflowInMemoryTransformer.ReplaceFileOutputsWithWebSocketOutputs(
                workflow,
                WebcamStreamHelper.TargetWidth,
                WebcamStreamHelper.TargetHeight);
            WebcamStreamHelper.InputBinding webcamInput = WebcamStreamHelper.InputBinding.Find(workflow);

            using VideoCapture? camera = WebcamStreamHelper.TryOpenCamera(streamOptions.CameraIndex);
            if (camera is null)
            {
                Console.WriteLine($"Could not open webcam with index {streamOptions.CameraIndex}.");
                return;
            }

            using var latestFrame = new WebcamStreamHelper.LatestFrameBuffer();
            using var stopCts = new CancellationTokenSource();
            var stats = new WebcamStreamHelper.PerformanceStats();

            WriteStartupInfo(streamOptions, comfyOptions);

            Task processingTask = ProcessFramesAsync(
                comfyUI,
                workflow,
                webcamInput,
                camera,
                latestFrame,
                stats,
                streamOptions.ShowStats,
                stopCts.Token);

            await WebcamStreamHelper.RunDisplayLoopAsync(
                latestFrame,
                processingTask,
                onStopAsync: () => RequestStopAsync(comfyUI, stopCts),
                stopCts.Token);

            if (!processingTask.IsCompleted)
            {
                await RequestStopAsync(comfyUI, stopCts);
            }
            else if (!stopCts.IsCancellationRequested)
            {
                stopCts.Cancel();
            }

            await WebcamStreamHelper.WaitForProcessingToStopAsync(processingTask, stopCts.Token);
        }

        private static ComfyStreamWorkflowOptions CreateComfyOptions(WebcamStreamHelper.Options streamOptions)
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

        private static void WriteStartupInfo(
            WebcamStreamHelper.Options streamOptions,
            ComfyStreamWorkflowOptions comfyOptions)
        {
            Console.WriteLine("Press ESC in the preview window or terminal to stop.");
            Console.WriteLine($"Workflow: {streamOptions.WorkflowPath}");
            Console.WriteLine($"Stream resolution: {WebcamStreamHelper.TargetWidth}x{WebcamStreamHelper.TargetHeight}");
            Console.WriteLine($"ComfyUI URL: {streamOptions.ComfyUiBaseUrl}");
            Console.WriteLine($"ComfyUI workspace: {comfyOptions.ComfyUiWorkspacePath ?? "not provided"}");
            Console.WriteLine($"Stats: {(streamOptions.ShowStats ? "on" : "off")}");
        }

        private static async Task RequestStopAsync(
            ComfyStreamWorkflowRunner comfyUI,
            CancellationTokenSource stopCts)
        {
            if (!stopCts.IsCancellationRequested)
            {
                stopCts.Cancel();
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try
            {
                await comfyUI.InterruptAsync(timeout.Token);
            }
            catch (Exception ex) when (
                ex is OperationCanceledException
                || ex is HttpRequestException
                || ex is InvalidOperationException)
            {
            }

            try
            {
                await comfyUI.KillStartedComfyUiAsync();
            }
            catch (Exception ex) when (
                ex is InvalidOperationException
                || ex is System.ComponentModel.Win32Exception)
            {
            }
        }

        private static async Task ProcessFramesAsync(
            ComfyStreamWorkflowRunner comfyUI,
            JObject workflow,
            WebcamStreamHelper.InputBinding webcamInput,
            VideoCapture camera,
            WebcamStreamHelper.LatestFrameBuffer latestFrame,
            WebcamStreamHelper.PerformanceStats stats,
            bool showStats,
            CancellationToken cancellationToken)
        {
            using var frame = new Mat();
            UploadedFrame currentFrame = await CaptureAndUploadFrameAsync(
                comfyUI,
                camera,
                frame,
                frameIndex: 0,
                cancellationToken);
            int frameIndex = 1;

            while (!cancellationToken.IsCancellationRequested)
            {
                var frameStart = Stopwatch.GetTimestamp();

                webcamInput.Set(currentFrame.Image.LoadImagePath, currentFrame.Width, currentFrame.Height);

                var executeStart = Stopwatch.GetTimestamp();
                Task<ComfyStreamWorkflowResult> executeTask = comfyUI.ExecutePreparedWorkflowAndWaitAsync(
                    workflow,
                    cancellationToken: cancellationToken);
                Task<UploadedFrame> nextFrameTask = CaptureAndUploadFrameAsync(
                    comfyUI,
                    camera,
                    frame,
                    frameIndex++,
                    cancellationToken);

                ComfyStreamWorkflowResult result;
                try
                {
                    result = await executeTask;
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await nextFrameTask;
                    }
                    catch
                    {
                    }

                    return;
                }
                catch
                {
                    try
                    {
                        await nextFrameTask;
                    }
                    catch
                    {
                    }

                    throw;
                }

                ComfyGeneratedImage? outputImage = result.Images.LastOrDefault();
                stats.AddExecution(Stopwatch.GetElapsedTime(executeStart), result);

                var decodeStart = Stopwatch.GetTimestamp();
                if (outputImage is not null)
                {
                    using var processedFrame = Cv2.ImDecode(outputImage.Bytes, ImreadModes.Color);
                    latestFrame.Update(processedFrame);
                }
                stats.AddDecode(Stopwatch.GetElapsedTime(decodeStart));

                UploadedFrame nextFrame = await nextFrameTask;
                stats.AddUpload(nextFrame.EncodeMilliseconds, nextFrame.UploadMilliseconds);
                currentFrame = nextFrame;

                TimeSpan elapsed = Stopwatch.GetElapsedTime(frameStart);
                stats.CompleteFrame(elapsed, showStats);

                TimeSpan delay = ProcessingInterval - elapsed;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }

        private static async Task<UploadedFrame> CaptureAndUploadFrameAsync(
            ComfyStreamWorkflowRunner comfyUI,
            VideoCapture camera,
            Mat frame,
            int frameIndex,
            CancellationToken cancellationToken)
        {
            WebcamStreamHelper.CapturedFrame capturedFrame = WebcamStreamHelper.CaptureJpeg(
                camera,
                frame,
                InputImageEncodingParameters);

            string filename = $"{InputImageFilenamePrefix}{frameIndex % 2}.jpg";

            var uploadStart = Stopwatch.GetTimestamp();
            ComfyUploadedImage image = await comfyUI.UploadImageAsync(
                capturedFrame.Bytes,
                filename: filename,
                subfolder: InputImageSubfolder,
                type: InputImageType,
                overwrite: true,
                contentType: InputImageContentType,
                cancellationToken: cancellationToken);
            double uploadMilliseconds = Stopwatch.GetElapsedTime(uploadStart).TotalMilliseconds;

            return new UploadedFrame(
                image,
                capturedFrame.Width,
                capturedFrame.Height,
                capturedFrame.EncodeMilliseconds,
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
