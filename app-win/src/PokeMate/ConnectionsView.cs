using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PokeMate;

/// 연결 화면 (맥 ConnectionsView). `pokemate status` 결과를 회사별로 묶어 보여 주고, 연결·다시 연결·일괄 연동을 한다.
[PopoverTab(SidebarItem.Connections)]
public sealed class ConnectionsView : UserControl
{
    readonly AppModel model;
    readonly StackPanel list = new();
    readonly StackPanel footer = new();
    readonly TextBlock progress = Ui.Text("", 12, "TextSecondary");
    string? resultMessage;

    public ConnectionsView(AppModel model)
    {
        this.model = model;
        var column = new DockPanel();

        var header = new DockPanel { Margin = new Thickness(12, 0, 12, 10) };
        var refresh = EditorStyle.GlyphButton("", () => _ = model.RefreshSystems(), "새로고침", id: "ConnectionsRefresh");
        DockPanel.SetDock(refresh, Dock.Right);
        header.Children.Add(refresh);
        progress.Text = "";
        DockPanel.SetDock(progress, Dock.Right);
        progress.Margin = new Thickness(0, 0, 8, 0);
        progress.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(progress);
        header.Children.Add(Ui.Text("연결", 15, "TextFg", FontWeights.SemiBold));
        DockPanel.SetDock(header, Dock.Top);
        column.Children.Add(header);

        var bottom = new StackPanel();
        bottom.Children.Add(EditorStyle.Divider(0));
        var group = ChatUi.Primary("일괄 연동", () => OpenLogin(new ConnectTarget.Group("ad"), "사내 계정 일괄 연동"));
        ChatUi.Id(group, "ConnectAll");
        group.Margin = new Thickness(12, 10, 12, 12);
        bottom.Children.Add(footer);
        bottom.Children.Add(group);
        DockPanel.SetDock(bottom, Dock.Bottom);
        column.Children.Add(bottom);

        column.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 0, 4) });
        Content = column;

        Loaded += (_, _) =>
        {
            model.PropertyChanged += OnModelChanged;
            Render();
            _ = model.RefreshSystems();
        };
        Unloaded += (_, _) => model.PropertyChanged -= OnModelChanged;
    }

    void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppModel.Systems) or nameof(AppModel.Microsoft365) or nameof(AppModel.ConnectionError) or nameof(AppModel.IsLoadingSystems))
            Render();
    }

    List<string> Companies()
    {
        var seen = new List<string>();
        foreach (var system in model.Systems)
        {
            var company = system.Company ?? "기타";
            if (!seen.Contains(company)) seen.Add(company);
        }
        return seen;
    }

    void Render()
    {
        progress.Text = model.IsLoadingSystems ? "확인 중…" : "";
        list.Children.Clear();
        // connections 파일의 company 로 묶는다 (처음 나온 순서). 회사 이름은 로컬 파일에만 있다.
        foreach (var company in Companies())
        {
            var title = Ui.Text(company, 12, "TextSecondary", FontWeights.SemiBold);
            title.Margin = new Thickness(16, 8, 16, 4);
            list.Children.Add(title);
            var rows = new StackPanel { Margin = new Thickness(12, 0, 12, 0) };
            foreach (var system in model.Systems.Where(s => (s.Company ?? "기타") == company))
            {
                if (rows.Children.Count > 0) rows.Children.Add(EditorStyle.Divider(0));
                rows.Children.Add(Row(system));
            }
            list.Children.Add(Ui.Card(rows, 8).With(c => c.Margin = new Thickness(12, 0, 12, 6)));
        }

        footer.Children.Clear();
        if (model.ConnectionError is { } error)
        {
            var text = Ui.Text(error, 12, "TextFg", wrap: true);
            text.Foreground = Brushes.IndianRed;
            text.Margin = new Thickness(12, 6, 12, 0);
            ChatUi.Id(text, "ConnectionError");
            footer.Children.Add(text);
        }
        if (resultMessage != null)
        {
            var text = Ui.Text(resultMessage, 13, "TextFg", wrap: true);
            text.Margin = new Thickness(12, 6, 12, 0);
            ChatUi.Id(text, "ConnectionResult");
            footer.Children.Add(text);
        }
    }

    UIElement Row(SystemStatus system)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4), MinHeight = 26 };
        var name = Ui.Text(system.Name, 13);
        name.Width = 110;
        name.VerticalAlignment = VerticalAlignment.Center;
        ChatUi.Id(name, "ConnectionName:" + system.System);

        switch (system.Status)
        {
            case ConnectionState.Unavailable:
                row.Children.Add(name);
                var soon = Ui.Text("준비 중", 13, "TextSecondary");
                soon.HorizontalAlignment = HorizontalAlignment.Right;
                soon.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(soon);
                break;
            case ConnectionState.External:
                // claude.ai 커넥터: 연결·해제는 claude.ai 설정에서 한다.
                var open = Ui.Pill("설정 열기", () => Opener.Open(ConnectorStatus.SettingsUrl.ToString()), "claude.ai 커넥터 설정에서 Microsoft 365 를 연결·해제해요");
                ChatUi.Id(open, "ConnectionOpenSettings");
                DockPanel.SetDock(open, Dock.Right);
                row.Children.Add(open);
                row.Children.Add(name);
                row.Children.Add(model.Microsoft365 is { } status
                    ? StatusLabel(status == ConnectionState.Connected ? ConnectionState.Connected : ConnectionState.Disconnected, system.System)
                    : Ui.Text("확인 중", 13, "TextSecondary"));
                break;
            default:
                var connect = Ui.Pill(system.Status == ConnectionState.Disconnected ? "연결" : "다시 연결", () =>
                {
                    if (system.Auth == "gh")
                        OpenGitHub(Uri.TryCreate(system.BaseUrl ?? "", UriKind.Absolute, out var uri) ? uri.Host : "github.com");
                    else
                        OpenLogin(new ConnectTarget.System(system.System), system.Name);
                });
                ChatUi.Id(connect, "ConnectionConnect:" + system.System);
                DockPanel.SetDock(connect, Dock.Right);
                row.Children.Add(connect);
                row.Children.Add(name);
                row.Children.Add(StatusLabel(system.Status, system.System));
                break;
        }
        return row;
    }

    static UIElement StatusLabel(ConnectionState status, string id)
    {
        var (glyph, text, brush) = status switch
        {
            ConnectionState.Connected => ("", "연결됨", "#FF34C759"),
            ConnectionState.Disconnected => ("", "연결 안 됨", ""),
            ConnectionState.Expired => ("", "만료됨", "#FFFF9500"),
            _ => ("", "", ""),
        };
        if (text.Length == 0) return new Border();
        var label = ChatUi.Label(glyph, text, brush.Length == 0 ? "TextSecondary" : "TextFg", 13);
        if (brush.Length > 0)
        {
            var color = (Color)ColorConverter.ConvertFromString(brush);
            foreach (var child in label.Children.OfType<TextBlock>()) child.Foreground = new SolidColorBrush(color);
        }
        label.VerticalAlignment = VerticalAlignment.Center;
        ChatUi.Id(label, "ConnectionStatus:" + id);
        return label;
    }

    // MARK: 로그인 창

    void OpenLogin(ConnectTarget target, string title)
    {
        var dialog = new LoginDialog(title, async (username, secret) =>
        {
            var results = await model.Connect(target, username, secret);
            resultMessage = string.Join("\n", results.Where(r => r.Status != "unavailable")
                .Select(r => (r.System.Length == 0 ? "" : $"{r.System}: ") + r.Message));
            Render();
            return results.All(r => r.Status is "connected" or "unavailable");
        });
        dialog.ShowModal();
    }

    void OpenGitHub(string host)
    {
        new GitHubLoginDialog(host, model.Settings).ShowModal();
        _ = model.RefreshSystems();
    }
}

static class UiExtensions
{
    public static T With<T>(this T value, Action<T> action) { action(value); return value; }
}

/// 아이디·비밀번호 로그인 (맥 LoginSheet)
sealed class LoginDialog
{
    readonly Window window;

    public LoginDialog(string title, Func<string, string, Task<bool>> submit)
    {
        var body = new StackPanel { Margin = new Thickness(20) };
        body.Children.Add(Ui.Text(title, 15, "TextFg", FontWeights.SemiBold));
        var caption = Ui.Text("사내 계정(AD) 아이디와 비밀번호를 입력하세요. 비밀번호는 이 PC 의 Windows 자격 증명 관리자에만 저장돼요.", 11, "TextSecondary", wrap: true);
        caption.Margin = new Thickness(0, 8, 0, 12);
        body.Children.Add(caption);

        var username = "";
        var secret = "";
        var working = false;
        Button connect = null!;
        var (userFrame, userBox) = EditorStyle.TextField("", t => { username = t; Update(); }, "아이디", id: "LoginUser");
        body.Children.Add(userFrame);
        var (passFrame, passBox) = EditorStyle.PasswordField(t => { secret = t; Update(); }, "LoginSecret", "비밀번호");
        passFrame.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(passFrame);
        var failed = Ui.Text("연결하지 못했어요. 아래 결과 메시지를 확인해 주세요.", 11, "TextFg", wrap: true);
        failed.Foreground = Brushes.IndianRed;
        failed.Visibility = Visibility.Collapsed;
        failed.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(failed);

        window = EditorStyle.Dialog(title, 360, body);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = Ui.Pill("취소", () => window.DialogResult = false);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        ChatUi.Id(cancel, "LoginCancel");

        connect = ChatUi.Primary("연결", async () =>
        {
            working = true;
            Update();
            var ok = await submit(username, secret);
            working = false;
            passBox.Password = "";
            secret = "";
            if (ok) window.DialogResult = true;
            else { failed.Visibility = Visibility.Visible; Update(); }
        }, enabled: false);
        ChatUi.Id(connect, "LoginSubmit");
        buttons.Children.Add(cancel);
        buttons.Children.Add(connect);
        body.Children.Add(buttons);
        EditorStyle.ButtonKeys(window, new[] { cancel, connect }, 1);
        window.Loaded += (_, _) => userBox.Focus();
        void Update() => connect.IsEnabled = username.Length > 0 && secret.Length > 0 && !working;
    }

    public bool? ShowModal() => EditorStyle.ShowModal(window);
}

/// GitHub 로그인: 한 번용 코드를 보여 주고 브라우저에서 승인받는다 (GitHub CLI 로그인을 메이트가 대신 실행). 맥 GitHubLoginSheet
sealed class GitHubLoginDialog
{
    readonly Window window;
    readonly GitHubLogin login = new();
    readonly StackPanel content = new();
    string? code;
    bool? result;

    public GitHubLoginDialog(string host, AppSettings settings)
    {
        var body = new StackPanel { Margin = new Thickness(20) };
        body.Children.Add(Ui.Text($"GitHub 연결 ({host})", 15, "TextFg", FontWeights.SemiBold));
        content.Margin = new Thickness(0, 12, 0, 12);
        body.Children.Add(content);
        window = EditorStyle.Dialog($"GitHub 연결 ({host})", 380, body);
        var close = Ui.Pill("취소", () => { login.Cancel(); window.DialogResult = false; });
        ChatUi.Id(close, "GitHubLoginClose");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        body.Children.Add(close);
        Render(close);
        window.Closing += (_, _) => login.Cancel();
        window.Loaded += (_, _) =>
            login.Start(host, settings, e =>
            {
                switch (e)
                {
                    case GitHubLogin.Event.Code c: code = c.Value; break;
                    case GitHubLogin.Event.Finished f: result = f.Ok; break;
                }
                Render(close);
            });
    }

    void Render(Button close)
    {
        content.Children.Clear();
        if (result is bool ok)
        {
            var label = ChatUi.Label(ok ? "" : "", ok ? "연결됐어요." : "연결하지 못했어요. 다시 시도해 주세요.", "TextFg", 13);
            foreach (var child in label.Children.OfType<TextBlock>()) child.Foreground = ok ? Brushes.SeaGreen : Brushes.IndianRed;
            content.Children.Add(label);
            close.Content = "닫기";
        }
        else if (code is { } value)
        {
            content.Children.Add(Ui.Text("브라우저가 열리면 아래 코드를 입력하고 승인해 주세요.", 13, "TextFg", wrap: true));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var codeText = new TextBox { Text = value, IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 20, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
            codeText.SetResourceReference(Control.ForegroundProperty, "TextFg");
            row.Children.Add(codeText);
            var copy = Ui.Pill("복사", () => { try { Clipboard.SetText(value); } catch { } });
            copy.Margin = new Thickness(10, 0, 0, 0);
            row.Children.Add(copy);
            content.Children.Add(row);
        }
        else content.Children.Add(Ui.Text("로그인 준비 중…", 13, "TextSecondary"));
    }

    public bool? ShowModal() => EditorStyle.ShowModal(window);
}
