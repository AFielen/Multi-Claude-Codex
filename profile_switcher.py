"""Keyboard and mouse profile picker for the current manager window."""
import tkinter as tk
from tkinter import font, ttk
import unicodedata


def _search_text(text):
    return "".join(c for c in unicodedata.normalize("NFKD", text.casefold()) if not unicodedata.combining(c))


def show_switcher(app, profiles, colors):
    existing = getattr(app, "_profile_switcher", None)
    if existing is not None and existing.winfo_exists():
        return existing
    previous_focus = app.focus_get()
    previous_grab = app.grab_current()
    dialog = tk.Toplevel(app, background=colors["PAINEL"])
    dialog.withdraw()
    dialog.transient(app)
    dialog.title(app.t("switch_profile"))
    app._profile_switcher = dialog
    px = app.px
    body = tk.Frame(dialog, bg=colors["PAINEL"], padx=px(14), pady=px(12))
    body.pack(fill="both", expand=True)
    body.columnconfigure(0, weight=1)
    body.rowconfigure(2, weight=1)
    tk.Label(body, text=app.t("search_profiles"), bg=body["bg"], fg=colors["TEXTO"],
             font=("Segoe UI", 11, "bold"), anchor="w").grid(row=0, column=0, sticky="ew", pady=(0, px(5)))
    query = tk.StringVar(dialog)
    search = tk.Entry(body, textvariable=query, bg=colors["CARD"], fg=colors["TEXTO"], width=1,
                      insertbackground=colors["TEXTO"], font=("Segoe UI", 11), relief="flat", borderwidth=3,
                      highlightthickness=px(1), highlightcolor=colors["ACCENT"], highlightbackground=colors["BORDA"])
    search.grid(row=1, column=0, sticky="ew", ipady=px(3), pady=(0, px(7)))

    frame = tk.Frame(body, bg=colors["CARD"], highlightbackground=colors["BORDA"], highlightthickness=1)
    frame.grid(row=2, column=0, sticky="nsew")
    frame.columnconfigure(0, weight=1)
    frame.rowconfigure(0, weight=1)
    rows = {str(n): (p[0], p[1], _search_text(p[1])) for n, p in enumerate(profiles)}
    row_height = font.nametofont("TkDefaultFont", dialog).metrics("linespace") + px(12)
    style = ttk.Style(dialog)
    tree_style = "ProfileSwitcher.Treeview"
    style.configure(tree_style, font=("Segoe UI", 10), rowheight=row_height, background=colors["CARD"],
                    fieldbackground=colors["CARD"], foreground=colors["TEXTO"], borderwidth=0)
    style.map(tree_style, background=[("selected", colors.get("ACTIVE", colors["BTN_HOVER"]))],
              foreground=[("selected", colors["TEXTO"])])
    results = ttk.Treeview(frame, show="tree", selectmode="browse", height=max(2, min(len(rows), 6)), style=tree_style)
    results.column("#0", width=1, minwidth=1, stretch=True)
    results.grid(row=0, column=0, sticky="nsew")
    bar = ttk.Scrollbar(frame, orient="vertical", command=results.yview)
    def sync_scroll(first, last):
        if float(first) <= 0 and float(last) >= 1:
            bar.grid_remove()
        else:
            bar.grid(row=0, column=1, sticky="ns")
        bar.set(first, last)
    results.configure(yscrollcommand=sync_scroll)
    empty = tk.Label(frame, text=app.t("no_match"), bg=frame["bg"], fg=colors["SUB"], font=("Segoe UI", 10))
    icons = {}
    def marker(identifier):
        option = getattr(app, "options", {}).get(identifier)
        color = getattr(option, "color", colors["ACCENT"])
        if color not in icons:
            size = max(4, px(9))
            image = tk.PhotoImage(master=dialog, width=size+px(5), height=size)
            for y in range(size):
                half = ((size / 2)**2 - (y + .5 - size / 2)**2)**.5
                left, right = round(size / 2 - half), round(size / 2 + half)
                image.put(color, to=(left, y, max(left+1, right), y+1))
            icons[color] = image
        return icons[color]

    def close(restore=True):
        dialog.destroy()
        if previous_grab is not None and previous_grab.winfo_exists():
            previous_grab.grab_set()
        if restore and previous_focus is not None and previous_focus.winfo_exists():
            previous_focus.focus_set()
    def activate(event=None):
        selection = results.selection()
        if selection:
            identifier = rows[selection[0]][0]
            close(restore=False)
            app.activate_id(identifier)
        return "break"

    actions = tk.Frame(body, bg=body["bg"])
    actions.grid(row=3, column=0, sticky="ew", pady=(px(9), 0))
    button_opts = dict(relief="flat", cursor="hand2", font=("Segoe UI", 10, "bold"), borderwidth=0,
                       padx=px(12), pady=px(4))
    open_button = tk.Button(actions, text=app.t("abrir"), command=activate, bg=colors["ACCENT"], fg="white",
                            activebackground=colors["ACCENT_HOVER"], activeforeground="white", **button_opts)
    open_button.pack(side="right")
    tk.Button(actions, text=app.t("cancelar"), command=close, bg=body["bg"], activebackground=colors["BTN"],
              fg=colors["TEXTO"], activeforeground=colors["TEXTO"],
              **button_opts).pack(side="right", padx=(0, px(5)))

    def select(item):
        results.selection_set(item)
        results.focus(item)
        results.see(item)
    def render(*_):
        selection = results.selection()
        preferred = rows[selection[0]][0] if selection else getattr(app, "selected", None)
        terms = _search_text(query.get()).split()
        visible = [item for item, (_, _, text) in rows.items() if all(term in text for term in terms)]
        results.delete(*results.get_children())
        for item in visible:
            identifier, label, _ = rows[item]
            results.insert("", "end", iid=item, text=label, image=marker(identifier))
        if visible:
            empty.grid_remove()
            select(next((item for item in visible if rows[item][0] == preferred), visible[0]))
        else:
            empty.grid(row=0, column=0, sticky="nsew")
        open_button.configure(state="normal" if visible else "disabled")
    def move(delta):
        visible = results.get_children()
        if visible:
            current = results.selection()
            index = visible.index(current[0]) if current else 0
            select(visible[min(len(visible)-1, max(0, index+delta))])
        return "break"
    def double_click(event):
        item = results.identify_row(event.y)
        if item:
            select(item)
            activate()
        return "break"

    query_trace = query.trace_add("write", render)
    def release(event):
        if event.widget is dialog:
            query.trace_remove("write", query_trace)
            app._profile_switcher = None
    dialog.bind("<Destroy>", release, add="+")
    render()
    for control in (search, results):
        control.bind("<Up>", lambda event: move(-1))
        control.bind("<Down>", lambda event: move(1))
        control.bind("<Return>", activate)
    results.bind("<Double-Button-1>", double_click)
    dialog.bind("<Escape>", lambda event: close())
    dialog.protocol("WM_DELETE_WINDOW", close)
    dialog.update_idletasks()
    w, h = max(px(375), dialog.winfo_reqwidth()), dialog.winfo_reqheight()
    x = app.winfo_rootx() + (app.winfo_width() - w) // 2
    y = app.winfo_rooty() + (app.winfo_height() - h) // 3
    app.ajustar_janela(dialog, w, h, x, y, px(250), px(174))
    dialog.deiconify()
    dialog.grab_set()
    search.focus_set()
    return dialog
