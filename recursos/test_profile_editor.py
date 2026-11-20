"""Exercise the German profile editor and sidebar using only temporary profiles."""
import importlib.machinery
import importlib.util
import os
from pathlib import Path
import shutil
import sqlite3
import tempfile
import tkinter as tk
import unittest
from unittest.mock import Mock, call, patch


ROOT = Path(__file__).resolve().parents[1]


def descendants(parent):
    for child in parent.winfo_children():
        yield child
        yield from descendants(child)


class EditorTests(unittest.TestCase):
    def setUp(self):
        self.folder = Path(self.enterContext(tempfile.TemporaryDirectory()))
        self.enterContext(patch.dict(os.environ, {"MULTI_CLAUDE_HOME": str(self.folder)}))
        loader = importlib.machinery.SourceFileLoader("editor_fixture", str(ROOT / "Multi Claude.pyw"))
        self.mod = importlib.util.module_from_spec(importlib.util.spec_from_loader(loader.name, loader))
        loader.exec_module(self.mod)
        for name in ("astronaut.png", "fox.png", "owl.png"):
            pictures = self.folder / "profile_pictures"
            pictures.mkdir(exist_ok=True)
            shutil.copyfile(ROOT / "profile_pictures" / name, pictures / name)
        self.mod.cfg_set("idioma", "de")
        self.mod.cfg_set("tema", "claro")
        self.enterContext(patch.object(self.mod, "preparar_fotos"))
        self.enterContext(patch.object(self.mod, "achar_claude", return_value="fictional"))
        self.enterContext(patch.object(self.mod.App, "atualizar_estado"))
        self.enterContext(patch.object(self.mod.App, "poll_usage"))
        self.enterContext(patch.object(self.mod.App, "calcular_estado", lambda app: app.estado.copy()))
        self.native = self.enterContext(patch.object(self.mod.windows_profiles, "_native")).return_value
        self.tray_native = self.enterContext(patch("profile_tray._native"))
        self.enterContext(patch.object(self.mod.Janela, "focus_force"))
        self.enterContext(patch.object(self.mod.Janela, "grab_set"))
        self.enterContext(patch.object(self.mod, "barra_titulo"))
        self.hide_test_windows()
        self.ui = self.mod.App()
        self.addCleanup(self.close_fixture)
        self.ui.report_callback_exception = Mock()
        self.addCleanup(self.ui.report_callback_exception.assert_not_called)
        self.ui.deiconify()
        self.ui.geometry("960x740+30000+30000")
        self.ui.update()

    def hide_test_windows(self):
        init = tk.Tk.__init__
        show = self.mod.Janela.mostrar
        def hidden_init(window):
            init(window)
            window.withdraw()
            window.attributes("-alpha", 0.0)
            window.tk.call("tk", "scaling", 96 / 72)
        def hidden_show(dialog):
            dialog.attributes("-alpha", 0.0)
            with patch.object(dialog, "geometry", lambda size: tk.Wm.geometry(
                dialog, size.split("+")[0] + "+30000+30000"
            )):
                show(dialog)
        self.enterContext(patch.object(tk.Tk, "__init__", hidden_init))
        self.enterContext(patch.object(self.mod.Janela, "mostrar", hidden_show))

    def close_fixture(self):
        for timer in self.ui.tk.splitlist(self.ui.tk.call("after", "info")):
            self.ui.after_cancel(timer)
        self.ui.destroy()

    def button(self, window, key):
        caption = self.ui.t(key)
        return next(w for w in descendants(window) if isinstance(w, self.mod.Botao) and w["text"] == caption)

    def click(self, widget):
        widget.event_generate("<Button-1>", x=2, y=2)
        self.ui.update()

    def dialog(self):
        return [w for w in self.ui.winfo_children() if isinstance(w, self.mod.Janela)][-1]

    def edit(self, profile=None, *, name=None, shortcut=None):
        self.ui.edit_profile(profile)
        self.ui.update()
        dialog = self.dialog()
        entry, key_field = [w for w in descendants(dialog) if isinstance(w, tk.Entry)]
        for field, value in ((entry, name), (key_field, shortcut)):
            if value is not None:
                field.delete(0, "end")
                field.insert(0, value)
        return dialog

    def create(self, name, shortcut=None):
        dialog = self.edit(name=name, shortcut=shortcut)
        self.click(self.button(dialog, "save"))
        return next(p for p in self.ui.perfis() if p[1] == name)

    def test_create_edit_and_cancel_use_draft_color_name_and_shortcut(self):
        draft = self.edit(name="Verworfen", shortcut="Ctrl+Alt+6")
        self.click(self.button(draft, "cancelar"))
        self.assertFalse(self.ui.perfis())

        draft = self.edit(name="Redaktion", shortcut="Ctrl+Alt+5")
        swatches = [w for w in descendants(draft) if isinstance(w, tk.Radiobutton)]
        swatches[2].invoke()
        self.click(self.button(draft, "save"))
        profile, = self.ui.perfis()
        opt = self.ui.options[profile[0]]
        self.assertEqual((opt.color, opt.shortcut), (self.mod.prefs.COLORS[2], "Ctrl+Alt+5"))
        draft = self.edit(profile, name="Redaktion Berlin")
        with patch.object(self.mod.colorchooser, "askcolor", return_value=((35, 86, 116), "#235674")):
            self.click(self.button(draft, "custom_color"))
        self.click(self.button(draft, "save"))
        self.assertEqual(self.ui.options[profile[0]].key, opt.key)
        self.assertEqual(self.ui.options[profile[0]].color, "#235674")
        updated, = self.ui.perfis()
        self.assertEqual(updated, (profile[0], "Redaktion Berlin", profile[2], profile[3]))
        draft = self.edit(updated, name="Nicht gespeichert", shortcut="Ctrl+Alt+G")
        self.click(self.button(draft, "cancelar"))
        self.assertEqual(self.ui.perfis(), [updated])
        self.assertEqual(self.ui.options[profile[0]].shortcut, "Ctrl+Alt+5")

    def test_duplicate_and_os_conflict_roll_back_creation(self):
        personal = self.create("Privat", "Ctrl+Alt+1")
        draft = self.edit(name="Beruf", shortcut="Ctrl+Alt+1")
        self.click(self.button(draft, "save"))
        self.assertTrue(draft.winfo_exists())
        self.assertEqual(self.ui.perfis(), [personal])
        self.assertTrue(any(isinstance(w, tk.Label) and w["text"] == self.ui.t("hotkey_busy") for w in descendants(draft)))
        keys = [w for w in descendants(draft) if isinstance(w, tk.Entry)][1]
        keys.delete(0, "end")
        keys.insert(0, "Ctrl+Alt+9")
        with patch.object(self.native, "register", side_effect=OSError("shortcut in use")):
            self.click(self.button(draft, "save"))
        self.assertEqual(self.ui.perfis(), [personal])
        self.assertEqual(self.ui.options[personal[0]].shortcut, "Ctrl+Alt+1")
        self.click(self.button(draft, "cancelar"))

    def test_empty_name_feedback_clears_when_corrected_in_same_draft(self):
        draft = self.edit(name="  ")
        self.click(self.button(draft, "save"))
        hint = self.ui.t("novo_pergunta").rstrip(":： ")
        error = next((w for w in descendants(draft) if isinstance(w, tk.Label) and w["text"] == hint), None)
        self.assertIsNotNone(error, "An empty name must identify the missing field")
        self.assertTrue(error.winfo_ismapped())
        field = next(w for w in descendants(draft) if isinstance(w, tk.Entry))
        field.insert("end", "Studio")
        self.ui.update()
        self.assertFalse(error.winfo_ismapped())
        self.assertTrue(draft.winfo_exists())
        self.assertFalse(self.ui.perfis())
        variables = [str(w["textvariable"]) for w in descendants(draft) if isinstance(w, tk.Entry)]
        self.click(self.button(draft, "cancelar"))
        for variable in variables:
            self.assertFalse(self.ui.tk.call("trace", "info", "variable", variable))

    def test_custom_color_and_editor_groups_remain_findable_when_narrow_at_200_percent(self):
        self.ui.tk.call("tk", "scaling", 192 / 72)
        self.ui.escala = 2
        draft = self.edit(name="Studio")
        draft.geometry("680x650+30000+30000")
        self.ui.update()
        labels = [w["text"] for w in descendants(draft) if isinstance(w, tk.Label)]
        self.assertIn("Windows", labels)
        self.assertIn(self.ui.t("code_settings"), labels)
        with patch.object(self.mod.colorchooser, "askcolor", return_value=((35, 86, 116), "#235674")):
            self.click(self.button(draft, "custom_color"))
        swatches = [w for w in descendants(draft) if isinstance(w, tk.Radiobutton)]
        chosen = [w for w in swatches if w["text"] == "✓"]
        self.assertEqual([str(w["value"]) for w in chosen], ["#235674"])
        for swatch in swatches:
            self.assertLessEqual(swatch.winfo_x() + swatch.winfo_width(), swatch.master.winfo_width())
        self.assertTrue(draft.bind("<Control-s>"))
        self.assertTrue(self.button(draft, "save").winfo_ismapped())
        scrolling = next(w for w in descendants(draft) if isinstance(w, self.mod.Rolavel))
        final_checkbox = next(w for w in descendants(draft) if isinstance(w, tk.Checkbutton)
                              and w["text"] == self.ui.t("share_on"))
        self.assertLess(scrolling.tela.yview()[1], 1)
        final_checkbox.event_generate("<FocusIn>")
        self.ui.update_idletasks()
        self.assertGreater(scrolling.tela.yview()[0], 0)
        self.assertLessEqual(final_checkbox.winfo_rooty() + final_checkbox.winfo_height(),
                             scrolling.tela.winfo_rooty() + scrolling.tela.winfo_height())
        self.click(self.button(draft, "cancelar"))
        self.assertFalse(self.ui.perfis())

    def test_failed_commit_restores_previous_sharing_options_name_and_selection(self):
        profile = self.create("Studio")
        selected = self.create("Privat")
        profile_id = profile[0]
        self.ui.select_profile(selected[0])
        real_connect = sqlite3.connect
        class FailedSave(sqlite3.Connection):
            def commit(connection):
                row = connection.execute("SELECT nome FROM perfis WHERE id=?", (profile_id,)).fetchone()
                if row == ("Umbenannt",):
                    raise sqlite3.OperationalError("simulated commit failure")
                super().commit()
        def connect(*args, **kwargs):
            return real_connect(*args, **kwargs, factory=FailedSave)

        for previously_shared in (False, True):
            with self.subTest(previously_shared=previously_shared):
                with self.mod.db() as con:
                    con.execute("UPDATE profile_options SET sharing=? WHERE profile=?", (previously_shared, profile_id))
                    before = self.mod.prefs.all_settings(con)[profile_id]
                self.ui.recarregar()
                draft = self.edit(profile, name="Umbenannt", shortcut="Ctrl+Alt+G")
                next(w for w in descendants(draft) if isinstance(w, tk.Checkbutton)
                     and w["text"] == self.ui.t("share_on")).invoke()
                with patch.object(self.mod.sqlite3, "connect", side_effect=connect), \
                        patch.object(self.mod.profile_code, "share_resources") as share:
                    self.click(self.button(draft, "save"))
                self.assertTrue(draft.winfo_exists())
                self.assertIn(profile, self.ui.perfis())
                with self.mod.db() as con:
                    self.assertEqual(self.mod.prefs.all_settings(con)[profile_id], before)
                self.assertEqual(self.ui.selected, selected[0])
                self.assertEqual(self.mod.cfg_get("selected_code_key"), self.ui.options[selected[0]].key)
                self.assertEqual(share.call_args_list, [call(self.mod.BASE, before.key, not previously_shared),
                                                       call(self.mod.BASE, before.key, previously_shared)])
                self.click(self.button(draft, "cancelar"))

    def test_gallery_choice_is_saved_only_after_confirming_editor(self):
        if not self.mod.Image:
            self.skipTest("Pillow is required to render gallery images")
        profile = self.create("Bibliothek")
        for action, expected in (("cancelar", None), ("save", "astronaut.png")):
            edit = self.edit(profile)
            self.click(self.button(edit, "foto"))
            gallery = self.dialog()
            image = next(w for w in descendants(gallery) if isinstance(w, tk.Canvas) and hasattr(w, "imagem"))
            self.click(image)
            self.assertEqual(self.ui.perfis()[0][3], None)
            self.click(self.button(edit, action))
            self.assertEqual(self.ui.perfis()[0][3], expected)

    def test_tray_checkbox_activates_only_saved_selected_profile_and_can_be_cleared(self):
        first = self.create("Privat")
        second = self.create("Studio")
        def toggle(draft):
            next(w for w in descendants(draft) if isinstance(w, tk.Checkbutton)
                 and w["text"] == self.ui.t("tray")).invoke()
        draft = self.edit(second)
        toggle(draft)
        self.click(self.button(draft, "cancelar"))
        self.tray_native.assert_not_called()
        draft = self.edit(second)
        toggle(draft)
        activate = self.enterContext(patch.object(self.ui, "activate_id"))
        self.click(self.button(draft, "save"))
        win = self.tray_native.return_value
        win.add.assert_called_once()
        _, token, label, _ = win.add.call_args.args
        self.assertEqual(label, "Studio")
        self.assertFalse(self.ui.options[first[0]].tray)
        self.assertTrue(self.ui.options[second[0]].tray)
        _, dispatch, _ = win.attach.call_args.args
        dispatch(token)
        self.ui.update_idletasks()
        activate.assert_called_once_with(second[0])
        draft = self.edit(second)
        toggle(draft)
        self.click(self.button(draft, "save"))
        self.assertFalse(self.ui.options[second[0]].tray)
        win.remove.assert_called_once()
        win.destroy_icon.assert_called_once()

    def test_reorder_controls_persist_in_both_themes_and_compact_layout(self):
        first = self.create("Privat")
        second = self.create("Arbeit")
        self.ui.mudar_tema("escuro")
        self.ui.geometry(f"{self.ui.px(880)}x{self.ui.px(600)}+30000+30000")
        self.ui.update()
        self.assertTrue(self.ui.sidebar.winfo_ismapped())
        self.ui.show_workspace_tab("details")
        self.ui.update_idletasks()
        up = next(w for w in descendants(self.ui.area) if isinstance(w, self.mod.Botao) and w["text"] == "↑  " + self.ui.t("up"))
        self.click(up)
        self.assertEqual(self.ui.perfis(), [second, first])
        self.ui.mudar_tema("claro")
        self.ui.geometry("590x640+30000+30000")
        self.ui.update()
        self.assertTrue(self.ui.picker.winfo_ismapped())
        down = next(w for w in descendants(self.ui.area) if isinstance(w, self.mod.Botao) and w["text"] == "↓  " + self.ui.t("down"))
        self.click(down)
        with self.mod.db() as con:
            self.assertEqual(list(self.mod.prefs.all_settings(con)), [first[0], second[0]])
        self.assertEqual(self.ui.selected, second[0])

    def test_double_click_opens_newly_selected_row_once_after_redraw(self):
        self.ui.geometry(f"{self.ui.px(880)}x{self.ui.px(600)}+30000+30000")
        first = self.create("Privat")
        second = self.create("Arbeit")
        self.ui.select_profile(second[0])
        self.ui.update()
        with patch.object(self.ui, "abrir") as open_profile:
            for click in range(2):
                row = self.ui.sidebar_rows[first[0]]
                for event in ("<ButtonPress-1>", "<ButtonRelease-1>"):
                    row.event_generate(event, x=8, y=8)
                self.ui.update()
                self.assertEqual(self.ui.selected, first[0])
                if click == 0:
                    open_profile.assert_not_called()
            open_profile.assert_called_once_with(first)


if __name__ == "__main__":
    unittest.main()
