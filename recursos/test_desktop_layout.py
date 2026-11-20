"""Measure the German Tk layout at 200% using transparent, off-screen fixture windows."""
import importlib.machinery
import importlib.util
import os
from pathlib import Path
import tempfile
import tkinter as tk
import unittest
from unittest.mock import Mock, patch


class DesktopLayoutTests(unittest.TestCase):
    def setUp(self):
        root = self.enterContext(tempfile.TemporaryDirectory())
        self.enterContext(patch.dict(os.environ, {"MULTI_CLAUDE_HOME": root}))
        source = Path(__file__).resolve().parents[1] / "Multi Claude.pyw"
        loader = importlib.machinery.SourceFileLoader("layout_fixture", str(source))
        self.module = importlib.util.module_from_spec(importlib.util.spec_from_loader(loader.name, loader))
        loader.exec_module(self.module)
        self.module.cfg_set("idioma", "de")
        self.module.cfg_set("tema", "claro")
        self.enterContext(patch.object(self.module, "achar_claude", return_value="fixture-only"))
        self.enterContext(patch.object(self.module.App, "atualizar_estado"))
        self.enterContext(patch.object(self.module, "barra_titulo"))
        self.enterContext(patch.object(self.module, "preparar_fotos"))
        init = tk.Tk.__init__

        def hidden(window):
            init(window)
            window.withdraw()
            window.attributes("-alpha", 0.0)
            window.attributes("-disabled", True)
            window.tk.call("tk", "scaling", 192 / 72)

        self.enterContext(patch.object(tk.Tk, "__init__", hidden))
        geometry = tk.Wm.geometry

        def record_geometry(window, value=None):
            if value:
                self.requested = value
            return geometry(window, value)

        self.enterContext(patch.object(self.module.App, "geometry", record_geometry))

    def launch(self, saved=None):
        if saved:
            self.module.cfg_set("geometria", saved)
        with self.module.db() as db:
            db.execute("INSERT INTO perfis (nome, pasta) VALUES (?,?)",
                       ("Abnahmeprofil", os.path.join("dados", "fixture")))
        self.ui = self.module.App()
        def close_fixture():
            for timer in self.ui.tk.splitlist(self.ui.tk.call("after", "info")):
                self.ui.after_cancel(timer)
            self.ui.destroy()
        self.addCleanup(close_fixture)
        self.ui.report_callback_exception = Mock()
        self.addCleanup(self.ui.report_callback_exception.assert_not_called)
        # Read the requested size without mapping an on-screen window.
        self.ui.update_idletasks()
        size = self.requested.split("+")[0].split("-")[0]
        self.ui.geometry(size + "+30000+30000")
        self.ui.deiconify()
        self.ui.update()
        self.profile = self.ui.perfis()[0]
        self.ui.estado = {self.profile[2]: (False, True)}
        self.ui.login_perfil = self.profile
        self.ui.montar()
        self.ui.update()

    def descendants(self, parent):
        for child in parent.winfo_children():
            yield child
            yield from self.descendants(child)

    def assert_label_fits(self, text):
        label = next(w for w in self.descendants(self.ui) if isinstance(w, tk.Label) and w["text"] == text)
        self.assertTrue(label.winfo_ismapped(), text)
        self.assertGreaterEqual(label.winfo_width(), label.winfo_reqwidth(), text)
        self.assertGreaterEqual(label.winfo_height(), label.winfo_reqheight(), text)
        parent = label.master
        while parent is not self.ui:
            self.assertLessEqual(label.winfo_rootx() + label.winfo_width(),
                                 parent.winfo_rootx() + parent.winfo_width() + 1, text)
            parent = parent.master

    def test_pending_login_fits_default_at_200_percent(self):
        with patch.object(self.module.App, "area_trabalho", return_value=(0, 0, 2560, 1440)):
            self.launch()
        self.assertGreaterEqual(self.ui.winfo_width(), self.ui.px(720))
        self.assertTrue(self.ui.sidebar.winfo_ismapped())
        for text in ("Multi Claude", "Abnahmeprofil", self.ui.t("aguard"), self.ui.t("colar"),
                     self.ui.t("cancelar"), self.module.ATRIBUICAO):
            self.assert_label_fits(text)

    def test_small_legacy_size_switches_to_compact_and_keeps_controls(self):
        self.launch("640x680+50+50")
        self.ui.geometry("840x740+30000+30000")
        self.ui.update()
        self.assertEqual(self.ui.layout, (1, True))
        for text in ("Multi Claude", "Abnahmeprofil", self.ui.t("aguard"), self.ui.t("entrar"),
                     self.ui.t("fechar"), self.ui.t("colar"), self.module.ATRIBUICAO):
            self.assert_label_fits(text)

    def test_profile_status_and_actions_fit_small_work_area(self):
        with patch.object(self.module.App, "area_trabalho", return_value=(0, 0, 800, 660)):
            self.launch("4000x3000+30000+30000")
        self.ui.login_perfil = None
        self.ui.montar()
        self.ui.update()
        self.assertLessEqual(self.ui.winfo_width(), 800 - self.ui.moldura[0])
        self.assertLessEqual(self.ui.winfo_height(), 660 - self.ui.moldura[1])
        for text in (self.ui.t("desconectado"), self.ui.t("aberto"), self.ui.t("entrar"),
                     self.ui.t("fechar"), self.module.ATRIBUICAO):
            self.assert_label_fits(text)

    def test_geometry_on_left_monitor_keeps_absolute_negative_position(self):
        self.launch()
        with patch.object(self.ui, "area_trabalho", return_value=(-1920, 0, 0, 1080)), patch.object(
            self.ui, "geometry"
        ) as set_geometry:
            self.ui.restaurar_geometria("1200x840+-1450+105")
        set_geometry.assert_called_once_with("1200x840+-1450+105")

    def test_dialog_fits_monitor_including_its_frame(self):
        self.launch()
        dialog = self.module.Janela(self.ui, "Fixture", largura=560, altura=560)
        with patch.object(self.ui, "area_trabalho", return_value=(0, 0, 900, 710)), patch.object(
            dialog, "geometry"
        ) as set_geometry, patch.object(dialog, "deiconify"), patch.object(dialog, "grab_set"), patch.object(
            dialog, "focus_force"
        ):
            dialog.mostrar()
        set_geometry.assert_called_once_with(f"{900 - self.ui.moldura[0]}x{710 - self.ui.moldura[1]}+0+0")
        dialog.destroy()

    def test_welcome_continue_button_fits_short_monitor(self):
        self.launch()
        show = self.module.Janela.mostrar

        def hidden_show(dialog):
            dialog.attributes("-alpha", 0.0)
            dialog.attributes("-disabled", True)
            geometry = dialog.geometry

            def move_offscreen(value):
                return geometry(value.split("+")[0] + "+30000+30000")

            with patch.object(dialog, "geometry", move_offscreen), patch.object(
                dialog, "grab_set"
            ), patch.object(dialog, "focus_force"):
                show(dialog)
            dialog.update()

        with patch.object(self.module.Janela, "mostrar", hidden_show), patch.object(
            self.ui, "area_trabalho", return_value=(0, 0, 800, 660)
        ):
            self.ui.boas_vindas()
        dialog = next(child for child in self.ui.winfo_children() if isinstance(child, self.module.Janela))
        button = next(w for w in self.descendants(dialog) if isinstance(w, self.module.Botao))
        self.assertTrue(button.winfo_ismapped())
        self.assertGreaterEqual(button.winfo_height(), button.winfo_reqheight())
        self.assertLessEqual(button.winfo_rooty() + button.winfo_height(), dialog.winfo_rooty() + dialog.winfo_height())
        dialog.destroy()


if __name__ == "__main__":
    unittest.main()
