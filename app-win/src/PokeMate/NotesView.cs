using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PokeMate;

/// 메모 메뉴 (맥 NotesView): 목록 → 누르면 편집. 고치면 바로 md 파일에 저장된다.
[PopoverTab(SidebarItem.Notes)]
public sealed class NotesView : UserControl
{
    readonly AppModel model;
    readonly Grid root = new();
    List<Note> notes = new();
    string query = "";

    public NotesView(AppModel model)
    {
        this.model = model;
        Content = root;
        Loaded += (_, _) => { model.PropertyChanged += OnModelChanged; Refresh(); };
        Unloaded += (_, _) => model.PropertyChanged -= OnModelChanged;
        Refresh();
    }

    void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppModel.OpenNote)) Refresh();
    }

    void Refresh()
    {
        root.Children.Clear();
        if (model.OpenNote is { } url)
        {
            var path = url.IsFile ? url.LocalPath : url.OriginalString;
            root.Children.Add(new NoteEditor(path, () => { model.OpenNote = null; }));
        }
        else
        {
            notes = Notes.List();
            root.Children.Add(BuildList());
        }
    }

    // MARK: 목록

    UIElement BuildList()
    {
        var column = new DockPanel();
        var header = new DockPanel { Margin = new Thickness(Theme.Gutter, 0, Theme.Gutter, 8) };
        var add = Ui.Pill("새 메모", NewNote);
        ChatUi.Id(add, "NoteNew");
        add.Content = ChatUi.Label("", "새 메모", "TextFg", 13);
        DockPanel.SetDock(add, Dock.Right);
        header.Children.Add(add);
        header.Children.Add(Ui.Text("메모", 15, "TextFg", FontWeights.SemiBold));
        DockPanel.SetDock(header, Dock.Top);
        column.Children.Add(header);

        var (frame, box) = EditorStyle.TextField(query, text => { query = text; ShowRows(); }, "메모 찾기", id: "NoteSearch");
        frame.Margin = new Thickness(Theme.Gutter, 0, Theme.Gutter, 6);
        DockPanel.SetDock(frame, Dock.Top);
        column.Children.Add(frame);

        rows = new Border();
        column.Children.Add(rows);
        ShowRows();
        return column;
    }

    Border rows = new();

    List<Note> Filtered()
    {
        var q = query.Trim().ToLowerInvariant();
        if (q.Length == 0) return notes;
        return notes.Where(n => n.Title.ToLowerInvariant().Contains(q) || Notes.Read(n.Url).ToLowerInvariant().Contains(q)).ToList();
    }

    void ShowRows()
    {
        var shown = Filtered();
        if (shown.Count == 0)
        {
            var empty = Ui.Text(notes.Count == 0 ? "메모가 없어요. [새 메모]를 누르거나, 채팅에서 \"메모해줘: …\" 라고 말해 보세요."
                                                 : $"\"{query}\" 이(가) 든 메모가 없어요.", 13, "TextSecondary", wrap: true);
            empty.TextAlignment = TextAlignment.Center;
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Margin = new Thickness(Theme.Gutter, 40, Theme.Gutter, 0);
            rows.Child = empty;
            return;
        }
        var list = new StackPanel { Margin = new Thickness(Theme.Gutter - 2, 0, Theme.Gutter - 2, 0) };
        foreach (var note in shown)
        {
            list.Children.Add(Row(note));
            list.Children.Add(EditorStyle.Divider(0));
        }
        rows.Child = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    UIElement Row(Note note)
    {
        var grid = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand };
        ChatUi.Id(grid, "NoteRow:" + note.Title);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Margin = new Thickness(2, 6, 2, 6) };
        text.Children.Add(Ui.Text(note.Title, 13, "TextFg", FontWeights.Medium));
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        line.Children.Add(Ui.Text(note.Modified.ToLocalTime().ToString("M월 d일 HH:mm", CultureInfo.GetCultureInfo("ko-KR")), 11, "TextSecondary"));
        if (note.Preview.Length > 0)
        {
            var preview = Ui.Text(note.Preview, 11, "TextSecondary");
            preview.Margin = new Thickness(6, 0, 0, 0);
            line.Children.Add(preview);
        }
        text.Children.Add(line);
        grid.Children.Add(text);

        var delete = EditorStyle.GlyphButton("", () => Delete(note), "삭제", "TextSecondary", "NoteDelete:" + note.Title);
        delete.Foreground = Brushes.IndianRed;
        Grid.SetColumn(delete, 1);
        delete.VerticalAlignment = VerticalAlignment.Top;
        delete.Margin = new Thickness(0, 6, 0, 0);
        grid.Children.Add(delete);

        grid.MouseLeftButtonUp += (_, e) =>
        {
            for (var d = e.OriginalSource as DependencyObject; d != null && d != grid; d = VisualTreeHelper.GetParent(d))
                if (d == delete) return;
            model.OpenNote = new Uri(note.Url);
        };
        grid.MouseEnter += (_, _) => grid.Background = Theme.Brush("HoverBg");
        grid.MouseLeave += (_, _) => grid.Background = Brushes.Transparent;
        return grid;
    }

    void NewNote()
    {
        try { model.OpenNote = new Uri(Notes.Create("", "")); }
        catch (Exception e) { EditorStyle.Notice("메모를 만들지 못했어요", e.Message); }
    }

    void Delete(Note note)
    {
        if (!EditorStyle.Confirm("메모를 삭제할까요?", $"\"{note.Title}\" 메모를 지워요. 되돌릴 수 없어요.")) return;
        try { Notes.Delete(note.Url); } catch (Exception e) { App.Log($"메모를 지우지 못했어요: {e.Message}"); }
        Refresh();
    }
}

/// 메모 한 개 편집. 글자를 고칠 때마다 저장한다 (마크다운 그대로).
sealed class NoteEditor : UserControl
{
    public NoteEditor(string path, Action close)
    {
        var column = new DockPanel();

        var top = new DockPanel { Margin = new Thickness(Theme.Gutter, 0, Theme.Gutter, 6) };
        var back = new Button { Content = ChatUi.Label("", "목록", "TextFg", 13), Cursor = Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0) };
        back.Template = LinkTemplate();
        ChatUi.Id(back, "NoteBack");
        back.Click += (_, _) => close();
        top.Children.Add(back);
        var external = EditorStyle.GlyphButton("", () => Opener.Open(path), "다른 앱으로 열기", id: "NoteOpenExternal");
        DockPanel.SetDock(external, Dock.Right);
        top.Children.Insert(0, external);
        DockPanel.SetDock(top, Dock.Top);
        column.Children.Add(top);

        var hint = Ui.Text("첫 줄이 제목이에요. 마크다운으로 적으면 돼요 (# 제목, - 목록, | 표 |).", 11, "TextSecondary", wrap: true);
        hint.Margin = new Thickness(Theme.Gutter, 8, Theme.Gutter, 8);
        DockPanel.SetDock(hint, Dock.Bottom);
        column.Children.Add(hint);

        var editor = new TextBox
        {
            Text = Notes.Read(path), AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, FontFamily = new FontFamily("Cascadia Mono, Consolas, Malgun Gothic"), FontSize = 13,
        };
        editor.SetResourceReference(Control.ForegroundProperty, "TextFg");
        editor.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty, "TextFg");
        ChatUi.Id(editor, "NoteText");
        editor.TextChanged += (_, _) =>
        {
            try { Notes.Save(path, editor.Text); } catch (Exception e) { App.Log($"메모를 저장하지 못했어요: {e.Message}"); }
        };
        var card = Ui.Card(editor, 10);
        card.Margin = new Thickness(Theme.Gutter, 0, Theme.Gutter, 0);
        column.Children.Add(card);
        Content = column;
    }

    static ControlTemplate LinkTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        template.VisualTree = presenter;
        return template;
    }
}
