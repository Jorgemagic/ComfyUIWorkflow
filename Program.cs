using ComfyUIWorkflowRun.API;

namespace ComfyUIWorkflowRun
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            var comfyUI = new ComfyUIAPI();

            var workflow = await comfyUI.GetWorkflowAsync("Text2Img.json");
            workflow["14"].Inputs["text"] =
                "A highly realistic astronaut cat floating in outer space, wearing a detailed NASA-style astronaut suit with reflective helmet visor, ultra-realistic fur texture visible inside the helmet, cinematic lighting, Earth visible in the background, stars and nebulae surrounding the scene, photorealistic, ultra-detailed, 8k resolution, depth of field, dramatic lighting, space photography style, sharp focus, professional photography, realistic reflections on the helmet";

            var promptResponse = await comfyUI.ExecuteWorkflowAndWaitAsync(workflow);

            var history = await comfyUI.GetHistoryAsync(promptResponse.PromptId);
            var entry = history[promptResponse.PromptId];

            var firstImage = entry.Outputs.Values.SelectMany(o => o.Images).FirstOrDefault();

            if (firstImage == null)
            {
                Console.WriteLine("No images found in the workflow output.");
                return;
            }

            string outputPath = Path.Combine(AppContext.BaseDirectory, "result.png");
            await comfyUI.DownloadImageAsync(firstImage.Filename, firstImage.Subfolder, firstImage.Type, outputPath);

            Console.WriteLine($"Image saved to: {outputPath}");
        }
    }
}