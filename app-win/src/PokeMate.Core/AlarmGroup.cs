namespace PokeMate.Core;

/// 사용자가 만든 알림 그룹 하나와 그 안의 알림.
public sealed record AlarmGroup(string Name, IReadOnlyList<Alarm> Alarms)
{
    public string Id => Name;

    /// 그룹 순서대로 나누고, 그룹이 없거나 목록에 없는 그룹이면 "그룹 없음" 으로 돌린다. 빈 그룹도 보인다.
    public static (List<AlarmGroup> Groups, List<Alarm> Ungrouped) Make(IEnumerable<Alarm> alarms, IReadOnlyList<string> groups)
    {
        var all = alarms.ToList();
        var made = groups.Select(name => new AlarmGroup(name, all.Where(a => a.Group == name).ToList())).ToList();
        var ungrouped = all.Where(a => a.Group == null || !groups.Contains(a.Group)).ToList();
        return (made, ungrouped);
    }

    /// 접혀 있을 때 보일 한 줄. 예: "09:00 · 13:00 · 18:00 매일", 트리거가 섞이면 "트리거 2"
    public string Summary
    {
        get
        {
            var timed = Alarms.Where(a => a.Trigger == null).ToList();
            var parts = new List<string>();
            if (timed.Count > 0)
            {
                var times = string.Join(" · ", timed.Select(a => a.TimeText));
                var rules = timed.Select(a => a.RepeatRule.Description).Distinct().ToList();
                parts.Add(rules.Count == 1 ? $"{times} {rules[0]}" : times);
            }
            var triggers = Alarms.Count - timed.Count;
            if (triggers > 0) parts.Add($"트리거 {triggers}");
            return parts.Count == 0 ? "비어 있어요. 알림을 끌어다 놓으세요." : string.Join(" · ", parts);
        }
    }

    public bool AnyEnabled => Alarms.Any(a => a.Enabled);

    public bool Equals(AlarmGroup? o) => o is not null && Name == o.Name && Alarms.SequenceEqual(o.Alarms);
    public override int GetHashCode() => Name.GetHashCode();
}

/// 알람 목록의 순서 바꾸기 (순서는 alarms.json 배열 순서). 모두 새 목록을 돌려주고 원래 목록과 알람은 건드리지 않는다.
public static class AlarmOrder
{
    static string? Key(Alarm alarm, IReadOnlyList<string> groups) => alarm.Group != null && groups.Contains(alarm.Group) ? alarm.Group : null;

    public static List<Alarm> Move(IReadOnlyList<Alarm> alarms, Guid id, string? group, Guid? before, IReadOnlyList<string> groups)
    {
        var i = alarms.ToList().FindIndex(a => a.Id == id);
        if (before == id || i < 0) return alarms.ToList();
        var list = alarms.ToList();
        var item = list[i] with { Group = group };
        list.RemoveAt(i);
        var t = before is Guid target ? list.FindIndex(a => a.Id == target) : -1;
        if (t >= 0) list.Insert(t, item);
        else
        {
            var last = list.FindLastIndex(a => Key(a, groups) == group);
            if (last >= 0) list.Insert(last + 1, item);
            else list.Add(item);
        }
        return list;
    }

    public static Guid? Neighbor(IReadOnlyList<Alarm> alarms, Guid id, bool up, IReadOnlyList<string> groups)
    {
        var alarm = alarms.FirstOrDefault(a => a.Id == id);
        if (alarm == null) return null;
        var same = alarms.Where(a => Key(a, groups) == Key(alarm, groups)).ToList();
        var i = same.FindIndex(a => a.Id == id);
        if (i < 0) return null;
        var j = up ? i - 1 : i + 1;
        return j >= 0 && j < same.Count ? same[j].Id : null;
    }

    public static List<Alarm> Shift(IReadOnlyList<Alarm> alarms, Guid id, bool up, IReadOnlyList<string> groups)
    {
        var list = alarms.ToList();
        if (Neighbor(alarms, id, up, groups) is not Guid other) return list;
        var a = list.FindIndex(x => x.Id == id);
        var b = list.FindIndex(x => x.Id == other);
        if (a < 0 || b < 0) return list;
        (list[a], list[b]) = (list[b], list[a]);
        return list;
    }
}

/// 그룹 순서 바꾸기 (순서는 alarm-groups.json 배열 순서, 맥 GroupOrder 와 같다). 모두 새 목록을 돌려준다.
public static class GroupOrder
{
    /// 끌어다 놓기: name 을 target 자리로 옮긴다. 위로 끌면 target 앞, 아래로 끌면 target 뒤. target 이 null 이면 맨 끝으로.
    public static List<string> Move(IReadOnlyList<string> groups, string name, string? target)
    {
        var list = groups.ToList();
        var i = list.IndexOf(name);
        if (i < 0 || name == target) return list;
        var t = target == null ? list.Count - 1 : list.IndexOf(target);
        if (t < 0) return list;
        list.RemoveAt(i);
        list.Insert(t, name);
        return list;
    }

    public static bool CanShift(IReadOnlyList<string> groups, string name, bool up)
    {
        var i = groups.ToList().IndexOf(name);
        return i >= 0 && (up ? i > 0 : i < groups.Count - 1);
    }

    /// 오른쪽 클릭 "위로/아래로 옮기기": 이웃 그룹과 자리를 바꾼다.
    public static List<string> Shift(IReadOnlyList<string> groups, string name, bool up)
    {
        var list = groups.ToList();
        if (!CanShift(groups, name, up)) return list;
        var i = list.IndexOf(name);
        var j = up ? i - 1 : i + 1;
        (list[i], list[j]) = (list[j], list[i]);
        return list;
    }
}
