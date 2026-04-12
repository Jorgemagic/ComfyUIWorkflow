namespace ComfyStreamWorkflow
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            string comfyUiBaseUrl = args.Length > 1 ? args[1] : "http://localhost:8000/";
            string? requestedWorkspacePath = args.Length > 0 ? args[0] : null;
            ComfyStreamWorkflowOptions options = CreateOptions(comfyUiBaseUrl, requestedWorkspacePath);

            try
            {
                await using var comfyUI = new ComfyStreamWorkflowRunner(options);

                Console.WriteLine($"ComfyUI URL: {comfyUiBaseUrl}");
                Console.WriteLine($"ComfyUI workspace: {options.ComfyUiWorkspacePath ?? "not provided"}");

                var workflow = await comfyUI.GetWorkflowAsync("Text2Img.json");
                workflow["14"]!.AsObject()["inputs"]!.AsObject()["text"] =
                    "A highly realistic astronaut cat floating in outer space, wearing a detailed NASA-style astronaut suit with reflective helmet visor, ultra-realistic fur texture visible inside the helmet, cinematic lighting, Earth visible in the background, stars and nebulae surrounding the scene, photorealistic, ultra-detailed, 8k resolution, depth of field, dramatic lighting, space photography style, sharp focus, professional photography, realistic reflections on the helmet";

                var promptResponse = await comfyUI.ExecuteWorkflowAndWaitAsync(workflow);

                var firstImage = promptResponse.Images.FirstOrDefault();

                if (firstImage is null)
                {
                    Console.WriteLine("No images found in the workflow output.");
                    return;
                }

                string outputPath = Path.Combine(AppContext.BaseDirectory, "result-in-memory.png");
                await comfyUI.SaveImageAsync(firstImage, outputPath);

                Console.WriteLine($"Image received in memory and saved to: {outputPath}");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("ComfyUI is not reachable", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("ComfyUI no esta arrancado y no se ha indicado una ruta valida al workspace de ComfyUI.");
                Console.WriteLine("Ejecutalo pasando la ruta del workspace de ComfyUI, por ejemplo:");
                Console.WriteLine(@"dotnet run --project ComfyStreamWorkflow\ComfyStreamWorkflow.csproj -- ""D:\Ruta\A\ComfyUI""");
                Console.WriteLine("Tambien puedes configurar la variable de entorno COMFYUI_WORKSPACE.");
                Environment.ExitCode = 1;
            }
        }

        private static ComfyStreamWorkflowOptions CreateOptions(string comfyUiBaseUrl, string? requestedWorkspacePath)
        {
            var launchOptions = new ComfyStreamWorkflowOptions
            {
                BaseUri = new Uri(comfyUiBaseUrl),
                PythonExecutable = "python",
                ExtraComfyUiArguments = new[] { "--disable-cuda-malloc" }
            };

            return new ComfyStreamWorkflowOptions
            {
                BaseUri = launchOptions.BaseUri,
                ComfyUiWorkspacePath = ComfyUiWorkspaceResolver.Resolve(requestedWorkspacePath, launchOptions),
                PythonExecutable = launchOptions.PythonExecutable,
                ExtraComfyUiArguments = launchOptions.ExtraComfyUiArguments
            };
        }
    }
}
