# WebCamComfyStream

Proyecto consola para procesar continuamente frames de webcam con un workflow de ComfyUI.

Uso:

```powershell
dotnet run --project WebCamComfyStream\WebCamComfyStream.csproj
```

Tambien puedes indicar workflow, indice de camara y URL de ComfyUI:

```powershell
dotnet run --project WebCamComfyStream\WebCamComfyStream.csproj -- "WebcamFluxKlein.json" 0 "http://localhost:8000/"
```

El workflow debe tener un nodo `WebcamCapture`, `LoadImage` o `PrimaryInputLoadImage` para recibir el frame actual de la webcam. La salida puede ser `PreviewImage` o `SaveImage`; `ComfyStreamWorkflowRunner` la sustituye por `SaveImageWebsocket` para recibir la imagen procesada como bytes en memoria.

La primera version usa `/upload/image` para enviar el frame de entrada a ComfyUI. La salida ya evita el guardado en disco.
