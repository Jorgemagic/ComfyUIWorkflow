# ComfyUI Workflow Runner

Cliente y ejemplos en C#/.NET para ejecutar workflows de ComfyUI desde codigo, recibir resultados por WebSocket y probar pipelines de imagen tanto desde texto como desde webcam.

Este repositorio no reimplementa ComfyUI. Usa ComfyUI como motor Python y aporta una capa .NET para:

- cargar workflows JSON de ComfyUI;
- lanzar prompts contra la API HTTP/WebSocket;
- transformar salidas `SaveImage` y `PreviewImage` en `SaveImageWebsocket`;
- recoger imagenes generadas como bytes PNG en memoria;
- arrancar ComfyUI en modo headless cuando hay un workspace valido;
- enviar frames de webcam a ComfyUI y mostrar el resultado procesado.

## Proyectos

| Proyecto | Rol |
| --- | --- |
| `ComfyStreamWorkflow` | Libreria principal. Gestiona ComfyUI, transforma workflows y devuelve imagenes en memoria. |
| `ComfyUIWorkflowRun` | Consola de ejemplo para ejecutar un workflow `Text2Img.json` y guardar `result.png`. |
| `WebCamComfyStream` | Consola de webcam con OpenCvSharp que envia frames a ComfyUI y muestra la salida. |
| `API` | Cliente ComfyUI mas directo, basado en `/prompt`, `/history`, `/view` y WebSocket. |

## Requisitos

- .NET 8 SDK.
- Una instalacion funcional de ComfyUI con sus dependencias y modelos.
- Windows para el ejemplo de webcam incluido, porque usa `OpenCvSharp4.runtime.win`.
- Una webcam disponible si quieres ejecutar `WebCamComfyStream`.

ComfyUI puede estar ya levantado en `http://localhost:8000/`, o puedes indicar un workspace para que `ComfyStreamWorkflow` lo arranque como proceso hijo.

El resolver busca el workspace en este orden aproximado:

- ruta indicada en codigo;
- variable de entorno `COMFYUI_WORKSPACE`;
- instalacion de escritorio en `%LOCALAPPDATA%\Programs\ComfyUI`;
- carpetas `ComfyUI` o `comfyui` cercanas al directorio actual o al perfil de usuario.

## Inicio rapido

```powershell
git clone <url-del-repositorio>
cd ComfyUIWorkflow
dotnet restore
dotnet build
```

Si ComfyUI no esta ya iniciado, configura el workspace:

```powershell
$env:COMFYUI_WORKSPACE = "C:\ruta\a\ComfyUI"
```

Ejecuta el ejemplo de texto a imagen:

```powershell
dotnet run --project ComfyUIWorkflowRun.csproj
```

El ejemplo carga `Text2Img.json`, cambia el prompt desde `Program.cs`, espera a que ComfyUI termine y descarga la primera imagen como `result.png` en el directorio de salida.

Ejecuta el ejemplo de webcam:

```powershell
dotnet run --project WebCamComfyStream\WebCamComfyStream.csproj
```

Pulsa `ESC` en la ventana de preview o en la terminal para detenerlo.

## Uso de la libreria

```csharp
await using var runner = new ComfyStreamWorkflowRunner(new ComfyStreamWorkflowOptions
{
    BaseUri = new Uri("http://localhost:8000/"),
    ComfyUiWorkspacePath = ComfyUiWorkspaceResolver.Resolve(),
    PythonExecutable = "python",
    ExtraComfyUiArguments = new[] { "--disable-cuda-malloc" }
});

ComfyStreamWorkflowResult result =
    await runner.ExecuteWorkflowAsync("Text2Img.json");

byte[] pngBytes = result.Images[0].Bytes.ToArray();
await runner.SaveImageAsync(result.Images[0], "outputs/result.png");
```

Antes de enviar el prompt, la libreria clona el workflow y sustituye nodos de salida de archivo (`SaveImage` y `PreviewImage`) por `SaveImageWebsocket`. Asi la salida vuelve por WebSocket como bytes PNG, sin depender de descargar la imagen desde `/view` ni de escribirla primero en disco.

## Workflows incluidos

- `Text2Img.json`: workflow de texto a imagen para el ejemplo de consola.
- `ComfyStreamWorkflow/Text2Img.json`: copia del workflow para la libreria.
- `WebCamComfyStream/WebCamCanny.json`: workflow de webcam orientado a procesamiento tipo Canny.
- `WebCamComfyStream/WebcamFluxKlein2.json`: workflow alternativo para la demo de webcam.

Para usar tus propios workflows, exportalos desde ComfyUI en formato API JSON. En los workflows de webcam debe existir al menos un nodo `WebcamCapture`, `LoadImage` o `PrimaryInputLoadImage`, porque ahi se inyecta el frame actual.

## Configuracion frecuente

En `WebCamComfyStream/Program.cs` puedes cambiar:

```csharp
private static readonly WebcamStreamHelper.Options StreamOptions = new(
    WorkflowPath: "WebcamFluxKlein2.json",
    CameraIndex: 0,
    ComfyUiBaseUrl: "http://localhost:8000/",
    ComfyUiWorkspacePath: null,
    ShowStats: false);
```

- `WorkflowPath`: workflow que procesara cada frame.
- `CameraIndex`: indice de la camara local.
- `ComfyUiBaseUrl`: URL del backend de ComfyUI.
- `ComfyUiWorkspacePath`: ruta explicita a ComfyUI, si no quieres usar `COMFYUI_WORKSPACE`.
- `ShowStats`: imprime estadisticas de rendimiento cada pocos segundos.

## Estructura

```text
.
|-- API/                         # Cliente basico de ComfyUI
|-- ComfyStreamWorkflow/          # Libreria principal
|-- WebCamComfyStream/            # Demo de webcam
|-- Program.cs                    # Demo Text2Img clasica
|-- Text2Img.json                 # Workflow de ejemplo
|-- ComfyUIWorkflowRun.csproj
`-- ComfyUIWorkflowRun.sln
```

## Notas

- Si ComfyUI ya esta levantado en `BaseUri`, el runner lo reutiliza.
- Si no esta levantado y hay un workspace valido, el runner intenta iniciarlo con Python.
- La primera version del stream de webcam sube cada frame mediante `/upload/image`; la salida ya vuelve por WebSocket.
- Los modelos y custom nodes que use cada workflow deben existir en tu instalacion de ComfyUI.

## Documentacion adicional

- `ComfyStreamWorkflow/README.md`
- `WebCamComfyStream/README.md`
