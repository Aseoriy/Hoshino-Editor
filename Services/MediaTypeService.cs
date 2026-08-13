namespace HoshinoEditor.Services;

public enum EditorKind { Photo, Video, Unknown }

public static class MediaTypeService
{
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff"];
    public static readonly string[] VideoExtensions = [".mp4", ".mov", ".mkv", ".avi", ".webm", ".wmv", ".m4v"];

    public static EditorKind GetKind(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (ImageExtensions.Contains(extension)) return EditorKind.Photo;
        if (VideoExtensions.Contains(extension) || extension == ".hoshino") return EditorKind.Video;
        return EditorKind.Unknown;
    }
}
