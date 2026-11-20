"""Live readings for the compact dashboard. Updating data never replaces a control."""
import tkinter as tk
import time


class UsagePanel(tk.Frame):
    def __init__(self, parent, app, profile, kind, colors, button):
        super().__init__(parent, bg=colors["CARD"])
        self.app, self.profile, self.kind = app, profile, kind
        self.colors = colors
        self.capture = False
        self.last = None
        inner = tk.Frame(self, bg=self["bg"])
        inner.pack(fill="x", padx=12, pady=7)
        header = tk.Frame(inner, bg=self["bg"])
        header.pack(fill="x")
        title = "quota_used" if kind == "overview" else "code_tokens"
        app.heading(header, app.t(title), 11).pack(side="left")
        self.reload = button(header, "↻  " + app.t("refresh"), lambda: app.refresh_usage(profile),
                             tipo="fantasma", tamanho=9, padx=5, pady=1)
        if kind != "summary" and app.options[profile[0]].isolated:
            self.reload.pack(side="right")
        self.body = tk.Frame(inner, bg=self["bg"])
        self.body.pack(fill="x", pady=(3, 1))
        self.metrics = {}
        if kind == "overview":
            for i, key in enumerate(("five_hour", "seven_day")):
                self.body.columnconfigure(i, weight=1, uniform="window")
                box = tk.Frame(self.body, bg=self["bg"])
                box.grid(row=0, column=i, sticky="nsew", padx=(0, 10) if i == 0 else (4, 0))
                app.note(box, app.t(key)).pack(fill="x")
                value = app.heading(box, "—", 16)
                value.pack(fill="x")
                bar = tk.Canvas(box, bg=colors["BTN"], height=max(3, app.px(3)), width=1, highlightthickness=0)
                bar.pack(fill="x", pady=(1, 2))
                bar.fill = bar.create_rectangle(0, 0, 0, 0, width=0)
                bar.percent = 0
                bar.bind("<Configure>", lambda e, b=bar: self.paint(b))
                reset = app.note(box, "")
                reset.pack(fill="x")
                self.metrics[key] = (value, bar, reset)
        else:
            self.total = app.heading(self.body, "—", 17)
            self.total.pack(anchor="w")
            breakdown = tk.Frame(self.body, bg=self["bg"])
            breakdown.pack(fill="x")
            for i, key in enumerate(("tokens_in", "tokens_out", "cache_read", "cache_write")):
                label = app.note(breakdown, "")
                breakdown.columnconfigure(i % 2, weight=1, uniform="tokens")
                label.grid(row=i // 2, column=i % 2, sticky="ew", padx=(0, 5))
                self.metrics[key] = label
        self.message = app.note(inner, "")
        self.message.pack(fill="x")
        self.toggle = button(inner, app.t("capture_on"), lambda: app.toggle_capture(profile, not self.capture),
                             tipo="fantasma", tamanho=9, padx=4, pady=2)

    @staticmethod
    def paint(bar):
        bar.coords(bar.fill, 0, 0, bar.winfo_width() * bar.percent / 100, bar.winfo_height())

    def update_reading(self):
        app, colors = self.app, self.colors
        opts = app.options[self.profile[0]]
        data = app.usage_cache.get(opts.key)
        limits = app.limits.get(opts.key, {})
        windows = dict(app.live_windows(limits))
        stale = time.time() - limits.get("observed_at", 0) > 900
        tokens = data[1] if data else None
        capture = bool(data and data[2])
        signature = (opts.isolated, tokens, capture, data is not None, windows, stale, limits.get("observed_at"))
        if signature == self.last:
            return
        self.last = signature
        self.capture = capture
        message = ""
        if not opts.isolated:
            message = app.t("shared_usage")
        elif self.kind == "overview" and (windows or data is not None):
            for key, (value, bar, reset) in self.metrics.items():
                window = windows.get(key)
                pct = window["used_percentage"] if window else 0
                value.config(text=f"{pct:g} %" if window else "—", fg=colors["SUB"] if stale else colors["TEXTO"])
                bar.percent = pct
                bar.itemconfigure(bar.fill, fill=colors["SUB"] if stale else colors["AMARELO"] if pct > 80 else opts.color)
                self.paint(bar)
                reset.config(text=app.t("reset_at", time=app.local_time(window["resets_at"])) if window else "")
            if windows:
                message = app.t("observed", time=app.local_time(limits["observed_at"]))
                if stale:
                    message += " · " + app.t("stale")
            else:
                message = app.t("wait_reading" if capture else "capture_disabled")
        elif data is None:
            message = app.t("loading")
        elif tokens and tokens.available:
            def number(value):
                return format(value, ",").replace(",", " ")
            self.total.config(text=number(tokens.total_tokens))
            for key, value in (("tokens_in", tokens.input_tokens), ("tokens_out", tokens.output_tokens),
                               ("cache_read", tokens.cache_read_tokens), ("cache_write", tokens.cache_write_tokens)):
                self.metrics[key].config(text=app.t(key) + "   " + number(value))
            message = app.t("sample_count", n=tokens.requests)
        else:
            message = app.t("no_usage" if tokens is not None else "usage_error")
        if self.kind != "overview" and tokens and tokens.incomplete:
            message += " " + app.t("partial")
        show_body = opts.isolated and (windows if self.kind == "overview" else tokens and tokens.available)
        if show_body and not self.body.winfo_manager():
            self.body.pack(fill="x", pady=(3, 1), before=self.message)
        elif not show_body:
            self.body.pack_forget()
        self.message.config(text=message)
        # Keep setup available at the empty state and let a user turn it off from the Code tab.
        if opts.isolated and data and (self.kind == "code" or (self.kind == "overview" and not capture)):
            self.toggle.config(text=app.t("capture_off" if capture else "capture_on"))
            self.toggle.pack(anchor="w", pady=(3, 0))
        else:
            self.toggle.pack_forget()
