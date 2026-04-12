# ComfyStreamWorkflow

Cliente C# para ejecutar workflows de ComfyUI recogiendo las imagenes en memoria.

Este proyecto no reimplementa ComfyUI en C#. Usa ComfyUI como motor Python, pero puede arrancar su backend sin abrir la UI si se le pasa una instalacion valida. Soporta tanto un workspace Python con `main.py` como la instalacion de escritorio de ComfyUI, usando su entorno `.venv`.

Antes de enviar el workflow, sustituye los nodos `SaveImage` y `PreviewImage` por `SaveImageWebsocket`, de modo que la imagen resultante llega por WebSocket como bytes PNG y no se descarga desde `/view` ni se guarda en disco.

Ejemplo:

```csharp
await using var runner = new ComfyStreamWorkflowRunner(new ComfyStreamWorkflowOptions
{
    BaseUri = new Uri("http://localhost:8000/"),
    ComfyUiWorkspacePath = @"C:\Users\jferrero\AppData\Local\Programs\ComfyUI",
    PythonExecutable = "python",
    ExtraComfyUiArguments = new[] { "--disable-cuda-malloc" }
});

ComfyStreamWorkflowResult result = await runner.ExecuteWorkflowAsync("Text2Img.json");

byte[] pngBytes = result.Images[0].Bytes;
```

Si ComfyUI ya esta levantado en `BaseUri`, el runner lo reutiliza. Si no lo esta y `ComfyUiWorkspacePath` apunta a una instalacion valida de ComfyUI, lo arranca como proceso hijo.
