using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MultiClaude;

public partial class MainWindow : Window
{
    private readonly Catalog catalog;
    private readonly WindowsIntegration integration;
    private readonly Func<IReadOnlyList<Profile>, Dictionary<string, ResourceReading>> readProcessResources;
    private ProfileBindings? bindings;
    private List<Profile> profiles = [];
    private Profile? selected;
    private string language;
    private bool rendering, closed, wide;
    private readonly DispatcherTimer pulse = new() { Interval = TimeSpan.FromSeconds(3) };
    private Profile? loginProfile;
    private DateTimeOffset loginDeadline;
    private readonly Queue<string> pendingActivations = new();

    public MainWindow(Catalog catalog) : this(catalog, null) { }

    internal MainWindow(Catalog catalog, Func<IReadOnlyList<Profile>, Dictionary<string, ResourceReading>>? readProcessResources)
    {
        this.catalog = catalog;
        integration = new WindowsIntegration(catalog.Home);
        this.readProcessResources = readProcessResources ?? integration.ReadProcessResources;
        language = catalog.Get("idioma") ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        Theme.Apply(catalog.Get("tema") ?? "sistema");
        InitializeComponent();
        DataContext = new Captions(language);
        SourceInitialized += (_, _) =>
        {
            RestoreGeometry();
            bindings = new ProfileBindings(this, id => Safe(() => ActivateById(id)));
        };
        Loaded += (_, _) =>
        {
            Reload(catalog.Get("selected_code_key"));
            CheckInstallation();
            pulse.Start();
            if (catalog.Get("idioma") is null)
                Dispatcher.BeginInvoke(() => { if (!closed) Welcome(); }, DispatcherPriority.ApplicationIdle);
        };
        pulse.Tick += (_, _) =>
        {
            if (!ModalOpen && pendingActivations.TryDequeue(out var key)) ActivateProfile(key);
            if (loginProfile != null && DateTimeOffset.UtcNow >= loginDeadline)
            {
                EndLogin();
                Feedback.Text = T("esgotado");
            }
            if (selected == null) return;
            UpdateUsageDisplay();
            StartStatusRead();
            if (DateTimeOffset.UtcNow >= nextUsage && usageRead == null) RequestUsage(false);
        };
        Closed += (_, _) =>
        {
            closed = true;
            pulse.Stop();
            usageRead?.Cancel();
            bindings?.Dispose();
            SaveGeometry();
        };
    }

    public sealed record Captions(string Language)
    {
        public string this[string key] => Texts.Get(Language, key);
    }
    private string T(string key) => Texts.Get(language, key);
    private string Format(string key, string field, object value) =>
        T(key).Replace("{" + field + "}", Convert.ToString(value, CultureInfo.CurrentCulture), StringComparison.Ordinal);
    private static Visibility ShowIf(bool condition) => condition ? Visibility.Visible : Visibility.Collapsed;
    private void Safe(Action action)
    {
        try { action(); }
        catch (Exception error) { Error(this, error); }
    }
    private void Error(Window owner, Exception error) =>
        MessageBox.Show(owner, error.Message, T("erro"), MessageBoxButton.OK, MessageBoxImage.Warning);
    private bool Confirm(string title, string message) =>
        MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void Reload(string? preferred = null)
    {
        profiles = catalog.Profiles();
        var problems = bindings?.Sync(profiles);
        if (problems?.Count > 0) Feedback.Text = string.Join(" · ", problems);
        var key = preferred ?? selected?.CodeKey ?? catalog.Get("selected_code_key");
        var next = profiles.Find(p => p.CodeKey == key) ?? profiles.FirstOrDefault();
        selected = null;
        SetSelection(next);
    }

    private void SetSelection(Profile? next)
    {
        if (next != null) next = profiles.Find(p => p.CodeKey == next.CodeKey);
        if (next != null && next == selected) return;
        if (loginProfile != null && next?.CodeKey != loginProfile.CodeKey) EndLogin();
        selected = next;
        usageRead?.Cancel();
        usageRead = null;
        snapshot = null;
        usageFailed = false;
        nextUsage = DateTimeOffset.MinValue;
        RenderPicker();
        EmptyState.Visibility = ShowIf(next == null);
        Workspace.Visibility = ShowIf(next != null);
        if (next == null) return;
        catalog.Set("selected_code_key", next.CodeKey);
        SelectedName.Text = next.Name;
        SelectedName.ToolTip = next.Name;
        Avatar.Background = ProfileColor(next);
        Initial.Text = string.IsNullOrEmpty(next.Name) ? "?" : StringInfo.GetNextTextElement(next.Name).ToUpper(CultureInfo.CurrentCulture);
        Portrait.Fill = TryImageBrush(catalog.PhotoFile(next.Photo));
        DataFolder.Text = next.DesktopPath;
        UpdateMetadata(next);
        DataSize.Text = T("folder_size") + "   —";
        CodeMode.Text = T(next.Isolated ? "isolated" : "shared") + (next.Isolated && next.Sharing ? " · " + T("share_items") : "");
        CodeLogin.Visibility = ShowIf(next.Isolated);
        MoveUp.IsEnabled = profiles.IndexOf(next) > 0;
        MoveDown.IsEnabled = profiles.IndexOf(next) < profiles.Count - 1;
        RenderStatus();
        UpdateUsageDisplay();
        StartStatusRead();
        RequestUsage(false);
    }

    private void UpdateMetadata(Profile p)
    {
        var last = long.TryParse(p.LastOpen, out var epoch) && epoch is >= -62135596800 and <= 253402300799
            ? Local(DateTimeOffset.FromUnixTimeSeconds(epoch)) : "—";
        var created = DateTimeOffset.TryParse(p.Created, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var stamp)
            ? Local(stamp) : "—";
        var shortcut = p.Shortcut.Length == 0 ? "—" : p.Shortcut;
        if (p.Shortcut.Length > 0 && bindings?.IsHotkeyRegistered(p.Id) == false) shortcut += " · " + T("hotkey_busy");
        Metadata.Text = $"{T("created")}   {created}     {T("last_open")}   {last}\n{T("hotkey")}   {shortcut}";
        if (p.Tray && bindings?.IsTrayRegistered(p.Id) == false) Metadata.Text += "\n" + T("tray") + " · " + T("unavailable");
    }
    private static string Local(DateTimeOffset stamp) => stamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    private static Brush ProfileColor(Profile p)
    {
        try { return (Brush)new BrushConverter().ConvertFromString(p.Color)!; }
        catch (Exception error) when (error is FormatException or NotSupportedException) { return Brushes.SlateGray; }
    }
    private static ImageBrush? TryImageBrush(string? file)
    {
        if (file == null) return null;
        try
        {
            using var stream = File.OpenRead(file);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 80;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
        }
        catch (Exception error) when (error is IOException or NotSupportedException or UnauthorizedAccessException or FormatException or System.Runtime.InteropServices.COMException)
        { return null; }
    }
    public void ActivateProfile(string codeKey)
    {
        if (closed) return;
        if (ModalOpen) { pendingActivations.Enqueue(codeKey); return; }
        Safe(() => { var profile = catalog.Profiles().FirstOrDefault(p => p.CodeKey == codeKey); if (profile != null) { Reload(profile.CodeKey); OpenSelected(); } });
    }
    private bool ModalOpen => !IsEnabled || OwnedWindows.Cast<Window>().Any(w => w.IsVisible);
    private void ActivateById(long id)
    {
        if (closed) return;
        var profile = profiles.Find(p => p.Id == id);
        if (profile == null) return;
        if (ModalOpen) { pendingActivations.Enqueue(profile.CodeKey); return; }
        SetSelection(profile);
        OpenSelected();
    }
    private void OpenSelected()
    {
        if (selected is not { } p) return;
        integration.OpenDesktop(p);
        catalog.MarkOpened(p);
        var saved = p with { LastOpen = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) };
        profiles = profiles.Select(item => item.CodeKey == p.CodeKey ? saved : item).ToList();
        selected = saved;
        UpdateMetadata(saved);
        if (!status.GetValueOrDefault(p.CodeKey).Open) Feedback.Text = Format("opening", "n", p.Name);
        StartStatusRead();
    }
    private static void OpenInShell(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    private async void CheckInstallation()
    {
        try
        {
            bool available = await Task.Run(integration.DesktopAvailable);
            if (!closed) DesktopInstallationNotice.Visibility = ShowIf(!available);
        }
        catch (Exception) { /* The launch action reports executable discovery errors when used. */ }
    }
    private void AuthorClick(object sender, RoutedEventArgs e) => Safe(() => OpenInShell("https://github.com/RichardKPL"));
    private void NewClick(object sender, RoutedEventArgs e) => Safe(() => Edit(null));
    private void EditClick(object sender, RoutedEventArgs e) => Safe(() => { if (selected != null) Edit(selected); });
    private void ShowSettings(object sender, RoutedEventArgs e) => Safe(Settings);
    private void ImportClick(object sender, RoutedEventArgs e) => Safe(Import);
    private void SearchClick(object sender, RoutedEventArgs e) => Safe(Search);
    private void OpenClick(object sender, RoutedEventArgs e) => Safe(OpenSelected);
    private void UpClick(object sender, RoutedEventArgs e) => Safe(() => MoveSelected(-1));
    private void DownClick(object sender, RoutedEventArgs e) => Safe(() => MoveSelected(1));
}
