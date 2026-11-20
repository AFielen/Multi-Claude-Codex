using System.Drawing;
using MultiClaude;

internal static class TaskbarOverlayChecks
{
    internal static void Run(string tempRoot)
    {
        string home = Path.Combine(tempRoot, "taskbar icon checks");
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        string darkPath = ProfileIcons.TaskbarIcon(home, executable, "#102030");
        string lightPath = ProfileIcons.TaskbarIcon(home, executable, "#ffedcc");
        if (darkPath == lightPath || lightPath != ProfileIcons.TaskbarIcon(home, executable, "#FFEDCC"))
            throw new Exception("Different colors need distinct icons, and identical colors must reuse the cached file.");

        foreach (int size in new[] { 16, 24, 32, 48, 64, 128, 256 })
        {
            using var originalIcon = Icon.ExtractIcon(executable, 0, size)!;
            using var original = originalIcon.ToBitmap();
            using var darkIcon = Icon.ExtractIcon(darkPath, 0, size)!;
            using var dark = darkIcon.ToBitmap();
            using var lightIcon = Icon.ExtractIcon(lightPath, 0, size)!;
            using var light = lightIcon.ToBitmap();
            if (dark.Width != size || light.Width != size)
                throw new Exception($"Missing {size}px native taskbar icon frame.");
            int center = (int)(size * .8);
            if (dark.GetPixel(center, center).ToArgb() != Color.FromArgb(0x10, 0x20, 0x30).ToArgb() ||
                light.GetPixel(center, center).ToArgb() != Color.FromArgb(0xff, 0xed, 0xcc).ToArgb())
                throw new Exception($"Profile dot color missing from the lower right of the {size}px taskbar icon.");
            bool contrastingEdge = false;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var source = original.GetPixel(x, y);
                var pixel = dark.GetPixel(x, y);
                if (x < size * .55 || y < size * .55)
                {
                    if (source.ToArgb() != pixel.ToArgb() && (source.A != 0 || pixel.A != 0))
                        throw new Exception($"Desktop artwork outside the dot changed at {size}px ({x}, {y}).");
                }
                else if (pixel.R > light.GetPixel(x, y).R + 80) contrastingEdge = true;
            }
            if (!contrastingEdge) throw new Exception($"Missing contrasting rim for light and dark dots at {size}px.");
        }
    }
}
