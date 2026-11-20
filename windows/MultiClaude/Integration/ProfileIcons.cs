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

    private static byte[] IconData(Profile p, Image? photo)
    {
        int[] sizes = [16, 24, 32, 48, 64, 128, 256];
        var frames = new List<byte[]>();
        foreach (int size in sizes)
        {
            using var bitmap = Render(p, size, photo);
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
        byte[] data = IconData(p, portrait);
        string hash = Convert.ToHexStringLower(SHA256.HashData(data))[..20];
        string icon = Path.Combine(folder, $"{p.CodeKey}-{hash}.ico");
        string tmpIcon = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        string tmpLink = Path.Combine(parent!, Guid.NewGuid().ToString("N") + ".lnk");
        dynamic? shell = null, link = null;
        try
        {
            if (!StoragePaths.RequireUnsharedFile(icon))
            {
                using (var writer = new FileStream(tmpIcon, FileMode.CreateNew, FileAccess.Write, FileShare.None)) writer.Write(data);
                File.Move(tmpIcon, icon, true);
            }
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
            File.Delete(tmpIcon);
        }
    }
}
