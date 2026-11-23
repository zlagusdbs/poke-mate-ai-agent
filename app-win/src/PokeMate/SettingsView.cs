using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PokeMate;

/// 설정 화면 (맥 SettingsView). 값을 바꾸면 model.UpdateSettings 로 바로 저장한다.
/// 글자 칸은 입력이 멈추고 0.3초 뒤에 저장한다 (한 글자마다 파일을 쓰지 않게).
[PopoverTab(SidebarItem.Settings)]
public sealed class SettingsView : UserControl
{
    readonly AppModel model;
    readonly ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly Dictionary<DispatcherTimer, Action> pending = new();
    Border? pokedexStatus;
    TextBlock? hotkeyDisplay;
    TextBlock? hotkeyMessage;
    Button? hotkeyButton;
    Button? hotkeyDefault;
    bool recording;
    Window? keyWindow;

    // 규칙 영역 상태 (맥 @State)
    List<Rules.File> files = new();
    bool localReady = AppPaths.LocalReady;

    public SettingsView(AppModel model)
    {
        this.model = model;
        // 다른 탭처럼 위에 제목 줄을 둔다
        var column = new DockPanel();
        var title = Ui.Text("설정", 15, "TextFg", FontWeights.SemiBold);
        title.Margin = new Thickness(12, 0, 12, 10);
        DockPanel.SetDock(title, Dock.Top);
        column.Children.Add(title);
        var divider = EditorStyle.Divider(0);
        DockPanel.SetDock(divider, Dock.Top);
        column.Children.Add(divider);
        column.Children.Add(scroll);
        Content = column;

        Loaded += (_, _) =>
        {
            UsageStore.Shared.PropertyChanged += OnUsageChanged;
            model.Chat.PropertyChanged += OnChatChanged;
            Rebuild();
            _ = RefreshAccount();
        };
        Unloaded += (_, _) =>
        {
            UsageStore.Shared.PropertyChanged -= OnUsageChanged;
            model.Chat.PropertyChanged -= OnChatChanged;
            StopRecording();
            FlushPending();
        };
    }

    AppSettings S => model.Settings;
    void Change(Action<AppSettings> change) => model.UpdateSettings(change);

    // MARK: 글자 칸 저장 (입력이 멈추면)
    FrameworkElement SettingField(string label, string value, Action<AppSettings, string> apply, string? hint = null, string? id = null, bool mono = false)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        var latest = value;
        void Save()
        {
            timer.Stop();
            pending.Remove(timer);
            Change(s => apply(s, latest));
        }
        timer.Tick += (_, _) => Save();
        var (frame, _) = EditorStyle.TextField(value, text =>
        {
            latest = text;
            pending[timer] = Save;
            timer.Stop();
            timer.Start();
        }, hint, mono: mono, id: id);
        return EditorStyle.Labeled(label, frame);
    }

    /// 화면을 떠나거나 다시 그리기 전에 아직 저장하지 않은 입력을 저장한다
    void FlushPending()
    {
        foreach (var save in pending.Values.ToList()) save();
        pending.Clear();
    }


    // MARK: 만들기

    void Rebuild()
    {
        var offset = scroll.VerticalOffset;
        StopRecording();
        FlushPending();
        localReady = AppPaths.LocalReady;
        files = Rules.Files();
        var column = new StackPanel { Margin = new Thickness(12, 12, 12, 8) };

        column.Children.Add(EditorStyle.Section("이름", new UIElement[]
        {
            SettingField("나를 부를 호칭", S.OwnerName, (s, v) => s.OwnerName = v, "예: 길동, 트레이너 (끝에 '님'이 붙어요)", "OwnerName"),
        }));
        column.Children.Add(PokedexSection());
        column.Children.Add(WorkSessionSection());
        column.Children.Add(AutomationModelSection());
        column.Children.Add(RulesSection());
        column.Children.Add(HomeSection());
        column.Children.Add(EditorStyle.Section("단축키", new UIElement[] { HotkeyRow() }));
        column.Children.Add(AccountSection());
        column.Children.Add(EditorStyle.Section("경로", new UIElement[]
        {
            SettingField("메이트 저장소", S.RepoDir, (s, v) => s.RepoDir = v, id: "RepoDir"),
            SettingField("claude", S.ClaudePath, (s, v) => s.ClaudePath = v, id: "ClaudePath"),
            SettingField("uv", S.UvPath, (s, v) => s.UvPath = v, id: "UvPath"),
        }));
        column.Children.Add(StartupSection());

        var quit = Ui.Pill("메이트 종료", () => App.Instance.RequestQuit());
        ChatUi.Id(quit, "SettingsQuit");
        quit.HorizontalAlignment = HorizontalAlignment.Left;
        column.Children.Add(EditorStyle.Section(null, new UIElement[] { quit }));

        scroll.Content = column;
        scroll.UpdateLayout();
        scroll.ScrollToVerticalOffset(offset);
    }

    // MARK: 선택지

    static IReadOnlyList<(string Label, int Value)> Choices(int current, IEnumerable<(string Label, int Value)> options, Func<int, string> label)
    {
        var list = options.ToList();
        if (list.All(o => o.Value != current)) list.Add((label(current), current));
        return list;
    }

    // MARK: 포켓몬 (맥 PokedexSection)

    UIElement PokedexSection()
    {
        var range = EditorStyle.Picker(PokedexRangeExtensions.AllCases.Select(r => (r.Label(), r)).ToList(), S.PokedexRange,
            value => { Change(s => s.PokedexRange = value); Rebuild(); }, "PokedexRange");
        pokedexStatus = new Border();
        UpdatePokedexStatus();
        return EditorStyle.Section("포켓몬", new UIElement[] { EditorStyle.Row("포켓몬 범위", range), pokedexStatus },
            "새 범위는 다음 알부터 적용돼요. 키우는 포켓몬과 도감 기록은 그대로예요. 한 번 받은 도감은 다시 받지 않아요.");
    }

    void UpdatePokedexStatus()
    {
        if (pokedexStatus == null) return;
        var usage = UsageStore.Shared;
        if (usage.PokedexProgress is { } progress)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var bar = Ui.Progress(progress.Done / (double)Math.Max(1, progress.Total), "AccentBrush");
            bar.Width = 120;
            bar.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(bar);
            var text = Ui.Text($"{usage.Range.Label()} 받는 중 · {progress.Text}", 12, "TextSecondary");
            text.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(text);
            pokedexStatus.Child = row;
        }
        else if (usage.PokedexError is { } error)
        {
            var row = new DockPanel();
            var retry = Ui.Pill("다시 받기", () => usage.LoadPokedex());
            ChatUi.Id(retry, "PokedexRetry");
            DockPanel.SetDock(retry, Dock.Right);
            row.Children.Add(retry);
            var text = Ui.Text(error, 12, "TextFg", wrap: true);
            text.Foreground = Brushes.DarkOrange;
            row.Children.Add(text);
            pokedexStatus.Child = row;
        }
        else pokedexStatus.Child = null;
        pokedexStatus.Visibility = pokedexStatus.Child == null ? Visibility.Collapsed : Visibility.Visible;
    }

    void OnUsageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UsageStore.PokedexProgress) or nameof(UsageStore.PokedexError) or nameof(UsageStore.Range))
            Dispatcher.BeginInvoke(UpdatePokedexStatus);
    }

    // MARK: 작업 세션

    UIElement WorkSessionSection()
    {
        var alert = EditorStyle.Picker(Choices(S.WorkSessionAlertPercent,
            new[] { ("끄기", 0) }.Concat(new[] { 50, 60, 70, 80, 90 }.Select(p => ($"{p}%", p))), p => $"{p}%"),
            S.WorkSessionAlertPercent, v => Change(s => s.WorkSessionAlertPercent = v), "WorkSessionAlert");
        var minutes = EditorStyle.Picker(Choices(S.SessionReuseMinutes,
            new[] { ("늘 이어 쓰기", 0) }.Concat(new[] { 30, 55, 120, 240 }.Select(p => ($"{p}분", p))), p => $"{p}분"),
            S.SessionReuseMinutes, v => Change(s => s.SessionReuseMinutes = v), "SessionReuseMinutes");
        var tokens = EditorStyle.Picker(Choices(S.SessionReuseMaxTokens,
            new[] { ("늘 이어 쓰기", 0) }.Concat(new[] { 80_000, 120_000, 160_000, 200_000 }.Select(p => (TokenFormat.Compact(p), p))), p => TokenFormat.Compact(p)),
            S.SessionReuseMaxTokens, v => Change(s => s.SessionReuseMaxTokens = v), "SessionReuseMaxTokens");
        return EditorStyle.Section("작업 세션", new UIElement[]
        {
            EditorStyle.Row("문맥이 넘으면 알림", alert),
            EditorStyle.Row("자동 처리: 이 시간 넘게 쉬면 새 세션", minutes),
            EditorStyle.Row("자동 처리: 문맥이 이만큼이면 새 세션", tokens),
        }, "알림을 받으면 위쪽 '작업' 탭에서 새 세션으로 바꿀지 정하세요. 쉰 시간·문맥 크기 기준은 모든 자동 처리 역할 공통이에요(Jira 역할 처리·GitHub 리뷰). 세션은 작업 한 건(티켓 하나, PR 하나) 안에서만 이어 쓰고, 마지막 사용 뒤 정한 시간이 지났거나 문맥이 정한 크기를 넘으면 새 세션으로 시작해요(캐시가 1시간이라 기본 55분·120k). 새 세션은 그 건의 인계 메모와 도메인 노트·지식부터 읽어요.");
    }

    // MARK: 자동 처리 모델

    UIElement AutomationModelSection()
    {
        var models = new[] { "" }.Concat(ClaudeCommand.Models).Select(m => (ModelLabels.Models.GetValueOrDefault(m) ?? m, m)).ToList();
        var efforts = new[] { "" }.Concat(ClaudeCommand.Efforts).Select(m => (ModelLabels.Efforts.GetValueOrDefault(m) ?? m, m)).ToList();
        return EditorStyle.Section("자동 처리 모델", new UIElement[]
        {
            EditorStyle.Row("모델", EditorStyle.Picker(models, S.AutomationModel, v => Change(s => s.AutomationModel = v), "AutomationModel"),
                "알람, Jira·리뷰 자동 처리, 작업 세션, 역할별 세션, 레포 정하기에 쓰는 모델이에요.\n홈 채팅창 아래에서 고르는 모델은 메이트와 직접 대화할 때만 쓰고, 여기와 따로예요.\n바꾸면 다음 실행부터 적용돼요."),
            EditorStyle.Row("추론 수준", EditorStyle.Picker(efforts, S.AutomationEffort, v => Change(s => s.AutomationEffort = v), "AutomationEffort"),
                "자동 처리가 얼마나 깊이 생각할지예요. 높을수록 꼼꼼하지만 느리고 토큰을 더 써요.\n홈 채팅창의 추론 수준과 따로예요."),
        });
    }

    // MARK: 규칙 (맥 RulesSection)

    static string KindKey(Rules.Kind kind) => kind.ToString().ToLowerInvariant();

    static string Folder(Rules.Kind kind) => kind switch
    {
        Rules.Kind.Root => "root",
        Rules.Kind.Rule => "rules",
        Rules.Kind.Skill => "skills",
        _ => "prompts",
    };

    static string NewTitle(Rules.Kind kind) => kind switch
    {
        Rules.Kind.Skill => "새 스킬",
        Rules.Kind.Prompt => "새 프롬프트",
        _ => "새 규칙",
    };

    static string HelpText(Rules.Kind kind) => kind switch
    {
        Rules.Kind.Root => "메이트 전체에 늘 적용하는 기본 지시예요 (말투, 답하는 방식 등).\nshared/CLAUDE.md(일반, Git 에 올라감) 다음에 local/CLAUDE.md(회사·개인, Git 제외)를 붙여 읽고, 겹치면 local 을 따라요. 지우면 기본 지시가 사라져서 고치기만 하고 지울 수는 없어요.",
        Rules.Kind.Rule => "질문할 때마다 모든 파일을 통째로 읽어요. 길수록 질문마다 비용이 늘고 답이 느려지니, 늘 지켜야 할 것만 짧게 적고 파일당 200줄을 넘기지 마세요.\n채팅에서 \"기억해\" 하고 [기억하기]를 누르면 rules/memory.md 에 쌓여요.",
        Rules.Kind.Skill => "주제별 안내서예요. 평소엔 한 줄 설명만 보내고, 질문에 그 주제의 키워드가 나오면 그때 본문을 읽어요.\n예: Jira 이야기가 나오면 skills/jira.md 를 읽어요. 키워드가 없어도 필요하면 메이트가 직접 찾아 읽어요.\n파일 맨 위에 description(한 줄 설명)과 keywords(쉼표로 구분)를 적어 주세요.",
        _ => "알람 내용에서 불러 쓰는 프롬프트 파일이에요. 대화에서는 자동으로 읽지 않아요.\n알람 내용에 {{prompt:이름}} 을 쓰거나, 알람 수정 화면의 [프롬프트 불러오기]로 골라요.",
    };

    bool IsCollapsed(Rules.Kind kind) => (Prefs.String("rulesCollapsed") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(KindKey(kind));

    void ToggleCollapsed(Rules.Kind kind)
    {
        var keys = (Prefs.String("rulesCollapsed") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!keys.Remove(KindKey(kind))) keys.Add(KindKey(kind));
        Prefs.Set("rulesCollapsed", string.Join(",", keys));
        Rebuild();
    }

    UIElement RulesSection()
    {
        var rows = new List<UIElement>();
        if (!localReady)
        {
            var warning = new StackPanel();
            var head = ChatUi.Label("", "local 폴더가 없어요. 알람·규칙을 읽기만 하고 저장하지 않아요.", "TextFg", 12);
            foreach (var child in head.Children.OfType<TextBlock>()) child.Foreground = Brushes.DarkOrange;
            warning.Children.Add(head);
            var detail = Ui.Text($"레포 위치: {AppPaths.RepoDir} (아래 \"메이트 저장소\" 설정). local/ 은 Git 에 올라가지 않는 회사·개인 설정 폴더예요. 다른 PC 에서 옮겨 오거나 예시로 만들어요.", 11, "TextSecondary", wrap: true);
            detail.Margin = new Thickness(0, 6, 0, 0);
            warning.Children.Add(detail);
            var make = Ui.Pill("예시(local.example)로 local 만들기", () =>
            {
                AppPaths.MakeLocalFromExample();
                Rebuild();
            });
            ChatUi.Id(make, "MakeLocal");
            make.HorizontalAlignment = HorizontalAlignment.Left;
            make.Margin = new Thickness(0, 6, 0, 0);
            warning.Children.Add(make);
            rows.Add(warning);
        }
        var hidden = Rules.Overridden(files);
        foreach (var kind in new[] { Rules.Kind.Root, Rules.Kind.Rule, Rules.Kind.Skill, Rules.Kind.Prompt })
        {
            var list = files.Where(f => f.Kind == kind).ToList();
            rows.Add(GroupHeader(kind, list.Count));
            if (IsCollapsed(kind)) continue;
            if (list.Count == 0 && kind != Rules.Kind.Root)
            {
                var none = Ui.Text("없음", 11, "TextTertiary");
                none.Margin = new Thickness(18, 0, 0, 0);
                rows.Add(none);
            }
            foreach (var file in list) rows.Add(FileRow(file, kind != Rules.Kind.Root, hidden));
        }

        var folder = ChatUi.Link("폴더 열기", () => Opener.Open(localReady ? AppPaths.LocalDir : AppPaths.RepoDir));
        ChatUi.Id(folder, "RulesOpenFolder");
        var reload = EditorStyle.GlyphButton("", Rebuild, "새로고침", id: "RulesReload");
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(folder);
        right.Children.Add(reload);
        return EditorStyle.Section("규칙", rows, headerRight: right, spacing: 6);
    }

    UIElement GroupHeader(Rules.Kind kind, int count)
    {
        var open = !IsCollapsed(kind);
        var row = new DockPanel();
        var toggle = new Button { Cursor = Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Focusable = false };
        toggle.Template = BareTemplate();
        var label = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
        label.Children.Add(ChatUi.Glyph(open ? "" : "", "TextSecondary", 9));
        var name = Ui.Text(Folder(kind), 12, "TextSecondary", FontWeights.SemiBold);
        name.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        name.Margin = new Thickness(6, 0, 0, 0);
        label.Children.Add(name);
        var number = Ui.Text(count.ToString(), 11, "TextTertiary");
        number.Margin = new Thickness(6, 0, 0, 0);
        number.VerticalAlignment = VerticalAlignment.Center;
        label.Children.Add(number);
        toggle.Content = label;
        toggle.ToolTip = open ? "접기" : "펼치기";
        ChatUi.Id(toggle, "RulesGroup:" + Folder(kind));
        toggle.Click += (_, _) => ToggleCollapsed(kind);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(toggle);
        left.Children.Add(EditorStyle.Help(HelpText(kind)));
        row.Children.Add(left);
        if (kind != Rules.Kind.Root)
        {
            var add = EditorStyle.GlyphButton("", () => NewFile(kind), NewTitle(kind), id: "RulesNew:" + Folder(kind));
            DockPanel.SetDock(add, Dock.Right);
            row.Children.Insert(0, add);
        }
        return row;
    }

    static ControlTemplate BareTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        template.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
        return template;
    }

    UIElement FileRow(Rules.File file, bool deletable, HashSet<string> hidden)
    {
        var row = new DockPanel { Margin = new Thickness(18, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(EditorStyle.GlyphButton("", () =>
        {
            if (file.Layer == Rules.Layer.Shared)
            {
                if (EditorStyle.Confirm("Git 에 올라가는 파일이에요",
                        $"shared/{file.Name} 은 레포에 함께 올라가요. 회사 이름·주소·사람 이름 같은 값은 넣지 말고, 그런 내용은 local 에 같은 이름 파일로 덮어쓰세요.", "열기"))
                    Opener.Open(file.Url);
            }
            else Opener.Open(file.Url);
        }, "열기", id: "RulesOpen:" + Path.GetFileName(file.Url)));
        if (deletable)
        {
            var delete = EditorStyle.GlyphButton("", () => Delete(file), "삭제", id: "RulesDelete:" + Path.GetFileName(file.Url));
            delete.Foreground = Brushes.IndianRed;
            buttons.Children.Add(delete);
        }
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);

        var info = new StackPanel { Orientation = Orientation.Horizontal };
        info.Children.Add(ChatUi.Glyph("", "TextSecondary", 12));
        var name = Ui.Text(Path.GetFileName(file.Url), 13);
        name.FontFamily = new FontFamily("Cascadia Mono, Consolas, Malgun Gothic");
        name.Margin = new Thickness(6, 0, 0, 0);
        if (hidden.Contains(file.Id)) name.TextDecorations = TextDecorations.Strikethrough;
        info.Children.Add(name);
        var isLocal = file.Layer == Rules.Layer.Local;
        var badge = new TextBlock { Text = isLocal ? "local" : "shared", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = isLocal ? Brushes.SteelBlue : Theme.Brush("TextSecondary") };
        info.Children.Add(new Border
        {
            Child = badge, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(5, 1, 5, 1), CornerRadius = new CornerRadius(20),
            Background = Ui.Alpha(isLocal ? Colors.SteelBlue : Colors.Gray, 0.14), VerticalAlignment = VerticalAlignment.Center,
        });
        if (hidden.Contains(file.Id))
        {
            var over = Ui.Text("local 이 덮어씀", 11, "TextSecondary");
            over.Margin = new Thickness(6, 0, 0, 0);
            over.VerticalAlignment = VerticalAlignment.Center;
            info.Children.Add(over);
        }
        if (file.Kind == Rules.Kind.Rule)
        {
            var lines = file.LineCount;
            var count = Ui.Text(lines > Rules.RuleLineLimit ? $"{lines}줄 · 200줄 넘음" : $"{lines}줄", 11, lines > Rules.RuleLineLimit ? "OrangeBrush" : "TextSecondary");
            count.Margin = new Thickness(6, 0, 0, 0);
            count.VerticalAlignment = VerticalAlignment.Center;
            info.Children.Add(count);
        }
        row.Children.Add(info);
        return row;
    }

    void Delete(Rules.File file)
    {
        if (!EditorStyle.Confirm("파일을 삭제할까요?", $"{file.Layer.ToString().ToLowerInvariant()}/{file.Name} 을(를) 휴지통으로 옮겨요.")) return;
        try { Rules.Delete(file); }
        catch (Exception e) { EditorStyle.Notice("지우지 못했어요", e.Message); }
        Rebuild();
    }

    void NewFile(Rules.Kind kind)
    {
        var name = "";
        var body = new StackPanel { Margin = new Thickness(20) };
        body.Children.Add(Ui.Text(NewTitle(kind), 15, "TextFg", FontWeights.SemiBold));
        var message = Ui.Text($"{Folder(kind)}/ 에 md 파일을 만들고 열어요. local 은 Git 에 올라가지 않는 회사·개인용, shared 는 Git 에 올라가는 일반용이에요.", 11, "TextSecondary", wrap: true);
        message.Margin = new Thickness(0, 8, 0, 10);
        body.Children.Add(message);
        Button? local = null, shared = null;
        void Update()
        {
            var empty = name.Trim().Length == 0;
            if (local != null) local.IsEnabled = !empty && localReady;
            if (shared != null) shared.IsEnabled = !empty;
        }
        body.Children.Add(EditorStyle.WhiteField("", t => { name = t; Update(); }, "파일 이름. 예: wiki", id: "NewFileName"));
        var window = EditorStyle.Dialog(NewTitle(kind), 380, body);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = Ui.Pill("취소", () => window.DialogResult = false);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        shared = Ui.Pill("shared 에 만들기", () => Create(Rules.Layer.Shared));
        shared.Margin = new Thickness(0, 0, 8, 0);
        shared.IsEnabled = false;
        local = ChatUi.Primary("local 에 만들기", () => Create(Rules.Layer.Local), enabled: false);
        ChatUi.Id(local, "NewFileLocal");
        ChatUi.Id(shared, "NewFileShared");
        buttons.Children.Add(cancel);
        buttons.Children.Add(shared);
        buttons.Children.Add(local);
        body.Children.Add(buttons);
        EditorStyle.ButtonKeys(window, new[] { cancel, shared, local }, 2);
        void Create(Rules.Layer layer)
        {
            try
            {
                var path = Rules.Create(name, kind, layer);
                window.DialogResult = true;
                Opener.Open(path);
            }
            catch (Exception e) { window.DialogResult = false; EditorStyle.Notice("만들지 못했어요", e.Message); }
        }
        EditorStyle.ShowModal(window);
        Rebuild();
    }

    // MARK: 홈 지침 (맥 RulesSection 의 둘째 Section)

    UIElement HomeSection()
    {
        bool homeOpen = Prefs.Bool("homeInstructionsOpen"), rulesOpen = Prefs.Bool("homeRulesOpen");
        var rows = new List<UIElement>();

        var title = new StackPanel { Orientation = Orientation.Horizontal };
        if (S.UseHomeInstructions)
        {
            var chevron = new Button { Content = ChatUi.Glyph(homeOpen ? "" : "", "TextSecondary", 9), Cursor = Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 0, 6, 0), ToolTip = homeOpen ? "접기" : "펼치기", Focusable = false };
            chevron.Template = BareTemplate();
            ChatUi.Id(chevron, "HomeInstructionsToggle");
            chevron.Click += (_, e) => { Prefs.Set("homeInstructionsOpen", !homeOpen); Rebuild(); };
            title.Children.Add(chevron);
        }
        title.Children.Add(Ui.Text("내 Claude Code 개인 지침도 읽기", 13));
        title.Children.Add(EditorStyle.Help("켜면 내 홈의 ~/.claude/CLAUDE.md 와 그 rules/ 도 메이트가 읽어요.\n개발용 지침(코드 분석 형식, 커밋 규칙 등)이 비서 답에 섞이고 질문마다 읽는 양이 늘어서 기본은 꺼 둬요.\n메이트 지시는 메이트 폴더의 CLAUDE.md 에 적으세요."));
        rows.Add(EditorStyle.Switch(title, S.UseHomeInstructions, on => { Change(s => s.UseHomeInstructions = on); Rebuild(); }, id: "UseHomeInstructions"));

        if (S.UseHomeInstructions && homeOpen)
        {
            rows.Add(Indent(EditorStyle.Switch(Titled("대화에서 읽기", "홈 탭에서 메이트와 직접 채팅할 때 내 Claude Code 개인 지침(~/.claude/CLAUDE.md 와 rules 전부)을 읽어요.\n켜면 답하는 방식이 Claude Code 와 같아져요(코드 분석 형식, 커밋 규칙 등). 질문마다 읽는 양이 늘어요.\n끄면 메이트 폴더의 CLAUDE.md 와 rules 만 따라요."),
                S.HomeInChat, on => Change(s => s.HomeInChat = on), id: "HomeInChat"), 16));
            rows.Add(Indent(EditorStyle.Switch(Titled("자동 처리(알람)에서 읽기", "알람, Jira·리뷰 자동 처리, 작업 세션이 실행될 때 개인 지침을 읽어요.\n아래에서 체크한 rules 파일만 읽어요. 체크하지 않은 규칙은 자동 처리에 적용되지 않아요.\n내 작업에만 맞는 규칙(설명 형식 등)은 빼 두세요."),
                S.HomeInAutomation, on => { Change(s => s.HomeInAutomation = on); Rebuild(); }, id: "HomeInAutomation"), 16));
            if (S.HomeInAutomation)
            {
                var homeFiles = ClaudeCommand.HomeRules();
                var header = new StackPanel { Orientation = Orientation.Horizontal };
                var toggle = new Button { Cursor = Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Focusable = false, ToolTip = rulesOpen ? "접기" : "펼치기" };
                toggle.Template = BareTemplate();
                var label = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
                label.Children.Add(ChatUi.Glyph(rulesOpen ? "" : "", "TextSecondary", 9));
                var rulesName = Ui.Text("rules", 12, "TextSecondary", FontWeights.SemiBold);
                rulesName.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                rulesName.Margin = new Thickness(6, 0, 0, 0);
                label.Children.Add(rulesName);
                var ratio = Ui.Text($"{homeFiles.Count(f => S.AutomationRules.Contains(f))}/{homeFiles.Count}", 11, "TextTertiary");
                ratio.Margin = new Thickness(6, 0, 0, 0);
                ratio.VerticalAlignment = VerticalAlignment.Center;
                label.Children.Add(ratio);
                toggle.Content = label;
                ChatUi.Id(toggle, "HomeRulesToggle");
                toggle.Click += (_, _) => { Prefs.Set("homeRulesOpen", !rulesOpen); Rebuild(); };
                header.Children.Add(toggle);
                header.Children.Add(EditorStyle.Help("~/.claude/rules 의 파일이에요. 새 파일은 체크 안 된 상태로 나타나요."));
                rows.Add(Indent(header, 32));
                if (rulesOpen && homeFiles.Count == 0)
                {
                    var none = Ui.Text("~/.claude/rules 에 md 파일이 없어요", 11, "TextSecondary");
                    rows.Add(Indent(none, 48));
                }
                if (rulesOpen)
                    foreach (var file in homeFiles)
                    {
                        var name = file;
                        var box = EditorStyle.Check(name, S.AutomationRules.Contains(name), on => Change(s =>
                        {
                            s.AutomationRules.RemoveAll(r => r == name);
                            if (on) s.AutomationRules.Add(name);
                        }), id: "HomeRule:" + name);
                        box.FontSize = 11;
                        rows.Add(Indent(box, 48));
                    }
            }
        }

        rows.Add(EditorStyle.Switch(Titled("내 Claude Code 스킬도 쓰기", "~/.claude/skills 의 스킬을 메이트도 써요. 새 스킬도 다음 실행부터 자동으로 보여요.\n메이트 폴더에 같은 주제가 있으면 메이트 쪽을 따라요.\n스킬이 안내하는 명령도 권한은 그대로라, 자동 처리에서는 허용한 명령만 실행돼요."),
            S.ShareHomeSkills, on =>
            {
                Change(s => s.ShareHomeSkills = on);
                try { HomeSkills.Sync(on); } catch (Exception e) { App.Log($"홈 스킬을 연결하지 못했어요: {e.Message}"); }
            }, id: "ShareHomeSkills"));

        var tools = new DockPanel();
        var permissions = Ui.Pill("권한 파일 열기", () =>
        {
            try { ClaudeCommand.InstallPermissions(); } catch (Exception e) { App.Log($"권한 파일을 쓰지 못했어요: {e.Message}"); }
            Opener.Open(ClaudeCommand.PermissionsFile);
        });
        ChatUi.Id(permissions, "OpenPermissions");
        permissions.FontSize = 11;
        DockPanel.SetDock(permissions, Dock.Right);
        tools.Children.Add(permissions);
        tools.Children.Add(EditorStyle.Switch(Titled("Claude Code 도구 쓰기", "켜면 메이트 대화에서 Claude Code 기본 도구(터미널 명령, 파일 읽기·수정, 웹 가져오기 등)를 써요.\n명령 실행·파일 수정처럼 확인이 필요한 일은 대화창에 [허용/거부] 카드가 떠요. 누르기 전에는 실행하지 않아요.\n권한 파일의 deny 에 적은 것(sudo, 시스템 설정 변경 등)은 카드 없이 거부해요.\n알람 실행에는 쓰지 않아요."),
            S.ClaudeTools, on => Change(s => s.ClaudeTools = on), id: "ClaudeTools"));
        rows.Add(tools);

        return EditorStyle.Section(null, rows, "고친 내용은 다음 질문부터 바로 적용돼요.");
    }

    static UIElement Titled(string text, string help)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Ui.Text(text, 13));
        row.Children.Add(EditorStyle.Help(help));
        return row;
    }

    static UIElement Indent(FrameworkElement element, double left)
    {
        element.Margin = new Thickness(left, 0, 0, 0);
        return element;
    }

    // MARK: 단축키 (맥 HotkeyRecorder). [변경] 을 누르고 원하는 키 조합을 누르면 바로 바뀐다. Esc 는 취소.

    UIElement HotkeyRow()
    {
        var stack = new StackPanel();
        var row = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        hotkeyButton = Ui.Pill("변경", () => { if (recording) StopRecording(); else StartRecording(); });
        ChatUi.Id(hotkeyButton, "HotkeyChange");
        buttons.Children.Add(hotkeyButton);
        hotkeyDefault = Ui.Pill("기본값", () => { Change(s => s.Hotkey = Hotkey.Default); hotkeyMessage!.Text = ""; hotkeyMessage.Visibility = Visibility.Collapsed; UpdateHotkey(); });
        ChatUi.Id(hotkeyDefault, "HotkeyDefault");
        hotkeyDefault.Margin = new Thickness(6, 0, 0, 0);
        buttons.Children.Add(hotkeyDefault);
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);

        hotkeyDisplay = new TextBlock { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        hotkeyDisplay.SetResourceReference(TextBlock.ForegroundProperty, "TextFg");
        ChatUi.Id(hotkeyDisplay, "HotkeyDisplay");
        var chip = new Border { Child = hotkeyDisplay, Padding = new Thickness(10, 4, 10, 4), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 8, 0) };
        chip.SetResourceReference(Border.BackgroundProperty, "HoverBg");
        DockPanel.SetDock(chip, Dock.Right);
        row.Children.Add(chip);
        row.Children.Add(Ui.Text("채팅창 열기·닫기", 13));
        stack.Children.Add(row);

        hotkeyMessage = Ui.Text("", 11, "TextSecondary", wrap: true);
        hotkeyMessage.Margin = new Thickness(0, 6, 0, 0);
        hotkeyMessage.Visibility = Visibility.Collapsed;
        stack.Children.Add(hotkeyMessage);
        UpdateHotkey();
        return stack;
    }

    void UpdateHotkey()
    {
        if (hotkeyDisplay == null) return;
        hotkeyDisplay.Text = recording ? "키 조합을 누르세요…" : S.Hotkey.Display;
        if (hotkeyButton != null) hotkeyButton.Content = recording ? "취소" : "변경";
        if (hotkeyDefault != null) hotkeyDefault.Visibility = S.Hotkey != Hotkey.Default && !recording ? Visibility.Visible : Visibility.Collapsed;
    }

    void StartRecording()
    {
        if (hotkeyMessage != null) hotkeyMessage.Visibility = Visibility.Collapsed;
        keyWindow = Window.GetWindow(this);
        if (keyWindow == null) return;
        recording = true;
        keyWindow.PreviewKeyDown += OnRecordKey;
        UpdateHotkey();
    }

    void StopRecording()
    {
        if (keyWindow != null) keyWindow.PreviewKeyDown -= OnRecordKey;
        keyWindow = null;
        recording = false;
        UpdateHotkey();
    }

    void OnRecordKey(object sender, KeyEventArgs e)
    {
        if (!recording) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (key == Key.Escape) { StopRecording(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None) return;
        uint mods = 0;
        var pressed = Keyboard.Modifiers;
        if (pressed.HasFlag(ModifierKeys.Control)) mods |= Hotkey.Control;
        if (pressed.HasFlag(ModifierKeys.Alt)) mods |= Hotkey.Alt;
        if (pressed.HasFlag(ModifierKeys.Shift)) mods |= Hotkey.Shift;
        if (pressed.HasFlag(ModifierKeys.Windows)) mods |= Hotkey.Win;
        var candidate = new Hotkey((uint)KeyInterop.VirtualKeyFromKey(key), mods);
        if (candidate.IsValid)
        {
            Change(s => s.Hotkey = candidate);
            var error = App.Instance.Hotkeys?.LastError;
            hotkeyMessage!.Text = error ?? $"{candidate.Display} 로 바꿨어요. 다른 앱과 겹치면 그 앱의 단축키가 먼저 동작할 수 있어요.";
            StopRecording();
        }
        else hotkeyMessage!.Text = "Ctrl, Alt, Win 중 하나 이상을 같이 눌러 주세요.";
        hotkeyMessage!.Visibility = Visibility.Visible;
    }

    // MARK: 메이트 Claude 계정 (맥 ClaudeAccountSection)

    UIElement AccountSection()
    {
        accountText = Ui.Text("확인 중…", 13, "TextSecondary");
        accountText.TextTrimming = TextTrimming.CharacterEllipsis;
        ChatUi.Id(accountText, "ClaudeAccount");
        accountButtons = new StackPanel { Orientation = Orientation.Horizontal };
        ShowAccount();
        return EditorStyle.Section("메이트 Claude 계정", new UIElement[] { EditorStyle.Row("연결된 계정", accountText), accountButtons },
            "메이트는 터미널 Claude Code 와 따로 로그인해요. 로그인할 때 브라우저에서 쓸 조직(팀 플랜 등)을 고를 수 있어요. 사내 데이터를 어느 조직으로 보내도 되는지는 회사 기준을 따라 주세요. Microsoft 365 커넥터는 로그인한 계정의 claude.ai 설정에서 따로 연결해야 해요.");
    }

    // 계정은 화면이 나타날 때와 로그인·로그아웃 뒤에만 다시 묻는다 (설정을 하나 바꿀 때마다 claude 를 부르지 않는다)
    ClaudeAccount? account;
    bool accountLoading;
    TextBlock? accountText;
    StackPanel? accountButtons;

    void ShowAccount()
    {
        if (accountText == null || accountButtons == null) return;
        accountText.Text = accountLoading ? "확인 중…" : account?.Display ?? "claude 를 찾지 못했어요";
        accountButtons.Children.Clear();
        var login = Ui.Pill(account?.LoggedIn == true ? "다른 계정으로 로그인" : "로그인", async () =>
        {
            if (account?.LoggedIn == true) await ClaudeLogin.Logout(model.Settings);
            model.Chat.ForgetSession();
            model.Selected = SidebarItem.Chat;
            model.Chat.StartLogin(null);
        });
        ChatUi.Id(login, "ClaudeLogin");
        accountButtons.Children.Add(login);
        if (account?.LoggedIn == true)
        {
            var logout = Ui.Pill("로그아웃", async () => { await ClaudeLogin.Logout(model.Settings); await RefreshAccount(); });
            ChatUi.Id(logout, "ClaudeLogout");
            logout.Margin = new Thickness(8, 0, 0, 0);
            accountButtons.Children.Add(logout);
        }
    }

    async Task RefreshAccount()
    {
        accountLoading = true;
        ShowAccount();
        try { account = await ClaudeLogin.Account(model.Settings); }
        catch { account = null; }
        accountLoading = false;
        ShowAccount();
    }

    void OnChatChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatSession.LoginState) && model.Chat.LoginState == LoginState.Idle) _ = RefreshAccount();
    }

    // MARK: 시작 (새 항목)

    UIElement StartupSection()
    {
        CheckBox? box = null;
        box = EditorStyle.Switch("Windows 시작 시 실행", Autostart.IsEnabled, on =>
        {
            if (!Autostart.Set(on))
            {
                box!.IsChecked = Autostart.IsEnabled;
                EditorStyle.Notice("바꾸지 못했어요", "Windows 시작 프로그램 항목을 바꾸지 못했어요.");
            }
        }, "켜면 Windows 에 로그인할 때 메이트가 트레이에서 같이 시작돼요", "Autostart");
        return EditorStyle.Section("Windows", new UIElement[] { box },
            "켜면 Windows 에 로그인할 때 메이트가 트레이에서 같이 시작돼요.");
    }
}
