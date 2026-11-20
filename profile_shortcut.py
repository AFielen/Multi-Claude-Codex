"""Save a user-selected Windows Shell Link and its multi-resolution profile icon."""
import hashlib
import json
import os
from pathlib import Path
import re
import struct
import subprocess
import sys
import tempfile

from profile_code import _local, _ordinary, _powershell, code_config_dir


_SIZES = (16, 24, 32, 48, 64, 128, 256)
_FORMATS = {".png", ".jpeg", ".jpg", ".bmp", ".tif", ".tiff", ".gif", ".ico", ".webp"}
_CREATE = r"""
$ErrorActionPreference = 'Stop'
$v = [Console]::In.ReadToEnd() | ConvertFrom-Json
Add-Type -AssemblyName System.Drawing
$color = [Drawing.ColorTranslator]::FromHtml($v.color)
$brush = [Drawing.SolidBrush]::new($color)
$ink = if ((0.2126*$color.R + 0.7152*$color.G + 0.0722*$color.B) -gt 145) {
    [Drawing.Brushes]::Black
} else { [Drawing.Brushes]::White }
$text = [Drawing.StringFormat]::new()
$text.Alignment = $text.LineAlignment = [Drawing.StringAlignment]::Center
$photo = $null
try {
    if ($v.photo) { $photo = [Drawing.Image]::FromFile($v.photo) }
    foreach ($size in $v.sizes) {
        $n = [int]$size
        $bmp = [Drawing.Bitmap]::new($n,$n)
        $g = [Drawing.Graphics]::FromImage($bmp)
        $clip = [Drawing.Drawing2D.GraphicsPath]::new()
        $font = $null
        try {
            $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
            $g.FillEllipse($brush, 0, 0, $n-1, $n-1)
            if ($photo) {
                $inset = [Math]::Max(1, [int]($n*0.07))
                $d = $n - 2*$inset
                $clip.AddEllipse($inset,$inset,$d,$d)
                $g.SetClip($clip)
                $side = [Math]::Min($photo.Width,$photo.Height)
                $src = [Drawing.Rectangle]::new([int](($photo.Width-$side)/2),[int](($photo.Height-$side)/2),$side,$side)
                $dst = [Drawing.Rectangle]::new($inset,$inset,$d,$d)
                $g.DrawImage($photo,$dst,$src,[Drawing.GraphicsUnit]::Pixel)
            } else {
                $font = [Drawing.Font]::new('Segoe UI', [single]($n*0.62), [Drawing.FontStyle]::Bold, [Drawing.GraphicsUnit]::Pixel)
                $box = [Drawing.RectangleF]::new(0,0,$n,$n)
                $g.DrawString($v.initial,$font,$ink,$box,$text)
            }
            $bmp.Save([IO.Path]::Combine($v.staging,"$n.png"), [Drawing.Imaging.ImageFormat]::Png)
        } finally {
            if ($font) { $font.Dispose() }
            $clip.Dispose()
            $g.Dispose()
            $bmp.Dispose()
        }
    }
} finally {
    if ($photo) { $photo.Dispose() }
    $text.Dispose()
    $brush.Dispose()
}
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($v.link)
$link.TargetPath = $v.program
$link.Arguments = $v.arguments
$link.WorkingDirectory = $v.cwd
$link.IconLocation = $v.icon + ',0'
$link.Description = $v.description
$link.Save()
"""


def _pack_icon(staging):
    frames = [(staging / f"{size}.png").read_bytes() for size in _SIZES]
    offset = 6 + 16 * len(frames)
    header = bytearray(struct.pack("<HHH", 0, 1, len(frames)))
    for size, frame in zip(_SIZES, frames):
        header.extend(struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(frame), offset))
        offset += len(frame)
    return bytes(header) + b"".join(frames)


def _photo_for_drawing(photo, staging):
    if not photo or photo.suffix.lower() not in (".ico", ".webp"):
        return str(photo) if photo else ""
    # The packaged gallery uses Pillow. Native GDI+ cannot decode every gallery format.
    try:
        from PIL import Image
    except ImportError:
        return ""
    converted = staging / "portrait.png"
    with Image.open(photo) as original:
        original.thumbnail((512, 512))
        original.convert("RGBA").save(converted)
    return str(converted)


def save_shortcut(file, base, profile_key, name, color, photo_path=None):
    """Replace only the selected .lnk. Existing links keep their content-addressed icons."""
    destination = _local(file)
    base = _local(base)
    code_config_dir(base, profile_key)  # same persistent key and local path rules as the launcher
    if destination.suffix.lower() != ".lnk":
        raise ValueError("Choose a Windows shortcut (.lnk).")
    if not isinstance(color, str) or not re.fullmatch("#[0-9A-Fa-f]{6}", color):
        raise ValueError("A six-digit profile color is required.")
    if not isinstance(name, str) or not name.strip() or "\x00" in name:
        raise ValueError("A profile name is required.")
    if not _ordinary(destination.parent, True):
        raise FileNotFoundError(str(destination.parent))
    _ordinary(destination, False)
    photo = None
    if photo_path:
        candidate = _local(photo_path)
        if candidate.parent != base / "profile_pictures" or candidate.suffix.lower() not in _FORMATS:
            raise ValueError("Choose a photo from the profile gallery.")
        if _ordinary(candidate, False):
            photo = candidate
    # The icon name changes only when its visual inputs change. Windows caches icons by path.
    initial = name.strip()[:1].upper()[:1]
    digest = hashlib.sha256(("icon-v1|" + color.lower() + "|" + initial).encode("utf-8"))
    if photo:
        with photo.open("rb") as image:
            while chunk := image.read(65536):
                digest.update(chunk)
    icons = base / "profile_shortcuts"
    if not _ordinary(icons, True):
        icons.mkdir(mode=0o700, exist_ok=True)
    icon = icons / f"{profile_key}-{digest.hexdigest()[:20]}.ico"
    _ordinary(icon, False)
    source = Path(__file__).with_name("Multi Claude.pyw")
    program = Path(sys.executable)
    args = ["--open-profile", profile_key, "--manager-home", str(base)]
    if not getattr(sys, "frozen", False):
        windowed = program.with_name("pythonw.exe")
        if windowed.is_file():
            program = windowed
        args.insert(0, str(source))
    with (tempfile.TemporaryDirectory(dir=destination.parent, prefix=".multi-claude-link-") as link_dir,
          tempfile.TemporaryDirectory(dir=icons, prefix=".icon-") as icon_dir):
        staged_link = Path(link_dir) / "profile.lnk"
        staged_icon = Path(icon_dir) / "profile.ico"
        data = dict(link=str(staged_link), icon=str(icon), staging=icon_dir, program=str(program),
                    arguments=subprocess.list2cmdline(args), cwd=str(base), description=("Multi Claude · " + name)[:240],
                    color=color, initial=initial, photo=_photo_for_drawing(photo, Path(icon_dir)), sizes=_SIZES)
        shell = _powershell()
        try:
            result = subprocess.run([str(shell), "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", _CREATE],
                                    input=json.dumps(data).encode("ascii"), capture_output=True, timeout=25,
                                    env=dict(os.environ, PSMODULEPATH=str(shell.parent / "Modules")),
                                    creationflags=subprocess.CREATE_NO_WINDOW)
        except subprocess.TimeoutExpired as exc:
            raise OSError("Windows Shell timed out while creating the shortcut.") from exc
        if result.returncode:
            raise OSError(result.stderr.decode(errors="replace").strip() or "Cannot create Windows shortcut.")
        if not staged_link.is_file():
            raise OSError("The Windows Shell did not create the shortcut.")
        staged_icon.write_bytes(_pack_icon(Path(icon_dir)))
        os.replace(staged_icon, icon)
        os.replace(staged_link, destination)
    return destination
