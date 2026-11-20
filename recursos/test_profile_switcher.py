"""Exercise the profile switcher with synthetic profiles in an invisible Tk window."""
from types import SimpleNamespace
import tkinter as tk
from tkinter import ttk
import unittest
from unittest.mock import Mock, patch

import profile_switcher


class SwitcherTests(unittest.TestCase):
    def setUp(self):
        self.app = tk.Tk()
        self.app.withdraw()
        self.app.attributes("-alpha", 0.0)
        self.app.geometry("480x360+30000+30000")
        self.app.deiconify()
        self.app.update()
        self.addCleanup(self.app.destroy)
        self.app.t = {"switch_profile": "Profil wechseln", "search_profiles": "Profile durchsuchen",
                      "no_match": "Kein Treffer", "abrir": "Öffnen", "cancelar": "Abbrechen"}.__getitem__
        self.app.px = round
        self.app.ajustar_janela = lambda win, w, h, *_: win.geometry(f"{w}x{h}+30000+30000")
        self.app.selected = 2
        self.app.options = {2: SimpleNamespace(color="#445588")}
        self.app.activate_id = Mock()
        self.colors = dict(PAINEL="#ffffff", CARD="#ffffff", BORDA="#cccccc", TEXTO="#202020", SUB="#555555",
                           BTN="#eeeeee", BTN_HOVER="#dddddd", ACCENT="#af4934", ACCENT_HOVER="#963124")
        profiles = [(1, "Büro Berlin", "", None), (2, "Studio", "", None), (3, "Privat", "", None)]
        original = tk.Toplevel.__init__
        def invisible(window, *args, **kwargs):
            original(window, *args, **kwargs)
            window.attributes("-alpha", 0.0)
        with patch.object(tk.Toplevel, "__init__", invisible), patch.object(tk.Toplevel, "grab_set"):
            self.dialog = profile_switcher.show_switcher(self.app, profiles, self.colors)
        self.app.update()
        def descend(parent):
            for child in parent.winfo_children():
                yield child
                yield from descend(child)
        self.widgets = list(descend(self.dialog))
        self.search = next(w for w in self.widgets if isinstance(w, tk.Entry))
        self.results = next(w for w in self.widgets if isinstance(w, ttk.Treeview))

    def key(self, widget, sequence):
        # Dispatch the installed Tcl binding without foregrounding a window on the user's input desktop.
        binding = widget.bind(sequence)
        self.assertTrue(binding)
        widget.bind("<<FixtureKey>>", binding)
        widget.event_generate("<<FixtureKey>>")
        self.app.update()

    def rows(self):
        return [self.results.item(item, "text") for item in self.results.get_children()]

    def test_filter_preserves_order_keyboard_opens_only_the_chosen_result(self):
        self.assertEqual(self.rows(), ["Büro Berlin", "Studio", "Privat"])
        self.assertEqual(self.results.item(self.results.selection()[0], "text"), "Studio")
        self.key(self.search, "<Down>")
        self.assertEqual(self.results.item(self.results.selection()[0], "text"), "Privat")
        self.search.insert(0, "BER buro")
        self.app.update()
        self.assertEqual(self.rows(), ["Büro Berlin"])
        self.key(self.search, "<Return>")
        self.app.activate_id.assert_called_once_with(1)
        self.assertFalse(self.dialog.winfo_exists())

    def test_empty_results_disable_open_and_escape_closes_without_activation(self):
        variable = str(self.search["textvariable"])
        self.search.insert(0, "Missing")
        self.app.update()
        self.assertFalse(self.rows())
        empty = next(w for w in self.widgets if isinstance(w, tk.Label) and w["text"] == "Kein Treffer")
        self.assertTrue(empty.winfo_ismapped())
        self.key(self.search, "<Return>")
        self.assertTrue(self.dialog.winfo_exists())
        self.key(self.dialog, "<Escape>")
        self.assertFalse(self.dialog.winfo_exists())
        self.app.activate_id.assert_not_called()
        self.assertFalse(self.app.tk.call("trace", "info", "variable", variable))

    def test_single_click_selects_and_open_button_activates(self):
        row = self.results.get_children()[-1]
        x, y, w, h = self.results.bbox(row)
        self.results.event_generate("<ButtonPress-1>", x=x+w//2, y=y+h//2)
        self.results.event_generate("<ButtonRelease-1>", x=x+w//2, y=y+h//2)
        self.app.update()
        self.app.activate_id.assert_not_called()
        button = next(w for w in self.widgets if isinstance(w, tk.Button) and w["text"] == "Öffnen")
        button.invoke()
        self.app.activate_id.assert_called_once_with(3)

    def test_all_three_results_fit_and_double_click_activates_its_own_row(self):
        rows = self.results.get_children()
        self.assertEqual(self.results.yview(), (0.0, 1.0))
        x, y, w, h = self.results.bbox(rows[0])
        for _ in range(2):
            self.results.event_generate("<ButtonPress-1>", x=x+w//2, y=y+h//2)
            if self.results.winfo_exists():
                self.results.event_generate("<ButtonRelease-1>", x=x+w//2, y=y+h//2)
        self.app.update()
        self.app.activate_id.assert_called_once_with(1)


if __name__ == "__main__":
    unittest.main()
