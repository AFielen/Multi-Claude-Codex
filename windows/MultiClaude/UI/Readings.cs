using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MultiClaude;

public partial class MainWindow
{
    private CancellationTokenSource? usageRead;
    private UsageSnapshot? snapshot;
    private DateTimeOffset nextUsage;
    private long usageGeneration;
    private bool usageFailed, readingStatus;
    private readonly Dictionary<string, (bool Open, bool Signed)> status = [];
    private readonly Dictionary<string, (LimitWindow? Five, LimitWindow? Week)> limits = [];
    private Dictionary<string, ResourceReading> resourceReadings = [];
    private event Action? IndicatorsChanged;

    private void RefreshAfterTrayRestore()
    {
        RenderStatus();
        UpdateRowIndicators();
        UpdateUsageDisplay();
        StartStatusRead();
        RequestUsage(true);
    }

    private async void RequestUsage(bool force)
    {
        if (selected is not { } profile || closed || !IsVisible || (!force && usageRead != null)) return;
        usageRead?.Cancel();
        usageGeneration++;
        using var pending = new CancellationTokenSource();
        usageRead = pending;
        var token = pending.Token;
        try
        {
            var reading = profile is { Provider: ProfileProvider.Claude, Isolated: true }
                ? await Task.Run(() => Usage.Read(profile, token), token) : null;
            if (usageRead != pending || token.IsCancellationRequested || closed || !IsVisible) return;
            snapshot = reading;
            usageFailed = false;
            if (reading != null) limits[profile.CodeKey] = (reading.FiveHour, reading.SevenDay);
            UpdateRowIndicators();
            UpdateUsageDisplay();
            (long Bytes, bool Partial) size;
            try { size = await Task.Run(() => Usage.FolderSize(catalog, profile, token), token); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                if (usageRead == pending && !closed && IsVisible && !token.IsCancellationRequested) DataSize.Text = T("folder_size") + "   —";
                return;
            }
            if (usageRead != pending || token.IsCancellationRequested || closed || !IsVisible) return;
            var gib = size.Bytes >= 1024L * 1024 * 1024;
            DataSize.Text = T("folder_size") + "   " + (size.Partial ? "≥ " : "") +
                (size.Bytes / (gib ? 1073741824d : 1048576d)).ToString("N1") + (gib ? " GiB" : " MiB");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (usageRead != pending || token.IsCancellationRequested || closed || !IsVisible) return;
            usageFailed = true;
            UpdateUsageDisplay();
        }
        finally
        {
            if (usageRead == pending)
            {
                usageRead = null;
                nextUsage = DateTimeOffset.UtcNow.AddMinutes(1);
            }
        }
    }
    private void UpdateUsageDisplay()
    {
        if (!IsVisible || selected is not { Provider: ProfileProvider.Claude } p || OverviewTab == null) return;
        var now = DateTimeOffset.UtcNow;
        var stale = snapshot?.Observed is { } observed && (now - observed).TotalMinutes > 15;
        var capture = snapshot?.Capture == true;
        var five = snapshot?.FiveHour;
        var week = snapshot?.SevenDay;
        if (five?.Resets <= now) five = null;
        if (week?.Resets <= now) week = null;
        var hasLimits = five != null || week != null;
        var hasTokens = snapshot is { Requests: > 0 };
        var message = !p.Isolated ? T("shared_usage") : usageFailed ? T("usage_error") :
            snapshot == null ? T("loading") : "";
        QuotaMessage.Text = message.Length > 0 ? message : hasLimits && snapshot?.Observed is { } date
            ? Format("observed", "time", Local(date)) + (stale ? " · " + T("stale") : "")
            : T(capture ? "wait_reading" : "capture_disabled");
        LimitsGrid.Visibility = ShowIf(p.Isolated && hasLimits);
        SetMeter(five, FivePercent, FiveBar, FiveReset);
        SetMeter(week, WeekPercent, WeekBar, WeekReset);
        void SetMeter(LimitWindow? window, TextBlock label, ProgressBar bar, TextBlock reset)
        {
            label.Text = window != null ? window.Percent.ToString("0.##") + " %" : "—";
            reset.Text = window != null ? Format("reset_at", "time", Local(window.Resets)) : "";
            bar.Value = window?.Percent ?? 0;
            bar.Foreground = stale ? (Brush)FindResource("Muted") : bar.Value > 80 ? Brushes.DarkOrange : ProfileColor(p);
        }
        RefreshOverview.Visibility = RefreshCode.Visibility = ShowIf(p.Isolated);
        OverviewCapture.Visibility = ShowIf(p.Isolated && snapshot != null && !capture);
        CodeCapture.Visibility = ShowIf(p.Isolated && snapshot != null);
        OverviewCapture.Content = CodeCapture.Content = T(capture ? "capture_off" : "capture_on");
        TokenTotal.Visibility = TokenDetails.Visibility = ShowIf(p.Isolated && hasTokens);
        WideTokens.Visibility = ShowIf(wide && p.Isolated && hasTokens);
        TokenMessage.Text = message.Length > 0 ? message : hasTokens ? Format("sample_count", "n", snapshot!.Requests) : T("no_usage");
        if (snapshot is { } s)
        {
            if (s.Partial) TokenMessage.Text += " " + T("partial");
            TokenTotal.Text = s.Total.ToString("N0");
            WideTokens.Text = T("code_tokens") + "   " + TokenTotal.Text;
            TokenDetails.Text = $"{T("tokens_in")}   {s.Input:N0}      {T("tokens_out")}   {s.Output:N0}\n{T("cache_read")}   {s.CacheRead:N0}      {T("cache_write")}   {s.CacheWrite:N0}";
        }
    }

    private async void StartStatusRead()
    {
        if (closed || readingStatus) return;
        var profile = selected;
        readingStatus = true;
        var batch = profiles.ToArray();
        var generation = usageGeneration;
        bool readDetails = IsVisible;
        try
        {
            if (!readDetails || profile == null)
            {
                var running = await Task.Run(() =>
                {
                    integration.PruneWindowIcons();
                    return batch.Select(p => (p.CodeKey, Open: integration.IsRunning(p))).ToArray();
                });
                if (!closed)
                    foreach (var row in running)
                        status[row.CodeKey] = (row.Open, status.GetValueOrDefault(row.CodeKey).Signed);
                return;
            }
            var (updates, readings) = await Task.Run(() =>
            {
                integration.PruneWindowIcons();
                Dictionary<string, ResourceReading> resources;
                try { resources = readProcessResources(batch); }
                catch (Exception) { resources = []; }
                var rows = batch.Select(p =>
                {
                    bool open = integration.IsRunning(p);
                    bool signed = p.Provider == ProfileProvider.Claude && integration.IsSignedIn(p);
                    (DateTimeOffset? Observed, LimitWindow? Five, LimitWindow? Week) saved = default;
                    try { if (p.Provider == ProfileProvider.Claude) saved = Usage.ReadLimits(p); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException) { }
                    return (p.CodeKey, open, signed, saved);
                }).ToArray();
                return (rows, resources);
            });
            if (closed) return;
            resourceReadings = readings;
            foreach (var row in updates)
            {
                status[row.CodeKey] = (row.open, row.signed);
                if (selected?.CodeKey != row.CodeKey || generation == usageGeneration)
                    limits[row.CodeKey] = (row.saved.Five, row.saved.Week);
                if (snapshot != null && selected?.CodeKey == row.CodeKey && usageRead == null && generation == usageGeneration)
                    snapshot = snapshot with { Observed = row.saved.Observed, FiveHour = row.saved.Five, SevenDay = row.saved.Week };
            }
            RenderStatus();
            UpdateRowIndicators();
            UpdateUsageDisplay();
        }
        catch (Exception)
        {
            if (readDetails && !closed && IsVisible)
            {
                resourceReadings.Clear();
                RenderStatus();
                UpdateRowIndicators();
                if (profile != null && selected?.CodeKey == profile.CodeKey) DesktopStatus.Text = T("unavailable");
            }
        }
        finally
        {
            readingStatus = false;
            if (!closed && selected != null && (selected.CodeKey != profile?.CodeKey || !readDetails && IsVisible))
                StartStatusRead();
        }
    }
    private (string Mark, string Caption, Brush Ink) Indicator(Profile p)
    {
        var open = status.TryGetValue(p.CodeKey, out var state) && state.Open;
        var mark = open ? "●" : "○";
        if (p is { Provider: ProfileProvider.Claude, Isolated: true } && limits.TryGetValue(p.CodeKey, out var values))
        {
            var now = DateTimeOffset.UtcNow;
            var warning = new[] { (Key: "five_hour", Window: values.Five), (Key: "seven_day", Window: values.Week) }
                .Where(x => x.Window is { Percent: > 80 } window && window.Resets > now)
                .OrderByDescending(x => x.Window!.Percent).FirstOrDefault();
            if (warning.Window != null)
                return (mark, p.Provider + " · " + warning.Window.Percent.ToString("0.##") + " % · " + T(warning.Key), Brushes.DarkOrange);
        }
        return (mark, p.Shortcut.Length == 0 || bindings?.IsHotkeyRegistered(p.Id) == false ? DesktopStateText(p, open) : p.Provider + " · " + p.Shortcut,
            open ? Brushes.SeaGreen : (Brush)FindResource("Muted"));
    }
    private string DesktopStateText(Profile p, bool open) =>
        (p.Provider == ProfileProvider.Codex ? "Codex" : "Claude Desktop") + " · " + T(open ? "aberto" : "fechado");

    private void UpdateRowIndicators()
    {
        if (!IsVisible) return;
        foreach (var p in profiles)
        {
            if (!rowIndicators.TryGetValue(p.CodeKey, out var row)) continue;
            var indicator = Indicator(p);
            row.Caption.Text = indicator.Caption;
            row.Caption.Foreground = indicator.Ink;
            row.Dot.Text = indicator.Mark;
            row.Dot.Foreground = status.GetValueOrDefault(p.CodeKey).Open ? Brushes.SeaGreen : (Brush)FindResource("Muted");
            SetResourceText(row.Resources, p);
        }
        IndicatorsChanged?.Invoke();
    }
    private void RenderStatus()
    {
        if (!IsVisible || selected is not { } p) return;
        SetResourceText(ProfileResources, p);
        bool known = status.TryGetValue(p.CodeKey, out var state);
        var waiting = loginProfile?.CodeKey == p.CodeKey;
        bool claude = p.Provider == ProfileProvider.Claude;
        DesktopStatus.Text = known ? DesktopStateText(p, state.Open) : p.Provider + " · …";
        if (known && claude) DesktopStatus.Text += " · " + T(waiting ? "aguard" : state.Signed ? "conectado" : "desconectado");
        OpenProfile.Content = T(known && state.Open ? "switch" : "abrir");
        SignIn.Visibility = ShowIf(claude && !state.Signed && !waiting);
        SignOut.Visibility = ShowIf(claude && state.Signed);
        CloseDesktopButton.Visibility = ShowIf(state.Open);
    }
    private void SetResourceText(TextBlock label, Profile profile)
    {
        if (!resourceReadings.TryGetValue(profile.CodeKey, out var reading))
        {
            label.Text = "";
            label.Visibility = Visibility.Collapsed;
            return;
        }
        var cpu = reading.CpuPercent is double percent ? $"{percent:0.#} %" : "—";
        var ram = reading.RamBytes switch
        {
            null => "—",
            long bytes when bytes < (1L << 30) => $"{bytes / 1048576d:0} MiB",
            long bytes => $"{bytes / 1073741824d:0.#} GiB"
        };
        label.Text = $"CPU {cpu} · RAM {ram}";
        label.Visibility = Visibility.Visible;
    }
    private void RefreshClick(object sender, RoutedEventArgs e) => RequestUsage(true);
    private void ToggleCapture(object sender, RoutedEventArgs e) => Safe(() =>
    {
        if (selected is not { Provider: ProfileProvider.Claude, Isolated: true } p || snapshot == null) return;
        if (!Usage.SetCapture(p, !snapshot.Capture)) MessageBox.Show(this, T("status_conflict"), T("quotas"));
        RequestUsage(true);
    });
}
