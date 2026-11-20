"""Render the German Windows workspace with fictional data. Defaults to dist/.

Example: python recursos/preview_workspace.py --dpi 96 --profiles 3 --output-dir dist
"""
import argparse
import contextlib
import ctypes
import importlib.machinery
import importlib.util
import os
from pathlib import Path
import sys
import tempfile
import time
from unittest.mock import patch

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
sys.path[:0] = [str(ROOT), str(ROOT / "recursos")]
from capture_screenshots import capture


def load_application():
    loader = importlib.machinery.SourceFileLoader("workspace_preview", str(ROOT / "Multi Claude.pyw"))
    spec = importlib.util.spec_from_loader(loader.name, loader)
    module = importlib.util.module_from_spec(spec)
    loader.exec_module(module)
    return module


@contextlib.contextmanager
def fictional_workspace(dpi=None, profile_count=3):
    """Keep the actual widgets, replacing process discovery and global Windows integrations."""
    with tempfile.TemporaryDirectory(prefix="multi-claude-de-preview-") as sandbox, contextlib.ExitStack() as stack:
        stack.enter_context(patch.dict(os.environ, {"MULTI_CLAUDE_HOME": sandbox}))
        module = load_application()
        if dpi is not None:
            init = module.tk.Tk.__init__

            def init_scaled(root, *args, **kwargs):
                init(root, *args, **kwargs)
                root.tk.call("tk", "scaling", dpi / 72)

            stack.enter_context(patch.object(module.tk.Tk, "__init__", init_scaled))
        stack.enter_context(patch.object(module, "achar_claude", return_value="fixture-only"))
        for method in ("atualizar_estado", "sync_shortcuts", "sync_tray"):
            stack.enter_context(patch.object(module.App, method, lambda self, *args: None))
        module.cfg_set("idioma", "de")
        module.cfg_set("tema", "claro")
        app = module.App()

        def close_fixture():
            for timer in app.tk.splitlist(app.tk.call("after", "info")):
                app.after_cancel(timer)
            app.destroy()

        stack.callback(close_fixture)
        profiles = (("Studio", "astronaut.png", True, True, 46, 68),
                    ("Privat", "fox.png", True, False, 82, 53),
                    ("Recherche", "owl.png", False, False, 12, 24))[:profile_count]
        with module.db() as db:
            for name, photo, *_ in profiles:
                app._inserir(db, name, foto=photo)
        now = int(time.time())
        samples = module.profile_usage.TokenUsage(input_tokens=3840, output_tokens=1290,
                                                 cache_read_tokens=1120, cache_write_tokens=470,
                                                 requests=18, available=True)
        with module.db() as db:
            options = module.prefs.all_settings(db)
        for (identifier, _, path, _), (_, _, logged_in, opened, five, seven) in zip(app.perfis(), profiles):
            app.estado[path] = (logged_in, opened)
            key = options[identifier].key
            app.usage_cache[key] = (time.monotonic(), samples, True)
            app.sizes[key] = (18 * 1024**2, False)
            app.limits[key] = {"observed_at": now - 120,
                               "five_hour": {"used_percentage": five, "resets_at": now + 2 * 3600},
                               "seven_day": {"used_percentage": seven, "resets_at": now + 3 * 86400}}
        refreshed = {
            key: {**reading, "five_hour": {**reading["five_hour"], "used_percentage": 47}}
            for key, reading in app.limits.items()
        }

        def read_fixture(base, selected, keys, results, revision, folder):
            results.put((revision, selected, samples, True, refreshed, (18 * 1024**2, False)))

        stack.enter_context(patch.object(module.App, "scan_usage", staticmethod(read_fixture)))
        errors = []
        app.report_callback_exception = lambda _, error, __: errors.append(error)
        app.recarregar()
        yield app
        if errors:
            raise RuntimeError("Tk raised a callback exception during preview") from errors[0]


def descendants(widget):
    for child in widget.winfo_children():
        yield child
        yield from descendants(child)


def check_geometry(app):
    """Identify visible interactive controls extending past the horizontal viewport."""
    left = app.winfo_rootx()
    right = left + app.winfo_width()
    for child in descendants(app):
        if not child.winfo_ismapped() or not child.bind("<Button-1>"):
            continue
        if child.winfo_rootx() < left - 1 or child.winfo_rootx() + child.winfo_width() > right + 1:
            raise AssertionError(f"Control outside {app.winfo_width()} px workspace: {child}")
    for control in [app.open_action, *app.compact_buttons.values()]:
        if not control.winfo_ismapped():
            raise AssertionError(f"Main action hidden: {control}")
        top = control.winfo_rooty()
        bottom = top + control.winfo_height()
        parent = control.master
        while parent is not None:
            if top < parent.winfo_rooty() - 1 or bottom > parent.winfo_rooty() + parent.winfo_height() + 1:
                raise AssertionError(f"{control['text']} needs scrolling in the {app.winfo_width()} px workspace")
            parent = parent.master


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", type=Path, default=ROOT / "dist")
    parser.add_argument("--dpi", type=int, choices=(96, 120, 144, 192), help="Override Tk display DPI for layout testing")
    parser.add_argument("--profiles", type=int, choices=(2, 3), default=3)
    arguments = parser.parse_args()
    out = arguments.output_dir.resolve()
    out.mkdir(parents=True, exist_ok=True)
    ctypes.windll.shcore.SetProcessDpiAwareness(1)
    with fictional_workspace(arguments.dpi, arguments.profiles) as app:
        for filename, theme, width, height in (("Hell", "claro", 480, 360),
                                               ("Dunkel", "escuro", 480, 360),
                                               ("Schmal", "claro", 400, 320),
                                               ("Breit", "claro", 900, 640)):
            app.mudar_tema(theme)
            requested = (app.px(width), app.px(height))
            app.geometry(f"{requested[0]}x{requested[1]}+40+40")
            path = out / f"Vorschau-Arbeitsbereich-DE-{filename}.png"
            capture(app, path)
            check_geometry(app)
            with Image.open(path) as image:
                if image.size != requested:
                    raise AssertionError(f"Requested {requested}, received {image.size}")
                if len(image.getcolors(image.width * image.height)) < 100:
                    raise AssertionError(f"No rendered widget content in {path}")


if __name__ == "__main__":
    main()
