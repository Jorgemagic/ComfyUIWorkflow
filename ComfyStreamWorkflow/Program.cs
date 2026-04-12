namespace ComfyStreamWorkflow
{
    internal class Program
    {
        private const string DefaultComfyUiWorkspacePath = @"C:\Users\jferrero\AppData\Local\Programs\ComfyUI";

        static async Task Main(string[] args)
        {
            string comfyUiBaseUrl = args.Length > 1 ? args[1] : "http://localhost:8000/";
            string? comfyUiWorkspacePath = ResolveComfyUiWorkspacePath(args);

            try
            {
                await using var comfyUI = new ComfyStreamWorkflowRunner(new ComfyStreamWorkflowOptions
                {
                    BaseUri = new Uri(comfyUiBaseUrl),
                    ComfyUiWorkspacePath = comfyUiWorkspacePath,
                    PythonExecutable = "python",
                    ExtraComfyUiArguments = new[] { "--disable-cuda-malloc" }
                });

                Console.WriteLine($"ComfyUI URL: {comfyUiBaseUrl}");
                Console.WriteLine($"ComfyUI workspace: {comfyUiWorkspacePath ?? "not provided"}");

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

        private static string? ResolveComfyUiWorkspacePath(string[] args)
        {
            if (args.Length > 0 && IsComfyUiPath(args[0]))
            {
                return Path.GetFullPath(args[0]);
            }

            string? environmentWorkspace = Environment.GetEnvironmentVariable("COMFYUI_WORKSPACE");
            if (!string.IsNullOrWhiteSpace(environmentWorkspace) && IsComfyUiPath(environmentWorkspace))
            {
                return Path.GetFullPath(environmentWorkspace);
            }

            if (IsComfyUiPath(DefaultComfyUiWorkspacePath))
            {
                return Path.GetFullPath(DefaultComfyUiWorkspacePath);
            }

            return GetComfyUiWorkspaceCandidates().FirstOrDefault(IsComfyUiPath);
        }

        private static IEnumerable<string> GetComfyUiWorkspaceCandidates()
        {
            string currentDirectory = Environment.CurrentDirectory;

            for (var directory = new DirectoryInfo(currentDirectory); directory != null; directory = directory.Parent)
            {
                yield return Path.Combine(directory.FullName, "ComfyUI");
                yield return Path.Combine(directory.FullName, "comfyui");
            }

            string? userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                yield return Path.Combine(userProfile, "ComfyUI");
                yield return Path.Combine(userProfile, "comfyui");
            }
        }

        private static bool IsComfyUiPath(string path)
        {
            return ComfyUiLaunchInfo.TryCreate(path, new ComfyStreamWorkflowOptions(), out _);
        }
    }
}
