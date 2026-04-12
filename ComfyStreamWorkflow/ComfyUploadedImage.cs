namespace ComfyStreamWorkflow;

public sealed class ComfyUploadedImage
{
    public ComfyUploadedImage(string filename, string subfolder, string type)
    {
        Filename = filename;
        Subfolder = subfolder;
        Type = type;
    }

    public string Filename { get; }

    public string Subfolder { get; }

    public string Type { get; }

    public string LoadImagePath
    {
        get
        {
            string path = string.IsNullOrWhiteSpace(Subfolder)
                ? Filename
                : $"{Subfolder.Replace('\\', '/')}/{Filename}";

            return Type switch
            {
                "temp" => $"{path} [temp]",
                "output" => $"{path} [output]",
                _ => path,
            };
        }
    }
}
