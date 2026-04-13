using System.Diagnostics;
using ComfyStreamWorkflow;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

namespace WebCamComfyStream;

internal static class WebcamStreamHelper
{
    public const int TargetWidth = 640;
    public const int TargetHeight = 480;
    private static readonly TimeSpan DisplayFrameInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan StatsReportingInterval = TimeSpan.FromSeconds(2);

    public static void ApplyTargetResolution(JObject workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        foreach (JProperty property in workflow.Properties())
        {
            if (property.Value is not JObject nodeObject || nodeObject["inputs"] is not JObject inputs)
            {
                continue;
            }

            SetScalarInput(inputs, "width", TargetWidth);
            SetScalarInput(inputs, "height", TargetHeight);
            SetScalarInput(inputs, "resolution", Math.Max(TargetWidth, TargetHeight));
            SetScalarInput(inputs, "megapixels", TargetWidth * TargetHeight / 1_000_000d);
        }
    }

    public static VideoCapture? TryOpenCamera(int cameraIndex)
    {
        var camera = new VideoCapture(cameraIndex);
        if (!camera.IsOpened())
        {
            camera.Dispose();
            return null;
        }

        camera.Set(VideoCaptureProperties.FrameWidth, TargetWidth);
        camera.Set(VideoCaptureProperties.FrameHeight, TargetHeight);
        camera.Set(VideoCaptureProperties.Fps, 30);

        return camera;
    }

    public static CapturedFrame CaptureJpeg(
        VideoCapture camera,
        Mat frame,
        Mat resizedFrame,
        int[] encodingParameters)
    {
        if (!camera.Read(frame) || frame.Empty())
        {
            throw new InvalidOperationException("No frame was captured from the webcam.");
        }

        var encodeStart = Stopwatch.GetTimestamp();
        Mat frameToEncode = ResizeToTargetIfNeeded(frame, resizedFrame);

        Cv2.ImEncode(".jpg", frameToEncode, out byte[] frameBytes, encodingParameters);
        double encodeMilliseconds = Stopwatch.GetElapsedTime(encodeStart).TotalMilliseconds;

        return new CapturedFrame(
            frameBytes,
            frameToEncode.Width,
            frameToEncode.Height,
            encodeMilliseconds);
    }

    private static void SetScalarInput(JObject inputs, string inputName, int value)
    {
        if (!inputs.TryGetValue(inputName, out JToken? currentValue) || currentValue is JArray)
        {
            return;
        }

        inputs[inputName] = value;
    }

    private static void SetScalarInput(JObject inputs, string inputName, double value)
    {
        if (!inputs.TryGetValue(inputName, out JToken? currentValue) || currentValue is JArray)
        {
            return;
        }

        inputs[inputName] = value;
    }

    private static Mat ResizeToTargetIfNeeded(Mat frame, Mat resizedFrame)
    {
        if (frame.Width == TargetWidth && frame.Height == TargetHeight)
        {
            return frame;
        }

        Cv2.Resize(
            frame,
            resizedFrame,
            new Size(TargetWidth, TargetHeight),
            fx: 0,
            fy: 0,
            interpolation: InterpolationFlags.Area);

        return resizedFrame;
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

                latestFrame.Show("WebCamComfyStream");

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

    public readonly record struct Options(
        string WorkflowPath,
        int CameraIndex,
        string ComfyUiBaseUrl,
        string? ComfyUiWorkspacePath,
        bool ShowStats);

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

        public void UpdateOwnedFrame(Mat frame)
        {
            lock (syncRoot)
            {
                latestFrame?.Dispose();
                latestFrame = frame;
            }
        }

        public void Show(string windowName)
        {
            lock (syncRoot)
            {
                if (latestFrame is not null)
                {
                    Cv2.ImShow(windowName, latestFrame);
                }
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
        private double inputAgeMilliseconds;

        public void AddUpload(
            double encodeMilliseconds,
            double uploadMilliseconds,
            TimeSpan inputAge)
        {
            this.encodeMilliseconds += encodeMilliseconds;
            this.uploadMilliseconds += uploadMilliseconds;
            inputAgeMilliseconds += inputAge.TotalMilliseconds;
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
                    $"Avg ms | age: {inputAgeMilliseconds / processedFrames:0.0} | encode: {encodeMilliseconds / processedFrames:0.0} | upload: {uploadMilliseconds / processedFrames:0.0} | comfy: {executeMilliseconds / processedFrames:0.0} | decode: {decodeMilliseconds / processedFrames:0.0}");
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
            inputAgeMilliseconds = 0;
        }
    }
}
