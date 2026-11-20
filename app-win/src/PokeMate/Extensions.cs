using System.Reflection;
using System.Windows;

namespace PokeMate;

/// 탭·도구 화면을 PopoverWindow 에 꽂는 표시. 클래스(UserControl 등 UIElement)에 붙이고 `public X(AppModel model)` 생성자를 둔다.
/// 어셈블리를 훑어 자동으로 찾으므로 다른 파일을 고치지 않아도 된다.
///   [PopoverTab(SidebarItem.Alarms)] public partial class AlarmsView : UserControl { public AlarmsView(AppModel model) { ... } }
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class PopoverTabAttribute : Attribute
{
    public SidebarItem Item { get; }
    public PopoverTabAttribute(SidebarItem item) => Item = item;
}

/// 홈 탭의 카드 아래 칸(맥 HomeView 의 ChatView 자리)에 꽂는 표시. 한 클래스만 둔다. 생성자는 `public X(AppModel model)`.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class HomeChatAttribute : Attribute { }

/// 앱이 시작될 때 켜지고 끝날 때 멈추는 백그라운드 부품 (알람 스케줄러, 트리거 감시, 작업 세션, 미리 알림 …).
/// 공개 매개변수 없는 생성자를 가진 클래스에 이 인터페이스를 구현하면 App 이 자동으로 찾아 Start(UI 스레드)를 부른다.
public interface IAppService
{
    /// 작은 값이 먼저 시작한다 (같으면 클래스 이름 순)
    int Order => 0;
    void Start(AppModel model);
    void Stop() { }
}

/// 어셈블리에서 위 표시를 찾는 곳
public static class Extensions
{
    static Dictionary<SidebarItem, Func<AppModel, UIElement>>? tabs;
    static Func<AppModel, UIElement>? homeChat;

    static IEnumerable<Type> Types() => typeof(Extensions).Assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract);

    static Func<AppModel, UIElement>? Factory(Type type)
    {
        var ctor = type.GetConstructor(new[] { typeof(AppModel) });
        if (ctor == null || !typeof(UIElement).IsAssignableFrom(type)) return null;
        return model => (UIElement)ctor.Invoke(new object[] { model });
    }

    /// 탭·도구 화면 만드는 함수 (없으면 null)
    public static Func<AppModel, UIElement>? Tab(SidebarItem item)
    {
        if (tabs == null)
        {
            var found = new Dictionary<SidebarItem, Func<AppModel, UIElement>>();
            foreach (var type in Types())
                if (type.GetCustomAttribute<PopoverTabAttribute>() is { } attribute && Factory(type) is { } factory)
                    found[attribute.Item] = factory;
            tabs = found;
        }
        return tabs.GetValueOrDefault(item);
    }

    public static Func<AppModel, UIElement>? HomeChat()
    {
        if (homeChat == null)
            foreach (var type in Types())
                if (type.GetCustomAttribute<HomeChatAttribute>() != null && Factory(type) is { } factory) { homeChat = factory; break; }
        return homeChat;
    }

    public static List<IAppService> Services() => Types()
        .Where(t => typeof(IAppService).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null)
        .Select(t => (IAppService)Activator.CreateInstance(t)!)
        .OrderBy(s => s.Order).ThenBy(s => s.GetType().Name, StringComparer.Ordinal)
        .ToList();
}
