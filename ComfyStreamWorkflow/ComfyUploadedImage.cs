namespace ComfyStreamWorkflow;

public sealed record ComfyUploadedImage(string Filename, string Subfolder, string Type)
{
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
