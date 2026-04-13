# WebCamComfyStream

Proyecto consola para procesar continuamente frames de webcam con un workflow de ComfyUI.

Uso:

```powershell
dotnet run --project WebCamComfyStream\WebCamComfyStream.csproj
```

El workflow, el indice de camara, la URL de ComfyUI, el workspace y las stats se configuran en `Program.cs`, en `StreamOptions`:

```csharp
private static readonly WebcamStreamHelper.Options StreamOptions = new(
    WorkflowPath: "WebCamCanny.json",
    CameraIndex: 0,
    ComfyUiBaseUrl: "http://localhost:8000/",
    ComfyUiWorkspacePath: null,
    ShowStats: false);
```

La ruta al workflow se busca primero desde el directorio actual y despues desde el directorio de salida del proyecto, asi que el uso por defecto funciona al ejecutar con `dotnet run --project`.

Si no indicas workspace, se usa `COMFYUI_WORKSPACE` o una instalacion valida de ComfyUI encontrada cerca del directorio actual o en la carpeta de aplicaciones del usuario.

El workflow debe tener un nodo `WebcamCapture`, `LoadImage` o `PrimaryInputLoadImage` para recibir el frame actual de la webcam. La salida puede ser `PreviewImage` o `SaveImage`; `ComfyStreamWorkflowRunner` la sustituye por `SaveImageWebsocket` para recibir la imagen procesada como bytes en memoria.

La primera version usa `/upload/image` para enviar el frame de entrada a ComfyUI. La salida ya evita el guardado en disco.
