using System.ComponentModel;
using System.Threading;
using System.Windows;
using Microsoft.Win32;

namespace PokeMate;

/// 앱 시작·끝 (맥 main.swift 의 AppDelegate). 트레이 아이콘, 창, 단축키, 알림, 백그라운드 부품을 이어 준다.
///
/// 시작 순서: 테마 → 권한 파일 → AppModel → 창 → 트레이 → Notifier → 단축키 → IAppService 들 → 토큰 집계 시작 → 창 띄우기
public partial class App : Application
{
    const string MutexName = "PokeMate.SingleInstance";
    const string ShowEventName = "PokeMate.SingleInstance.Show";

    public static App Instance => (App)Current;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);

    Mutex? mutex;
    EventWaitHandle? showEvent;
    RegisteredWaitHandle? showWait;
    AppModel? model;
    PopoverWindow? popover;
    TrayIcon? tray;
    HotkeyManager? hotkey;
    Hotkey? registeredHotkey;
    List<IAppService> services = new();
    Ui.Debounce? iconRedraw;
    bool quitting;

    public TrayIcon? Tray => tray;
    public HotkeyManager? Hotkeys => hotkey;

    static string LogFile => Path.Combine(AppPaths.DataDir, "pokemate.log");

    /// 데이터 폴더의 pokemate.log 에 한 줄 남긴다 (화면이 없는 트레이 앱이라 오류를 볼 곳이 필요하다)
    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { }
        System.Diagnostics.Debug.WriteLine(message);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 앱은 한 개만. 두 번째 실행은 첫 앱의 창을 띄우게 하고 끝난다.
        mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            AllowSetForegroundWindow(-1);   // 첫 앱이 창을 앞으로 가져올 수 있게 (ASFW_ANY)
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            mutex.Dispose();
            mutex = null;
            Shutdown();
            return;
        }
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => Dispatcher.BeginInvoke(() => popover?.ShowPopover()), null, Timeout.Infinite, false);

        DispatcherUnhandledException += (_, args) =>
        {
            Log($"처리하지 못한 오류: {args.Exception}");
            args.Handled = true;
        };

        Theme.Apply();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SessionEnding += (_, _) => { quitting = true; };

        try { ClaudeCommand.InstallPermissions(); } catch (Exception ex) { Log($"권한 파일을 쓰지 못했어요: {ex.Message}"); }

        model = new AppModel();
        popover = new PopoverWindow(model);
        tray = new TrayIcon(
            toggle: () => popover.TogglePopover(),
            open: () => popover.ShowPopover(),
            settings: () => popover.ShowSettings(),
            togglePin: () => PopoverPin.Shared.Pinned = !PopoverPin.Shared.Pinned,
            isPinned: () => PopoverPin.Shared.Pinned,
            quit: RequestQuit);
        Notifier.Attach(tray);

        hotkey = new HotkeyManager();
        model.PropertyChanged += OnModelChanged;
        ApplySettings();

        var usage = UsageStore.Shared;
        iconRedraw = new Ui.Debounce(UpdateTrayIcon, 30);
        usage.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(UsageStore.Summary)) UpdateTooltip();
            if (args.PropertyName is nameof(UsageStore.Mate) or nameof(UsageStore.AllSpecies)) iconRedraw.Poke();
        };
        PokemonSprites.Shared.Changed += () => iconRedraw.Poke();
        UpdateTrayIcon();

        services = Extensions.Services();
        foreach (var service in services)
        {
            try { service.Start(model); }
            catch (Exception ex) { Log($"{service.GetType().Name} 를 시작하지 못했어요: {ex}"); }
        }

        usage.Start();
        Dispatcher.BeginInvoke(() => popover.ShowPopover());
    }

    // MARK: 설정이 바뀔 때

    void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppModel.Settings)) ApplySettings();
    }

    /// 단축키와 도감 범위를 설정에 맞춘다
    void ApplySettings()
    {
        if (model == null || hotkey == null) return;
        var key = model.Settings.Hotkey;
        if (key != registeredHotkey)
        {
            registeredHotkey = key;
            if (!hotkey.Register(key, () => popover?.TogglePopover())) Log(hotkey.LastError ?? "단축키를 등록하지 못했어요");
        }
        UsageStore.Shared.ApplyRange(model.Settings.PokedexRange);
    }

    void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            Dispatcher.BeginInvoke(Theme.Apply);
    }

    // MARK: 트레이 아이콘 (맥 메뉴바 아이콘 + 글자)

    void UpdateTooltip()
    {
        var today = UsageStore.Shared.Summary.Today.Total;
        tray?.SetTooltip(today > 0 ? $"PokeMate · 오늘 {TokenFormat.Compact(today)}" : "PokeMate");
    }

    void UpdateTrayIcon()
    {
        if (tray == null) return;
        var usage = UsageStore.Shared;
        var side = TrayIcon.IconSide;
        System.Windows.Media.Imaging.BitmapSource? image = null;
        if (usage.Mate.Current is { } current && usage.Species(current.Species) is { } species)
            image = PokemonSprites.Shared.TrayImage(species, current.Tokens, current.Form, current.Shown, side);
        tray.SetImage(image ?? PokemonSprites.EmptyEggImage(side));
    }

    // MARK: 끝내기 (맥 applicationShouldTerminate)

    /// 처리 중인 일이 있으면 물어본 뒤 끈다
    public void RequestQuit()
    {
        if (quitting) return;
        if (model != null && !quitting)
        {
            var active = model.ActiveWork();
            if (active.Count > 0)
            {
                popover?.Hold();
                var text = string.Join("\n", active) + "\n\n지금 끄면 다음에 켤 때 끊긴 단계부터 이어서 해요.";
                var answer = MessageBox.Show(text, "처리 중인 티켓이 있어요", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                popover?.Release();
                if (answer != MessageBoxResult.OK) return;
            }
        }
        quitting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        showWait?.Unregister(null);
        if (model != null)
        {
            try { model.RaiseQuitting(); } catch (Exception ex) { Log($"끝내는 중 오류: {ex.Message}"); }
        }
        foreach (var service in services)
        {
            try { service.Stop(); } catch (Exception ex) { Log($"{service.GetType().Name} 를 멈추지 못했어요: {ex.Message}"); }
        }
        hotkey?.Dispose();
        tray?.Dispose();
        popover?.CloseForQuit();
        showEvent?.Dispose();
        if (mutex != null)
        {
            try { mutex.ReleaseMutex(); } catch { }
            mutex.Dispose();
        }
        base.OnExit(e);
    }
}
