namespace ComfyStreamWorkflow;

public sealed class ComfyGeneratedImage
{
    public ComfyGeneratedImage(byte[] bytes, string contentType)
    {
        Bytes = bytes;
        ContentType = contentType;
    }

    public byte[] Bytes { get; }

    public string ContentType { get; }
}
