using System.Diagnostics;
using ComfyStreamWorkflow;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

namespace WebCamComfyStream;

internal static class WebcamStreamHelper
{
    private static readonly TimeSpan DisplayFrameInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan StatsReportingInterval = TimeSpan.FromSeconds(2);

    public static VideoCapture? TryOpenCamera(int cameraIndex)
    {
        var camera = new VideoCapture(cameraIndex);
        if (!camera.IsOpened())
        {
            camera.Dispose();
            return null;
        }

        camera.Set(VideoCaptureProperties.FrameWidth, 640);
        camera.Set(VideoCaptureProperties.FrameHeight, 480);
        camera.Set(VideoCaptureProperties.Fps, 30);

        return camera;
    }

    public static CapturedFrame CaptureJpeg(
        VideoCapture camera,
        Mat frame,
        int[] encodingParameters)
    {
        if (!camera.Read(frame) || frame.Empty())
        {
            throw new InvalidOperationException("No frame was captured from the webcam.");
        }

        var encodeStart = Stopwatch.GetTimestamp();
        Cv2.ImEncode(".jpg", frame, out byte[] frameBytes, encodingParameters);
        double encodeMilliseconds = Stopwatch.GetElapsedTime(encodeStart).TotalMilliseconds;

        return new CapturedFrame(
            frameBytes,
            frame.Width,
            frame.Height,
            encodeMilliseconds);
    }

    public static async Task RunDisplayLoopAsync(
        LatestFrameBuffer latestFrame,
        Task processingTask,
        Func<Task> onStopAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!processingTask.IsCompleted)
            {
                var displayStart = Stopwatch.GetTimestamp();

                using Mat? frameToShow = latestFrame.GetSnapshot();
                if (frameToShow is not null)
                {
                    Cv2.ImShow("WebCamComfyStream", frameToShow);
                }

                if (ShouldStop())
                {
                    await onStopAsync();
                    break;
                }

                TimeSpan elapsed = Stopwatch.GetElapsedTime(displayStart);
                TimeSpan delay = DisplayFrameInterval - elapsed;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            Cv2.DestroyAllWindows();
        }
    }

    public static async Task WaitForProcessingToStopAsync(
        Task processingTask,
        CancellationToken cancellationToken)
    {
        Task completedTask = await Task.WhenAny(
            processingTask,
            Task.Delay(TimeSpan.FromSeconds(3)));

        if (completedTask != processingTask)
        {
            return;
        }

        try
        {
            await processingTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static bool ShouldStop()
    {
        return Cv2.WaitKey(1) == 27 || ConsoleEscapeWasPressed();
    }

    private static bool ConsoleEscapeWasPressed()
    {
        try
        {
            while (Console.KeyAvailable)
            {
                if (Console.ReadKey(intercept: true).Key == ConsoleKey.Escape)
                {
                    return true;
                }
            }
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return false;
    }

    public sealed record Options(
        string WorkflowPath,
        int CameraIndex,
        string ComfyUiBaseUrl,
        string? ComfyUiWorkspacePath,
        bool ShowStats)
    {
        private const string DefaultWorkflowPath = "WebCamCanny.json";
        private const int DefaultCameraIndex = 0;
        private const string DefaultComfyUiBaseUrl = "http://localhost:8000/";

        public static Options Parse(string[] args)
        {
            string[] positionals = args
                .Where(arg => !arg.StartsWith("--", StringComparison.Ordinal))
                .ToArray();

            int cameraIndex = positionals.Length > 1 && int.TryParse(positionals[1], out int parsedCameraIndex)
                ? parsedCameraIndex
                : DefaultCameraIndex;

            return new Options(
                WorkflowPath: positionals.Length > 0 ? positionals[0] : DefaultWorkflowPath,
                CameraIndex: cameraIndex,
                ComfyUiBaseUrl: positionals.Length > 2 ? positionals[2] : DefaultComfyUiBaseUrl,
                ComfyUiWorkspacePath: positionals.Length > 3 ? positionals[3] : null,
                ShowStats: args.Any(arg => string.Equals(arg, "--stats", StringComparison.OrdinalIgnoreCase)));
        }
    }

    public sealed record InputBinding(JObject Inputs, bool HasCaptureSettings)
    {
        public static InputBinding Find(JObject workflow)
        {
            foreach (JProperty property in workflow.Properties())
            {
                if (property.Value is not JObject nodeObject)
                {
                    continue;
                }

                string? classType = nodeObject["class_type"]?.Value<string>();
                if (!IsWebcamInputNode(classType))
                {
                    continue;
                }

                if (nodeObject["inputs"] is not JObject inputs)
                {
                    inputs = new JObject();
                    nodeObject["inputs"] = inputs;
                }

                return new InputBinding(
                    inputs,
                    HasCaptureSettings: string.Equals(classType, "WebcamCapture", StringComparison.Ordinal));
            }

            throw new InvalidOperationException("The workflow must contain a WebcamCapture, LoadImage or PrimaryInputLoadImage node for the webcam frame.");
        }

        public void Set(string imagePath, int width, int height)
        {
            Inputs["image"] = imagePath;
            if (!HasCaptureSettings)
            {
                return;
            }

            Inputs["width"] = width;
            Inputs["height"] = height;
            Inputs["capture_on_queue"] = false;
        }

        private static bool IsWebcamInputNode(string? classType)
        {
            return string.Equals(classType, "LoadImage", StringComparison.Ordinal)
                || string.Equals(classType, "PrimaryInputLoadImage", StringComparison.Ordinal)
                || string.Equals(classType, "WebcamCapture", StringComparison.Ordinal);
        }
    }

    public sealed record CapturedFrame(
        byte[] Bytes,
        int Width,
        int Height,
        double EncodeMilliseconds);

    public sealed class LatestFrameBuffer : IDisposable
    {
        private readonly object syncRoot = new();
        private Mat? latestFrame;

        public void Update(Mat frame)
        {
            lock (syncRoot)
            {
                latestFrame?.Dispose();
                latestFrame = frame.Clone();
            }
        }

        public Mat? GetSnapshot()
        {
            lock (syncRoot)
            {
                return latestFrame?.Clone();
            }
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                latestFrame?.Dispose();
                latestFrame = null;
            }
        }
    }

    public sealed class PerformanceStats
    {
        private readonly Stopwatch window = Stopwatch.StartNew();
        private int processedFrames;
        private double encodeMilliseconds;
        private double uploadMilliseconds;
        private double executeMilliseconds;
        private double queueMilliseconds;
        private double waitMilliseconds;
        private double decodeMilliseconds;

        public void AddUpload(double encodeMilliseconds, double uploadMilliseconds)
        {
            this.encodeMilliseconds += encodeMilliseconds;
            this.uploadMilliseconds += uploadMilliseconds;
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

            if (window.Elapsed < StatsReportingInterval)
            {
                return;
            }

            if (showStats && processedFrames > 0)
            {
                double fps = processedFrames / window.Elapsed.TotalSeconds;
                Console.WriteLine($"FPS: {fps:0.0} | Last frame: {elapsed.TotalMilliseconds:0} ms");
                Console.WriteLine(
                    $"Avg ms | encode: {encodeMilliseconds / processedFrames:0.0} | upload: {uploadMilliseconds / processedFrames:0.0} | comfy: {executeMilliseconds / processedFrames:0.0} | decode: {decodeMilliseconds / processedFrames:0.0}");
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
