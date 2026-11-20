"""Text for the sidebar, profile editor and usage view."""

PROFILE_TEXT = {
    "en": dict(
        profiles="Profiles", edit_profile="Edit profile", name="Name", color="Profile color",
        custom_color="Custom…", hotkey="Global shortcut", hotkey_hint="Example: Ctrl+Alt+1. Leave blank to disable.",
        hotkey_busy="Shortcut unavailable", border="Use profile color for the Windows border",
        isolated="Separate Claude Code configuration", shared="Shared Windows configuration", save="Save",
        next_launch="Changes to Claude Code take effect the next time you open Desktop or a terminal.",
        switch="Switch to window", focus_blocked="Windows could not bring this window to the foreground. Select it in the taskbar.",
        opening="Opening {n}…", up="Move up", down="Move down", created="Created",
        last_open="Last opened here", previous="—", explorer="Show in Explorer", terminal="Open terminal",
        copy_command="Copy command", copied="PowerShell command copied.", usage="Usage",
        code_tokens="Local Claude Code tokens", tokens_in="Input", tokens_out="Output",
        cache_read="Cache read", cache_write="Cache write", sample_count="{n} responses in retained profile history",
        partial="Only the readable portion of this profile’s history is included.",
        no_usage="No recorded usage in this profile yet.", loading="Reading profile usage…",
        shared_usage="Enable a separate Claude Code configuration to track usage for this profile.",
        quotas="Plan limits", five_hour="5 hours", seven_day="7 days", reset_at="Resets {time}",
        observed="Last reading {time}", stale="Older reading", refresh="Refresh",
        capture_on="Enable limit readings", capture_off="Disable limit readings",
        no_limits="No current limit reading. Claude Code reports limits after a supported account makes a request.",
        status_conflict="This profile already has a custom status line. You can view the limits with /usage in Claude Code.",
        scope_usage="Limits reported by Claude Code", code_settings="Claude Code",
        share_on="Share skills and instructions", share_off="Stop sharing skills and instructions",
        share_items="Skills, agents, commands and CLAUDE.md", local_only="Profile only",
        shared_items="Shared resources", folder_size="Data size", unavailable="Unavailable",
        hold_code="The separate Claude Code folder is retained when you remove this profile.",
        missing_cli="Claude Code CLI could not be found. Install the Windows CLI to open a terminal.",
        hotkey_help="Shortcuts work while the manager is running.", usage_error="Usage could not be read.",
    ),
    "de": dict(
        profiles="Profile", edit_profile="Profil bearbeiten", name="Name", color="Profilfarbe",
        custom_color="Eigene Farbe…", hotkey="Globales Tastenkürzel", hotkey_hint="Beispiel: Ctrl+Alt+1. Leer lassen zum Deaktivieren.",
        hotkey_busy="Kürzel nicht verfügbar", border="Windows-Fensterrahmen in Profilfarbe",
        isolated="Eigene Claude-Code-Konfiguration", shared="Gemeinsame Windows-Konfiguration", save="Speichern",
        next_launch="Änderungen an Claude Code gelten ab dem nächsten Start von Desktop oder Terminal.",
        switch="Zum Fenster", focus_blocked="Windows konnte das Fenster nicht in den Vordergrund holen. Wähle es in der Taskleiste aus.",
        opening="{n} wird geöffnet…", up="Nach oben", down="Nach unten", created="Erstellt",
        last_open="Hier zuletzt geöffnet", previous="—", explorer="Im Explorer zeigen", terminal="Terminal öffnen",
        copy_command="Befehl kopieren", copied="PowerShell-Befehl kopiert.", usage="Verbrauch",
        code_tokens="Lokale Claude-Code-Tokens", tokens_in="Eingabe", tokens_out="Ausgabe",
        cache_read="Cache gelesen", cache_write="Cache geschrieben", sample_count="{n} Antworten im gespeicherten Profilverlauf",
        partial="Die Summe umfasst den lesbaren Teil des gespeicherten Profilverlaufs.",
        no_usage="Für dieses Profil liegt noch kein Codeverbrauch vor.", loading="Profilverbrauch wird gelesen…",
        shared_usage="Mit einer eigenen Claude-Code-Konfiguration lässt sich der Verbrauch diesem Profil zuordnen.",
        quotas="Tariflimits", five_hour="5 Stunden", seven_day="7 Tage", reset_at="Rücksetzung {time}",
        observed="Letzte Messung {time}", stale="Älterer Messwert", refresh="Aktualisieren",
        capture_on="Limitanzeige aktivieren", capture_off="Limitanzeige deaktivieren",
        no_limits="Kein aktueller Limitwert. Claude Code meldet Limits nach einer Anfrage mit einem unterstützten Konto.",
        status_conflict="Dieses Profil hat bereits eine eigene Statuszeile. Die Limits sind in Claude Code über /usage abrufbar.",
        scope_usage="Von Claude Code gemeldete Limits", code_settings="Claude Code",
        share_on="Skills und Anweisungen teilen", share_off="Freigabe beenden",
        share_items="Skills, Agents, Commands und CLAUDE.md", local_only="Nur in diesem Profil",
        shared_items="Freigegebene Inhalte", folder_size="Datengröße", unavailable="Nicht verfügbar",
        hold_code="Der separate Claude-Code-Ordner bleibt beim Entfernen dieses Profils erhalten.",
        missing_cli="Claude Code CLI wurde nicht gefunden. Für den Terminalstart wird die Windows-CLI benötigt.",
        hotkey_help="Die Kürzel funktionieren, solange der Manager läuft.", usage_error="Der Verbrauch konnte nicht gelesen werden.",
    ),
}

from profile_translations import PROFILE_TRANSLATIONS

PROFILE_TEXT.update(PROFILE_TRANSLATIONS)

_SHELL = {
    "de": ("Profilbuchstabe im Windows-Infobereich", "Windows-Verknüpfung speichern…", "Die Profilfarbe hebt sich kaum vom Hintergrund ab."),
    "en": ("Show profile initial in the notification area", "Save Windows shortcut…", "This color has low contrast against the background."),
    "pt": ("Mostrar inicial na área de notificação", "Salvar atalho do Windows…", "Esta cor tem pouco contraste com o fundo."),
    "es": ("Mostrar la inicial en el área de notificación", "Guardar acceso directo de Windows…", "Este color tiene poco contraste con el fondo."),
    "fr": ("Afficher l’initiale dans la zone de notification", "Enregistrer un raccourci Windows…", "Cette couleur contraste peu avec le fond."),
    "it": ("Mostra l’iniziale nell’area di notifica", "Salva collegamento Windows…", "Questo colore ha poco contrasto con lo sfondo."),
    "ru": ("Инициал профиля в области уведомлений", "Сохранить ярлык Windows…", "Цвет профиля слабо выделяется на фоне."),
    "ja": ("通知領域にプロファイルの頭文字を表示", "Windows ショートカットを保存…", "この色は背景とのコントラストが低くなります。"),
    "zh": ("在通知区域显示配置文件首字母", "保存 Windows 快捷方式…", "此颜色与背景的对比度较低。"),
    "ko": ("알림 영역에 프로필 이니셜 표시", "Windows 바로 가기 저장…", "선택한 색과 배경의 대비가 낮습니다."),
}
for _language, (_tray, _link, _contrast) in _SHELL.items():
    PROFILE_TEXT[_language].update(tray=_tray, save_link=_link, low_contrast=_contrast)

_WORKSPACE = {
    "de": ("Übersicht", "Profildaten", "Profil wechseln", "Profile durchsuchen", "Kein passendes Profil", "Noch kein Messwert", "Wartet auf die nächste Meldung von Claude Code.", "Limitanzeige ist ausgeschaltet.", "Mehr"),
    "en": ("Overview", "Profile details", "Switch profile", "Search profiles", "No matching profiles", "No reading yet", "Waiting for the next reading from Claude Code.", "Limit readings are turned off.", "More"),
    "pt": ("Visão geral", "Dados do perfil", "Trocar perfil", "Buscar perfis", "Nenhum perfil encontrado", "Sem leitura", "Aguardando a próxima leitura do Claude Code.", "A leitura de limites está desativada.", "Mais"),
    "es": ("Resumen", "Datos del perfil", "Cambiar perfil", "Buscar perfiles", "Ningún perfil coincide", "Sin medición", "Esperando la próxima lectura de Claude Code.", "La lectura de límites está desactivada.", "Más"),
    "fr": ("Aperçu", "Détails du profil", "Changer de profil", "Rechercher un profil", "Aucun profil trouvé", "Aucun relevé", "En attente du prochain relevé de Claude Code.", "Le suivi des limites est désactivé.", "Plus"),
    "it": ("Panoramica", "Dati del profilo", "Cambia profilo", "Cerca profili", "Nessun profilo trovato", "Nessun dato", "In attesa della prossima lettura di Claude Code.", "La lettura dei limiti è disattivata.", "Altro"),
    "ru": ("Обзор", "Данные профиля", "Сменить профиль", "Поиск профилей", "Профиль не найден", "Пока нет данных", "Ожидаются данные от Claude Code.", "Отображение лимитов отключено.", "Ещё"),
    "ja": ("概要", "プロファイル情報", "プロファイルを切り替え", "プロファイルを検索", "該当するプロファイルがありません", "データなし", "Claude Code からの次の値を待っています。", "使用上限の表示は無効です。", "その他"),
    "zh": ("概览", "配置文件信息", "切换配置文件", "搜索配置文件", "找不到匹配的配置文件", "暂无读数", "正在等待 Claude Code 的下一次读数。", "用量限额显示已关闭。", "更多"),
    "ko": ("개요", "프로필 정보", "프로필 전환", "프로필 검색", "일치하는 프로필 없음", "측정값 없음", "Claude Code의 다음 측정값을 기다리는 중입니다.", "한도 표시가 꺼져 있습니다.", "더 보기"),
}
for _language, _labels in _WORKSPACE.items():
    PROFILE_TEXT[_language].update(zip(("overview", "details", "switch_profile", "search_profiles", "no_match",
                                        "no_reading", "wait_reading", "capture_disabled", "more"), _labels, strict=True))

for _language, _label in {
    "de": "Tariflimits · verbraucht", "en": "Plan limits used", "pt": "Uso dos limites do plano",
    "es": "Límites del plan consumidos", "fr": "Limites du forfait utilisées", "it": "Limiti del piano utilizzati",
    "ru": "Использовано от лимита", "ja": "プラン上限の使用率", "zh": "套餐限额使用率", "ko": "요금제 한도 사용률",
}.items():
    PROFILE_TEXT[_language]["quota_used"] = _label
