using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MultiClaude;

internal static class ProfileIcons
{
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);

    internal static Icon Tray(Profile profile)
    {
        using var image = Render(profile, Math.Max(16, System.Windows.Forms.SystemInformation.SmallIconSize.Width), null);
        var handle = image.GetHicon();
        try { using var view = Icon.FromHandle(handle); return (Icon)view.Clone(); }
        finally { DestroyIcon(handle); }
    }

    private static Bitmap Render(Profile p, int size, Image? photo)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        Color color = ColorTranslator.FromHtml(p.Color);
        using var fill = new SolidBrush(color);
        g.FillEllipse(fill, 0, 0, size - 1, size - 1);
        if (photo != null)
        {
            int inset = Math.Max(1, (int)(size * 0.07));
            var target = new Rectangle(inset, inset, size - inset * 2, size - inset * 2);
            using var clip = new GraphicsPath();
            clip.AddEllipse(target);
            g.SetClip(clip);
            int side = Math.Min(photo.Width, photo.Height);
            g.DrawImage(photo, target, new Rectangle((photo.Width - side) / 2, (photo.Height - side) / 2, side, side), GraphicsUnit.Pixel);
        }
        else
        {
            string name = p.Name.Trim();
            string initial = name.Length == 0 ? "?" : StringInfo.GetNextTextElement(name).ToUpperInvariant();
            var ink = 0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B > 145 ? Brushes.Black : Brushes.White;
            using var font = new Font("Segoe UI", size * .6f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(initial, font, ink, new RectangleF(0, 0, size, size), format);
        }
        return bitmap;
    }

    private static byte[] IconData(Func<int, Bitmap> render)
    {
        int[] sizes = [16, 24, 32, 48, 64, 128, 256];
        var frames = new List<byte[]>();
        foreach (int size in sizes)
        {
            using var bitmap = render(size);
            using var data = new MemoryStream();
            bitmap.Save(data, ImageFormat.Png);
            frames.Add(data.ToArray());
        }
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            writer.Write((byte)sizes[i]);
            writer.Write((byte)sizes[i]);
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(frames[i].Length);
            writer.Write(offset);
            offset += frames[i].Length;
        }
        foreach (var frame in frames) writer.Write(frame);
        return output.ToArray();
    }

    internal static string TaskbarIcon(string home, string executablePath, string color)
    {
        var source = new FileInfo(executablePath);
        Color dot = ColorTranslator.FromHtml(color);
        string key = FormattableString.Invariant($"v1|{source.FullName.ToUpperInvariant()}|{source.Length}|{source.LastWriteTimeUtc.Ticks}|{dot.ToArgb()}");
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20];
        string folder = Path.Combine(home, "profile_shortcuts", "taskbar");
        string path = Path.Combine(folder, hash + ".ico");
        if (StoragePaths.RequireUnsharedFile(path)) return path;
        Directory.CreateDirectory(folder);
        WriteIconOnce(path, IconData(size => TaskbarFrame(source.FullName, dot, size)));
        return path;
    }

    private static Bitmap TaskbarFrame(string executable, Color dot, int size)
    {
        using var icon = Icon.ExtractIcon(executable, 0, size)
            ?? throw new IOException("Das Programmsymbol von Claude Desktop konnte nicht geladen werden.");
        var bitmap = icon.ToBitmap();
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float diameter = Math.Max(6, size * .36f);
        float corner = size - diameter - Math.Max(.5f, size * .025f);
        var bounds = new RectangleF(corner, corner, diameter, diameter);
        var rim = .2126 * dot.R + .7152 * dot.G + .0722 * dot.B > 145 ? Brushes.Black : Brushes.White;
        graphics.FillEllipse(rim, bounds);
        float edge = Math.Max(1f, size * .028f);
        bounds.Inflate(-edge, -edge);
        using var fill = new SolidBrush(dot);
        graphics.FillEllipse(fill, bounds);
        return bitmap;
    }

    private static void WriteIconOnce(string path, byte[] data)
    {
        string tmp = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var writer = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) writer.Write(data);
            try { File.Move(tmp, path); }
            catch (IOException) when (StoragePaths.RequireUnsharedFile(path)) { } // A concurrent writer finished first.
        }
        finally { File.Delete(tmp); }
    }

    private static Bitmap? Photo(string? file)
    {
        if (file == null) return null;
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri(file), System.Windows.Media.Imaging.BitmapCreateOptions.None,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(decoder.Frames[0]);
        using var contents = new MemoryStream();
        png.Save(contents);
        contents.Position = 0;
        using var original = Image.FromStream(contents);
        return new Bitmap(original);
    }

    internal static void SaveShortcut(string home, Profile p, string path)
    {
        if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Eine Windows-Verknüpfung (.lnk) auswählen.");
        StoragePaths.RequireUnsharedFile(path);
        var parent = Path.GetDirectoryName(path);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
        string folder = Path.Combine(home, "profile_shortcuts");
        StoragePaths.RequireOrdinary(folder);
        Directory.CreateDirectory(folder);
        string? photo = p.Photo == null ? null : new Catalog(home).PhotoFile(p.Photo);
        using var portrait = Photo(photo);
        byte[] data = IconData(size => Render(p, size, portrait));
        string hash = Convert.ToHexStringLower(SHA256.HashData(data))[..20];
        string icon = Path.Combine(folder, $"{p.CodeKey}-{hash}.ico");
        string tmpLink = Path.Combine(parent!, Guid.NewGuid().ToString("N") + ".lnk");
        dynamic? shell = null, link = null;
        try
        {
            if (!StoragePaths.RequireUnsharedFile(icon)) WriteIconOnce(icon, data);
            var type = Type.GetTypeFromProgID("WScript.Shell", true)!;
            shell = Activator.CreateInstance(type)!;
            link = shell.CreateShortcut(tmpLink);
            link.TargetPath = Environment.ProcessPath!;
            link.Arguments = "--open-profile " + p.CodeKey + " --manager-home " + CodeLauncher.QuoteArg(home);
            link.WorkingDirectory = home;
            link.IconLocation = icon + ",0";
            string title = "Multi Claude · " + p.Name;
            link.Description = title[..Math.Min(240, title.Length)];
            link.Save();
            File.Move(tmpLink, path, true);
        }
        finally
        {
            if (link != null) Marshal.FinalReleaseComObject(link);
            if (shell != null) Marshal.FinalReleaseComObject(shell);
            File.Delete(tmpLink);
        }
    }
}
