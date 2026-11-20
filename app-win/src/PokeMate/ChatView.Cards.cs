using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace PokeMate;

/// 대화 안의 메시지 줄과 제안·승인 카드들 (맥 ChatView.swift 의 MessageRow … PermissionCard)
public sealed partial class ChatView
{
    static FrameworkElement Thumbnail(string path, double size) => ChatUi.Thumbnail(path, size);

    static T Gap<T>(T element, double top) where T : FrameworkElement
    {
        element.Margin = new Thickness(0, top, 0, 0);
        return element;
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { App.Log($"열지 못했어요: {e.Message}"); }
    }

    static void OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) { App.Log($"폴더를 열지 못했어요: {e.Message}"); }
    }

    // MARK: 메시지 줄

    UIElement MessageRow(ChatMessage message)
    {
        var user = message.Role == ChatMessage.RoleKind.User;
        var column = new StackPanel { HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Stretch };
        if (message.Session is { } session)
        {
            var label = ChatUi.Label("", session, "TextSecondary", 11);
            label.Margin = new Thickness(0, 0, 0, 6);
            label.HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            column.Children.Add(label);
        }
        if (message.Images.Count > 0)
        {
            var images = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left };
            foreach (var path in message.Images)
            {
                var thumb = Thumbnail(path, 120);
                thumb.Margin = new Thickness(0, 0, 6, 0);
                images.Children.Add(thumb);
            }
            column.Children.Add(images);
        }
        if (message.Text.Length > 0)
        {
            var bubble = new Border { Padding = new Thickness(10), CornerRadius = new CornerRadius(10), Child = new MarkdownContent(message.Text) };
            bubble.HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
            switch (message.Role)
            {
                case ChatMessage.RoleKind.User:
                    bubble.Background = Ui.Alpha(Color.FromRgb(0xFF, 0x95, 0x00), 0.18);
                    break;
                case ChatMessage.RoleKind.Assistant:
                    bubble.SetResourceReference(Border.BackgroundProperty, "CardBg");
                    break;
                default:
                    bubble.Background = Ui.Alpha(Color.FromRgb(0xE5, 0x39, 0x35), 0.15);
                    break;
            }
            var menu = new ContextMenu();
            menu.Items.Add(ChatUi.Item("복사", () =>
            {
                try { Clipboard.SetText(message.Text); } catch (Exception e) { App.Log($"복사하지 못했어요: {e.Message}"); }
            }));
            bubble.ContextMenu = menu;
            column.Children.Add(bubble);
        }
        foreach (var code in ToolErrorCode.Detect(message.Text).Distinct())
        {
            switch (code)
            {
                case ToolErrorCode.AuthExpired a:
                    column.Children.Add(ReconnectButton(a.System));
                    break;
                case ToolErrorCode.NotConnected n:
                    column.Children.Add(ReconnectButton(n.System));
                    break;
            }
        }
        return column;
    }

    UIElement ReconnectButton(string system)
    {
        var button = Ui.Pill($"{system} 다시 연결", () => model.Selected = SidebarItem.Connections);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Margin = new Thickness(0, 6, 0, 0);
        return button;
    }

    // MARK: 보낼 곳 제안

    UIElement RouteSuggestCard(ChatMessage message, string repo)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = ChatUi.Glyph("", "OrangeBrush", 13);
        icon.Margin = new Thickness(0, 0, 8, 0);
        row.Children.Add(icon);
        var text = Ui.Text($"{repo} 작업 세션으로 보낼까요?", 13, "TextFg", wrap: true, maxLines: 2);
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        UIElement action;
        if (message.RouteSent) action = Ui.Text("보냈어요", 11, "TextSecondary");
        else action = Ui.Pill("보내기", () =>
        {
            ChatSession.WorkHost.Create(repo);
            chat.SendSuggested(message.Id, repo);
        });
        ((FrameworkElement)action).VerticalAlignment = VerticalAlignment.Center;
        ((FrameworkElement)action).Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(action, 2);
        row.Children.Add(action);
        return ChatUi.Tint(row, 0.08, 10, 10);
    }

    // MARK: 알림 제안

    /// 답변이 제안한 알림. [알림에 등록]을 눌러야 알림 메뉴에 들어간다.
    UIElement AlarmProposalCard(ChatMessage message)
    {
        var alarms = message.Alarms;
        var registered = alarms.All(a => model.Alarms.Any(x => x.Id == a.Id));
        // 들어갈 그룹. 직접 말한 이름, 없으면 같은 제목 여러 개일 때 그 제목 (AppModel.Register 와 같은 규칙).
        string? group = alarms.Select(a => a.Group).FirstOrDefault(g => g != null);
        if (group == null && alarms.Count > 1 && alarms.Select(a => a.Title).Distinct().Count() == 1) group = alarms[0].Title;

        var panel = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(ChatUi.Label("", $"알림 {alarms.Count}개", "TextFg", 13, FontWeights.Medium));
        if (group != null)
        {
            var g = ChatUi.Label("", group, "TextSecondary", 11);
            g.Margin = new Thickness(8, 0, 0, 0);
            title.Children.Add(g);
        }
        panel.Children.Add(title);
        foreach (var alarm in alarms)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            line.Children.Add(Ui.Text(alarm.TimeText, 13));
            var rule = Ui.Text(alarm.RepeatRule.Description, 13, "TextSecondary");
            rule.Margin = new Thickness(6, 0, 0, 0);
            line.Children.Add(rule);
            var t = Ui.Text(alarm.Title, 13);
            t.Margin = new Thickness(6, 0, 0, 0);
            t.MaxWidth = 240;
            line.Children.Add(t);
            panel.Children.Add(line);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        if (registered)
        {
            actions.Children.Add(ChatUi.Label("", "알림 메뉴에 등록했어요", "TextFg", 13, glyphBrush: "AccentBrush"));
            var open = ChatUi.Link("알림 보기", () => model.Selected = SidebarItem.Alarms);
            open.Margin = new Thickness(10, 0, 0, 0);
            actions.Children.Add(open);
        }
        else actions.Children.Add(ChatUi.Id(ChatUi.Primary("알림에 등록", () => { model.Register(alarms); OnMessageUpdated(message.Id); }), "AlarmRegister"));
        panel.Children.Add(actions);
        return ChatUi.Box(panel);
    }

    // MARK: 규칙 제안

    /// 답변이 제안한 규칙. [규칙에 저장]을 눌러야 이 PC 의 규칙 파일에 들어간다.
    UIElement RuleProposalCard(ChatMessage message)
    {
        var notes = message.Rules;
        // 기억만 / 잊기만 / 그 밖(규칙)에 따라 제목·버튼 말을 바꾼다.
        (string title, string glyph, string button, string done) kind =
            notes.All(n => n.IsForget) ? ("잊기", "", "잊기", "지웠어요")
            : notes.All(n => n.IsMemory) ? ("기억", "", "기억하기", "기억했어요. 새 대화에서도 알아요")
            : ("규칙", "", "규칙에 저장", "저장했어요. 다음 대화부터 늘 적용돼요");
        var panel = new StackPanel();
        panel.Children.Add(ChatUi.Label(kind.glyph, $"{kind.title} {notes.Count}개", "TextFg", 13, FontWeights.Medium));
        foreach (var note in notes)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var tag = Ui.Text(note.IsForget ? "지울 것" : note.IsMemory ? "기억" : $"{note.File}.md", 11, "TextSecondary");
            tag.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            tag.Margin = new Thickness(0, 2, 6, 0);
            tag.VerticalAlignment = VerticalAlignment.Top;
            line.Children.Add(tag);
            var text = Ui.Text(note.Text, 13, "TextFg", wrap: true);
            text.MaxWidth = 300;
            if (note.IsForget) text.TextDecorations = TextDecorations.Strikethrough;
            line.Children.Add(text);
            panel.Children.Add(line);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        if (message.RulesSaved)
        {
            actions.Children.Add(ChatUi.Label("", kind.done, "TextFg", 13, glyphBrush: "AccentBrush"));
            var open = ChatUi.Link("폴더 열기", () => OpenFolder(Rules.MyDir));
            open.Margin = new Thickness(10, 0, 0, 0);
            actions.Children.Add(open);
        }
        else actions.Children.Add(ChatUi.Id(ChatUi.Primary(kind.button, () =>
        {
            foreach (var note in notes)
            {
                try
                {
                    if (note.IsForget) Memory.Forget(note.Text, note.File);
                    else if (note.IsMemory) Memory.Add(note.Text);
                    else Rules.Append(note.Text, note.File);
                }
                catch (Exception e) { App.Log($"규칙을 저장하지 못했어요: {e.Message}"); }
            }
            chat.Update(message.Id, m => m.RulesSaved = true);
        }), "RuleSave"));
        panel.Children.Add(actions);
        return ChatUi.Box(panel);
    }

    // MARK: 메모 제안

    /// 답변이 제안한 메모. [메모에 저장]을 눌러야 notes/ 에 md 파일로 저장된다.
    UIElement NoteProposalCard(ChatMessage message)
    {
        var panel = new StackPanel();
        panel.Children.Add(ChatUi.Label("", $"메모 {message.Notes.Count}개", "TextFg", 13, FontWeights.Medium));
        foreach (var draft in message.Notes)
        {
            var item = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            item.Children.Add(Ui.Text(draft.Title.Length == 0 ? "(제목 없음)" : draft.Title, 13, "TextFg", FontWeights.SemiBold));
            item.Children.Add(Ui.Text(draft.Body, 11, "TextSecondary", wrap: true, maxLines: 4));
            panel.Children.Add(item);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        if (message.NotesSaved.Count == 0)
            actions.Children.Add(ChatUi.Primary("메모에 저장", () =>
            {
                var paths = new List<string>();
                foreach (var draft in message.Notes)
                {
                    try { paths.Add(Notes.Create(draft.Title, draft.Body)); }
                    catch (Exception e) { App.Log($"메모를 저장하지 못했어요: {e.Message}"); }
                }
                chat.Update(message.Id, m => m.NotesSaved = paths);
            }));
        else
        {
            actions.Children.Add(ChatUi.Label("", "메모에 저장했어요", "TextFg", 13, glyphBrush: "AccentBrush"));
            var first = message.NotesSaved[0];
            var open = ChatUi.Link("메모 보기", () =>
            {
                model.OpenNote = new Uri(first);
                model.Selected = SidebarItem.Notes;
            });
            open.Margin = new Thickness(10, 0, 0, 0);
            actions.Children.Add(open);
        }
        panel.Children.Add(actions);
        return ChatUi.Box(panel);
    }

    // MARK: worklog 제안

    /// 답변이 제안한 worklog. [기록하기]를 눌러야 Jira 에 기록된다. 이미 기록한 건 다시 보내지 않는다.
    UIElement WorklogCard(ChatMessage message)
    {
        var drafts = message.Worklogs;
        var done = message.WorklogsDone.ToHashSet();
        var total = drafts.Sum(d => d.Minutes);
        var panel = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(ChatUi.Label("", $"Work log {drafts.Count}건", "TextFg", 13, FontWeights.Medium));
        var sum = Ui.Text($"합계 {total / 60}시간{(total % 60 > 0 ? $" {total % 60}분" : "")}", 11, "TextSecondary");
        sum.Margin = new Thickness(8, 0, 0, 0);
        sum.VerticalAlignment = VerticalAlignment.Center;
        title.Children.Add(sum);
        panel.Children.Add(title);
        foreach (var draft in drafts)
        {
            var isDone = done.Contains(draft.Id);
            var line = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var mark = ChatUi.Glyph(isDone ? "" : "", isDone ? "AccentBrush" : "TextSecondary", 13);
            mark.VerticalAlignment = VerticalAlignment.Top;
            mark.Margin = new Thickness(0, 2, 6, 0);
            line.Children.Add(mark);
            var text = new StackPanel();
            text.Children.Add(Ui.Text($"{draft.IssueKey}{(draft.Summary.Length == 0 ? "" : $" · {draft.Summary}")}", 13, "TextFg", FontWeights.Medium));
            text.Children.Add(Ui.Text($"{draft.Date} {draft.TimeText}{(draft.Comment.Length == 0 ? "" : $" · {draft.Comment}")}", 11, "TextSecondary"));
            Grid.SetColumn(text, 1);
            line.Children.Add(text);
            panel.Children.Add(line);
        }
        if (errors.TryGetValue(message.Id, out var error))
            panel.Children.Add(Gap(Ui.Text(error, 11, "OrangeBrush", wrap: true), 6));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        if (drafts.All(d => done.Contains(d.Id)))
            actions.Children.Add(ChatUi.Label("", "Jira 에 기록했어요", "TextFg", 13, glyphBrush: "AccentBrush"));
        else if (working.Contains(message.Id))
            actions.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 60, Height = 3 });
        else actions.Children.Add(ChatUi.Primary("기록하기", () => RecordWorklogs(message)));
        panel.Children.Add(actions);
        return ChatUi.Box(panel);
    }

    async void RecordWorklogs(ChatMessage message)
    {
        working.Add(message.Id);
        errors.Remove(message.Id);
        OnMessageUpdated(message.Id);
        foreach (var draft in message.Worklogs.ToList())
        {
            if (message.WorklogsDone.Contains(draft.Id)) continue;
            try
            {
                await MateCLI.JiraWorklog(draft, model.Settings);
                chat.Update(message.Id, m => m.WorklogsDone.Add(draft.Id));
            }
            catch (Exception e)
            {
                errors[message.Id] = $"{draft.IssueKey} {draft.Start}: {e.Message}";
                break;
            }
        }
        working.Remove(message.Id);
        OnMessageUpdated(message.Id);
    }

    // MARK: Jira 이슈 초안

    /// 답변이 제안한 Jira 이슈 초안. [Jira에 등록]을 눌러야 등록되고, [수정]으로 고칠 수 있다.
    UIElement JiraDraftCard(ChatMessage message)
    {
        var draft = message.Jira!;
        var panel = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(ChatUi.Label("", "Jira 이슈", "TextFg", 13, FontWeights.Medium));
        var chip = Ui.Capsule(Ui.Text($"{draft.Project} · {draft.IssueType}", 11, "TextFg", FontWeights.Medium), Brushes.Transparent, new Thickness(6, 2, 6, 2));
        chip.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        chip.Margin = new Thickness(8, 0, 0, 0);
        title.Children.Add(chip);
        panel.Children.Add(title);

        var summary = new TextBox { Text = draft.Summary, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 6, 0, 0) };
        summary.SetResourceReference(FrameworkElement.StyleProperty, "PmTextBox");
        panel.Children.Add(summary);
        panel.Children.Add(Gap(ChatUi.Label("", "담당자 나 (연결된 Jira 계정)", "TextSecondary", 11), 4));
        if (draft.PeriodText.Length > 0) panel.Children.Add(Gap(ChatUi.Label("", draft.PeriodText, "TextSecondary", 11), 4));
        if (draft.EpicLink.Length > 0) panel.Children.Add(Gap(ChatUi.Label("", $"Epic {draft.EpicLink}", "TextSecondary", 11), 4));
        if (draft.Description.Length > 0)
        {
            var expander = new Expander { IsExpanded = descriptionOpen.Contains(message.Id), Margin = new Thickness(0, 4, 0, 0) };
            var header = Ui.Text("설명", 11, "TextSecondary");
            expander.Header = header;
            var body = new TextBox { Text = draft.Description, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
            body.SetResourceReference(FrameworkElement.StyleProperty, "PmTextBox");
            expander.Content = body;
            expander.Expanded += (_, _) => descriptionOpen.Add(message.Id);
            expander.Collapsed += (_, _) => descriptionOpen.Remove(message.Id);
            panel.Children.Add(expander);
        }
        if (errors.TryGetValue(message.Id, out var error))
            panel.Children.Add(Gap(Ui.Text(error, 11, "OrangeBrush", wrap: true), 6));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        if (message.JiraCreated is { } created)
        {
            actions.Children.Add(ChatUi.Label("", $"{created.Key} 등록했어요", "TextFg", 13, glyphBrush: "AccentBrush"));
            var open = ChatUi.Link("열기", () => { if (Uri.TryCreate(created.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") OpenUrl(uri.AbsoluteUri); });
            open.Margin = new Thickness(10, 0, 0, 0);
            actions.Children.Add(open);
        }
        else if (working.Contains(message.Id))
            actions.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 60, Height = 3 });
        else
        {
            actions.Children.Add(ChatUi.Primary("Jira에 등록", () => CreateJira(message)));
            var edit = Ui.Pill("수정", () => EditJira(message));
            edit.Margin = new Thickness(8, 0, 0, 0);
            actions.Children.Add(edit);
        }
        panel.Children.Add(actions);
        return ChatUi.Box(panel);
    }

    async void CreateJira(ChatMessage message)
    {
        working.Add(message.Id);
        errors.Remove(message.Id);
        OnMessageUpdated(message.Id);
        try
        {
            var result = await MateCLI.JiraCreate(message.Jira!, model.Settings);
            working.Remove(message.Id);
            chat.Update(message.Id, m => m.JiraCreated = result);
        }
        catch (Exception e)
        {
            errors[message.Id] = e.Message;
            working.Remove(message.Id);
            OnMessageUpdated(message.Id);
        }
    }

    void EditJira(ChatMessage message)
    {
        var editor = new JiraDraftEditor(message.Jira! with { });
        PopoverWindow.Current?.Hold();
        editor.Owner = null;
        var saved = editor.ShowDialog();
        PopoverWindow.Current?.Release();
        if (saved == true) chat.Update(message.Id, m => m.Jira = editor.Draft);
    }

    // MARK: 처음 사용

    /// 처음 사용: 주인 이름을 묻고 인사한다. Claude 를 부르지 않는다.
    UIElement OnboardingCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Text($"👋 안녕하세요! 저는 {UsageStore.Shared.MateName(model.Settings)}예요.", 15, "TextFg", FontWeights.SemiBold, wrap: true));
        panel.Children.Add(Gap(Ui.Text("앞으로 Jira, Wiki, 근무일정, 배포, 메일 같은 업무를 같이 챙겨 드릴게요.\n먼저, 어떻게 불러 드릴까요?", 13, "TextFg", wrap: true), 10));
        var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBox { Padding = new Thickness(6, 5, 6, 5), VerticalContentAlignment = VerticalAlignment.Center };
        name.SetResourceReference(FrameworkElement.StyleProperty, "PmTextBox");
        name.SetResourceReference(Control.BackgroundProperty, "FieldBg");
        name.SetResourceReference(Control.BorderBrushProperty, "Line");
        name.BorderThickness = new Thickness(1);
        var hint = Ui.Text("호칭. 예: 길동, 트레이너", 13, "TextTertiary");
        hint.IsHitTestVisible = false;
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.Margin = new Thickness(8, 0, 0, 0);
        var field = new Grid();
        field.Children.Add(name);
        field.Children.Add(hint);
        var save = ChatUi.Primary("반가워요", () => SaveOwner(name.Text), enabled: false);
        save.Margin = new Thickness(8, 0, 0, 0);
        name.TextChanged += (_, _) =>
        {
            hint.Visibility = name.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            save.IsEnabled = name.Text.Trim().Length > 0;
        };
        name.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Return && name.Text.Trim().Length > 0) { e.Handled = true; SaveOwner(name.Text); }
        };
        row.Children.Add(field);
        Grid.SetColumn(save, 1);
        row.Children.Add(save);
        panel.Children.Add(row);
        Dispatcher.BeginInvoke(new Action(() => name.Focus()), System.Windows.Threading.DispatcherPriority.Loaded);
        return ChatUi.Box(panel, 12);
    }

    void SaveOwner(string text)
    {
        var owner = text.Trim();
        if (owner.Length == 0) return;
        var info = new StringInfo(owner);
        owner = info.SubstringByTextElements(0, Math.Min(20, info.LengthInTextElements));
        model.UpdateSettings(s => s.OwnerName = owner);
        chat.Say($"""
            반가워요, {model.Settings.OwnerCallName}! 🙌 앞으로 잘 부탁드려요.

            **먼저 해 주세요**
            - 오른쪽 🔌 연결 → [일괄 연동] 으로 사내 계정을 연결해 주세요. 그래야 Jira·Wiki·근무일정을 볼 수 있어요.

            **이렇게 말해 보세요**
            - 내 미완료 Jira 알려줘
            - 평일 9시에 이번 주 근무일정 알려줘 (알림 등록)
            - 지라 등록해줘: 쿠폰 API 응답 필드 추가 (처음엔 기본값을 여쭤봐요)
            - 앞으로 지라 제목 앞에 [회의] 붙여줘 (rules/ 에 규칙으로 저장)
            """);
    }

    // MARK: 로그인

    /// 로그인 진행 상자. 브라우저에 나온 코드를 붙여넣으면 claude 에게만 전달하고 지운다.
    FrameworkElement LoginBox()
    {
        var panel = new StackPanel();
        switch (chat.LoginState)
        {
            case LoginState.WaitingBrowser:
                panel.Children.Add(ChatUi.Label("", "브라우저에서 회사 계정으로 로그인하고 승인해 주세요.", "TextFg"));
                break;
            case LoginState.NeedsCode:
            {
                panel.Children.Add(Ui.Text("브라우저에 나온 코드를 붙여넣어 주세요.", 13, "TextFg"));
                var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var code = new PasswordBox { Padding = new Thickness(6, 5, 6, 5), BorderThickness = new Thickness(1), VerticalContentAlignment = VerticalAlignment.Center };
                code.SetResourceReference(Control.BackgroundProperty, "FieldBg");
                code.SetResourceReference(Control.BorderBrushProperty, "Line");
                code.SetResourceReference(Control.ForegroundProperty, "TextFg");
                void Submit()
                {
                    if (code.Password.Length == 0) return;
                    var value = code.Password;
                    code.Clear();
                    chat.SubmitLoginCode(value);
                }
                code.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Return) { e.Handled = true; Submit(); } };
                row.Children.Add(code);
                var ok = Ui.Pill("확인", Submit);
                ok.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(ok, 1);
                row.Children.Add(ok);
                panel.Children.Add(row);
                Dispatcher.BeginInvoke(new Action(() => code.Focus()), System.Windows.Threading.DispatcherPriority.Loaded);
                break;
            }
            case LoginState.Verifying:
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 28, Height = 3, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(Ui.Text("로그인 확인 중…", 13));
                panel.Children.Add(row);
                break;
            }
        }
        panel.Children.Add(Gap(ChatUi.Id(ChatUi.Link("로그인 취소", () => chat.CancelLogin()), "LoginCancel"), 8));
        var box = new Border { Child = panel, Padding = new Thickness(10), CornerRadius = new CornerRadius(10) };
        box.SetResourceReference(Border.BackgroundProperty, "CardBg");
        return box;
    }

    // MARK: 승인 카드

    FrameworkElement PermissionCard(PermissionRequest request)
    {
        var menu = AllowRules.Menu(request.Tool, request.InputObject, request.Rules, ClaudeCommand.LoadDeny());
        var panel = new StackPanel();

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(ChatUi.Label("", $"{request.Tool} 실행을 허용할까요?", "TextFg", 13, FontWeights.SemiBold, glyphBrush: "OrangeBrush"));
        if (chat.SessionAllow.Count > 0)
        {
            var allowed = ChatUi.Id(ChatUi.Link($"항상 허용 {chat.SessionAllow.Count}개", () => { }), "PermAllowed");
            allowed.Click += (_, _) => ShowAllowed(allowed);
            Grid.SetColumn(allowed, 1);
            head.Children.Add(allowed);
        }
        panel.Children.Add(head);

        var summary = new TextBox
        {
            Text = request.Summary, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13, Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 0), MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        summary.SetResourceReference(FrameworkElement.StyleProperty, "PmTextBox");
        summary.SetResourceReference(Control.BackgroundProperty, "HoverBg");
        panel.Children.Add(summary);
        if (request.Detail.Length > 0)
        {
            var detail = new TextBox
            {
                Text = request.Detail, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 11, MaxHeight = 90, Margin = new Thickness(0, 8, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            detail.SetResourceReference(FrameworkElement.StyleProperty, "PmTextBox");
            detail.SetResourceReference(Control.ForegroundProperty, "TextSecondary");
            panel.Children.Add(detail);
        }

        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var allow = ChatUi.Id(ChatUi.Primary("허용", () => chat.AnswerPermission(new PermissionDecision.Allow())), "PermAllow");
        allow.Margin = new Thickness(0, 0, 8, 4);
        buttons.Children.Add(allow);
        var deny = ChatUi.Id(Ui.Pill("거부", () => chat.AnswerPermission(new PermissionDecision.Deny("사용자가 거부했어요"))), "PermDeny");
        deny.Margin = new Thickness(0, 0, 8, 4);
        deny.VerticalAlignment = VerticalAlignment.Center;
        buttons.Children.Add(deny);
        var always = ChatUi.Id(Ui.Pill("이 대화에서 항상 허용 ▾", () => { }), "PermAlways");
        always.Margin = new Thickness(0, 0, 8, 4);
        always.Click += (_, _) => ShowAlwaysMenu(always, menu);
        buttons.Children.Add(always);
        panel.Children.Add(buttons);
        var note = Ui.Text("10분 안에 답이 없으면 거부", 10, "TextTertiary");
        note.HorizontalAlignment = HorizontalAlignment.Right;
        panel.Children.Add(note);

        return ChatUi.Tint(panel, 0.08, 12, 12, 0.35);
    }

    void ShowAllowed(FrameworkElement anchor)
    {
        var menu = ChatUi.NewMenu(anchor);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.Items.Add(ChatUi.Heading("이 대화에서 항상 허용 중"));
        foreach (var rule in chat.SessionAllow)
        {
            var item = new MenuItem { Header = rule, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11, IsEnabled = false };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(ChatUi.Item("모두 지우기", () => chat.ClearSessionAllow()));
        menu.IsOpen = true;
    }

    void ShowAlwaysMenu(FrameworkElement anchor, AllowMenu menuData)
    {
        var menu = ChatUi.NewMenu(anchor);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        if (menuData.Unavailable is { } reason) menu.Items.Add(ChatUi.Heading(reason));
        foreach (var option in menuData.Options)
        {
            var item = option.Caution
                ? ChatUi.Item($"{option.Label}  ⚠ 주의 (바꾸거나 내보내는 명령)", () => chat.AnswerPermission(new PermissionDecision.AllowRules(option.Rules)), foreground: "OrangeBrush")
                : ChatUi.Item(option.Label, () => chat.AnswerPermission(new PermissionDecision.AllowRules(option.Rules)));
            item.ToolTip = string.Join("\n", option.Rules);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(ChatUi.Heading("허용할 규칙이 없어요"));
        menu.IsOpen = true;
    }
}

/// Jira 이슈 초안 고치기 (맥 JiraDraftEditor)
public sealed class JiraDraftEditor : Window
{
    public JiraDraft Draft { get; }

    Button? save;

    public JiraDraftEditor(JiraDraft draft)
    {
        Draft = draft;
        Title = "Jira 이슈 수정";
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; DialogResult = false; } };
        Width = 520;
        Height = 640;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "WindowBg");
        SetResourceReference(TextElement.ForegroundProperty, "TextFg");

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(Ui.Text("Jira 이슈 수정", 16, "TextFg", FontWeights.SemiBold));

        var form = new StackPanel();
        form.Children.Add(Field("프로젝트 키", draft.Project, "예: MYPROJ", v => draft.Project = v.Trim()));
        form.Children.Add(Field("이슈 유형", draft.IssueType, "예: Story, Task", v => draft.IssueType = v.Trim()));
        form.Children.Add(Field("제목", draft.Summary, "", v => draft.Summary = v.Trim()));
        form.Children.Add(Field("설명 (Jira 위키 문법)", draft.Description, "", v => draft.Description = v, multiline: true));
        form.Children.Add(Field("Original Estimate", draft.OriginalEstimate, "예: 1d, 0.5d, 4h (비우면 안 넣음)", v => draft.OriginalEstimate = v.Trim()));
        form.Children.Add(Field("Target start", draft.TargetStart, "yyyy-MM-dd, 비우면 안 넣음", v => draft.TargetStart = v.Trim()));
        form.Children.Add(Field("Target end", draft.TargetEnd, "yyyy-MM-dd, 비우면 안 넣음", v => draft.TargetEnd = v.Trim()));
        form.Children.Add(Field("Epic Link", draft.EpicLink, "예: MYPROJ-100, 비우면 안 넣음", v => draft.EpicLink = v.Trim()));
        var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 12) };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Ui.Pill("취소", () => { DialogResult = false; });
        cancel.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(cancel);
        save = ChatUi.Primary("저장", () => { DialogResult = true; });
        save.IsDefault = false;
        buttons.Children.Add(save);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        Content = root;
        save.IsEnabled = Draft.IsComplete;
    }

    UIElement Field(string title, string value, string hint, Action<string> changed, bool multiline = false)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(Ui.Text(title, 11, "TextSecondary"));
        var box = new TextBox
        {
            Text = value, Padding = new Thickness(6, 5, 6, 5), BorderThickness = new Thickness(1), Margin = new Thickness(0, 3, 0, 0),
            AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multiline ? 140 : 0, VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
        };
        if (multiline) box.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        box.SetResourceReference(Control.BackgroundProperty, "FieldBg");
        box.SetResourceReference(Control.BorderBrushProperty, "Line");
        box.SetResourceReference(Control.ForegroundProperty, "TextFg");
        box.SetResourceReference(TextBoxBase.CaretBrushProperty, "TextFg");
        var placeholder = Ui.Text(hint, 13, "TextTertiary");
        placeholder.IsHitTestVisible = false;
        placeholder.Margin = new Thickness(9, 8, 0, 0);
        placeholder.VerticalAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center;
        placeholder.Visibility = value.Length == 0 && hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) =>
        {
            changed(box.Text);
            placeholder.Visibility = box.Text.Length == 0 && hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (save != null) save.IsEnabled = Draft.IsComplete;
        };
        var host = new Grid();
        host.Children.Add(box);
        host.Children.Add(placeholder);
        panel.Children.Add(host);
        return panel;
    }
}
