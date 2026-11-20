using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;

namespace MultiClaude;

public partial class MainWindow
{
    private void EndLogin()
    {
        integration.CancelLogin();
        loginProfile = null;
        LoginBanner.Visibility = Visibility.Collapsed;
        RenderStatus();
    }
    private void CancelLoginClick(object sender, RoutedEventArgs e) => EndLogin();
    private void LoginClick(object sender, RoutedEventArgs e) => Safe(() =>
    {
        if (selected is not { Provider: ProfileProvider.Claude } p) return;
        EndLogin();
        integration.BeginLogin(p);
        loginProfile = p;
        loginDeadline = DateTimeOffset.UtcNow.AddMinutes(5);
        LoginMessage.Text = Format("aguardando", "n", p.Name);
        LoginBanner.Visibility = Visibility.Visible;
        RenderStatus();
    });
    private void PasteClick(object sender, RoutedEventArgs e) => Safe(() =>
    {
        if (loginProfile is not { Provider: ProfileProvider.Claude } p || selected?.CodeKey != p.CodeKey) return;
        var attempt = loginDeadline;
        var dialog = new Form(this, T("colar") + " · " + p.Name, 406, 210);
        Note(dialog.Fields, p.Name, true);
        Note(dialog.Fields, T("colar_pergunta"));
        var field = new PasswordBox { Padding = new Thickness(4), MaxLength = 16000 };
        Identify(field, "ManualLoginLink", T("colar_pergunta"));
        dialog.Fields.Children.Add(field);
        dialog.Action(T("cancelar"), dialog.Close);
        dialog.Action(T("ok"), () =>
        {
            if (loginProfile?.CodeKey != p.CodeKey || selected?.CodeKey != p.CodeKey || attempt != loginDeadline || DateTimeOffset.UtcNow >= attempt)
            { field.Clear(); dialog.Close(); return; }
            if (!WindowsIntegration.ValidLink(field.Password)) { dialog.Warning.Text = T("invalido"); field.Focus(); return; }
            try
            {
                integration.SendLoginLink(p, field.Password);
                field.Clear();
                dialog.DialogResult = true;
                EndLogin();
                Feedback.Text = Format("enviado", "n", p.Name);
                StartStatusRead();
            }
            catch (Exception error) { field.Clear(); dialog.Warning.Text = error.Message; }
        }, true, "ConfirmLoginLink");
        dialog.Loaded += (_, _) => field.Focus();
        dialog.ShowDialog();
        field.Clear();
    });
    private void CloseClick(object sender, RoutedEventArgs e) => Safe(() =>
    {
        if (selected is { } p) CloseProfile(p);
    });
    private void CloseProfile(Profile p)
    {
        if (p.CodeKey == loginProfile?.CodeKey) EndLogin();
        integration.CloseDesktop(p);
        StartStatusRead();
    }
    private void LogoutClick(object sender, RoutedEventArgs e) => Safe(() =>
    {
        if (selected is not { Provider: ProfileProvider.Claude } p || !Confirm(T("sair"), Format("sair_q", "n", p.Name))) return;
        EndLogin();
        integration.SignOut(p);
        Feedback.Text = Format("saiu", "n", p.Name);
        StartStatusRead();
    });
    private void TerminalClick(object sender, RoutedEventArgs e) => Safe(() => Terminal(false));
    private void CodeLoginClick(object sender, RoutedEventArgs e) => Safe(() => Terminal(true));
    private void Terminal(bool login)
    {
        if (selected is not { } p) return;
        try { integration.OpenCode(p, login); }
        catch (FileNotFoundException)
        {
            bool codex = p.Provider == ProfileProvider.Codex;
            var dialog = new Form(this, codex ? "Codex CLI" : T("code_settings"), 390, 172);
            Note(dialog.Fields, T(codex ? "codex_missing_cli" : "missing_cli"));
            dialog.Action(T("fechar"), dialog.Close);
            var docs = new Uri(codex ? "https://developers.openai.com/codex/cli/" : "https://code.claude.com/docs/en/setup");
            dialog.Action(docs.Host + " ↗", () => { OpenInShell(docs.AbsoluteUri); dialog.Close(); });
            dialog.ShowDialog();
        }
    }
    private void CopyCommandClick(object sender, RoutedEventArgs e) => Safe(() =>
    {
        if (selected is not { } p) return;
        Clipboard.SetText(integration.CodeCommand(p));
        Feedback.Text = T("copied");
    });
    private void FolderClick(object sender, RoutedEventArgs e) => Safe(() =>
    {
        if (selected is not { } p) return;
        catalog.ValidateDesktop(p);
        Directory.CreateDirectory(p.DesktopPath);
        OpenInShell(p.DesktopPath);
    });
    private void ShortcutClick(object sender, RoutedEventArgs e) => Safe(() =>
    {
        if (selected is not { } p) return;
        var clean = new string(p.Name.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' ? c : '_').Take(60).ToArray()).Trim();
        var dialog = new SaveFileDialog { Title = T("save_link"), FileName = p.Provider + " - " + clean + ".lnk", DefaultExt = ".lnk", Filter = "Windows|*.lnk" };
        if (dialog.ShowDialog(this) == true) integration.SaveShortcut(p, dialog.FileName);
    });
    private void DeleteClick(object sender, RoutedEventArgs e) => Safe(() =>
    {
        if (selected is not { } p) return;
        var canErase = catalog.CanErase(p);
        var message = Format("excluir_q", "n", p.Name);
        if (!canErase) message += "\n\n" + T("pastas_preservadas");
        var code = integration.CodeDirectory(p);
        if (Directory.Exists(code)) message += "\n\n" + T(p.Provider == ProfileProvider.Codex ? "hold_codex" : "hold_code") + "\n" + code;
        if (!Confirm(T("excluir"), message)) return;
        var erase = canErase && Confirm(T("excluir"), T("excluir_dados"));
        if (p.CodeKey == loginProfile?.CodeKey) EndLogin();
        if (erase) integration.CloseDesktop(p);
        catalog.Delete(p, erase);
        Reload();
    });
}
