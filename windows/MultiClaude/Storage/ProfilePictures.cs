using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MultiClaude;

public sealed partial class Catalog
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {".png",".jpg",".jpeg",".gif",".bmp",".webp",".ico",".tif",".tiff"};
    private static bool ImageExtension(string path) => ImageExtensions.Contains(Path.GetExtension(path));
    private static string Bundled => Path.Combine(AppContext.BaseDirectory, "gallery");

    public string? PhotoFile(string? name)
    {
        if (string.IsNullOrEmpty(name) || Path.GetFileName(name) != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !ImageExtension(name))
            return null;
        foreach (string parent in new[] { Pictures, Bundled })
        {
            string path = Path.Combine(parent, name);
            if (StoragePaths.Ordinary(path) && File.Exists(path)) return path;
        }
        return null;
    }

    public List<string> Photos()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string parent in new[] { Pictures, Bundled })
        {
            if (!StoragePaths.Ordinary(parent) || !Directory.Exists(parent)) continue;
            foreach (string path in Directory.EnumerateFiles(parent))
            {
                string name = Path.GetFileName(path);
                if (PhotoFile(name) != null) names.Add(name);
            }
        }
        return names.OrderBy(n => n.StartsWith("usuario_", StringComparison.OrdinalIgnoreCase))
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string ImportPhoto(string source)
    {
        StoragePaths.RequireOrdinary(source);
        StoragePaths.RequireOrdinary(Pictures);
        Directory.CreateDirectory(Pictures);
        using var input = File.OpenRead(source);
        var frame = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        int side = Math.Min(frame.PixelHeight, frame.PixelWidth);
        var crop = new CroppedBitmap(frame, new Int32Rect((frame.PixelWidth - side) / 2, (frame.PixelHeight - side) / 2, side, side));
        var scaled = new TransformedBitmap(crop, new ScaleTransform(512.0 / side, 512.0 / side));
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(scaled));
        var name = $"usuario_{StoragePaths.Slug(Path.GetFileNameWithoutExtension(source))}_{Guid.NewGuid():N}.png";
        string target = Path.Combine(Pictures, name);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try { png.Save(output); }
        catch { output.Dispose(); File.Delete(target); throw; }
        return name;
    }
}
