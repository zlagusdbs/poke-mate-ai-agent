import Foundation

/// 사용자가 만든 알림 그룹 하나와 그 안의 알림.
public struct AlarmGroup: Identifiable, Equatable {
    public let name: String
    public let alarms: [Alarm]
    public var id: String { name }

    /// 그룹 순서대로 나누고, 그룹이 없거나 목록에 없는 그룹이면 "그룹 없음" 으로 돌린다. 빈 그룹도 보인다.
    public static func make(_ alarms: [Alarm], groups: [String]) -> (groups: [AlarmGroup], ungrouped: [Alarm]) {
        let made = groups.map { name in AlarmGroup(name: name, alarms: alarms.filter { $0.group == name }) }
        let ungrouped = alarms.filter { $0.group.map { !groups.contains($0) } ?? true }
        return (made, ungrouped)
    }

    /// 접혀 있을 때 보일 한 줄. 예: "09:00 · 13:00 · 18:00 매일", 트리거가 섞이면 "트리거 2"
    public var summary: String {
        let timed = alarms.filter { $0.trigger == nil }
        var parts: [String] = []
        if !timed.isEmpty {
            let times = timed.map(\.timeText).joined(separator: " · ")
            let rules = Set(timed.map(\.repeatRule.description))
            parts.append(rules.count == 1 ? "\(times) \(rules.first!)" : times)
        }
        let triggers = alarms.count - timed.count
        if triggers > 0 { parts.append("트리거 \(triggers)") }
        return parts.isEmpty ? "비어 있어요. 알림을 끌어다 놓으세요." : parts.joined(separator: " · ")
    }

    public var anyEnabled: Bool { alarms.contains(where: \.enabled) }
}

public enum AlarmOrder {
    static func key(_ alarm: Alarm, groups: [String]) -> String? {
        alarm.group.flatMap { groups.contains($0) ? $0 : nil }
    }

    public static func move(_ alarms: [Alarm], id: UUID, to group: String?, before target: UUID?, groups: [String]) -> [Alarm] {
        guard target != id, let i = alarms.firstIndex(where: { $0.id == id }) else { return alarms }
        var list = alarms
        var item = list.remove(at: i)
        item.group = group
        if let target, let t = list.firstIndex(where: { $0.id == target }) {
            list.insert(item, at: t)
        } else if let last = list.lastIndex(where: { key($0, groups: groups) == group }) {
            list.insert(item, at: last + 1)
        } else {
            list.append(item)
        }
        return list
    }

    public static func neighbor(_ alarms: [Alarm], id: UUID, up: Bool, groups: [String]) -> UUID? {
        guard let alarm = alarms.first(where: { $0.id == id }) else { return nil }
        let same = alarms.filter { key($0, groups: groups) == key(alarm, groups: groups) }
        guard let i = same.firstIndex(where: { $0.id == id }) else { return nil }
        let j = up ? i - 1 : i + 1
        return same.indices.contains(j) ? same[j].id : nil
    }

    public static func shift(_ alarms: [Alarm], id: UUID, up: Bool, groups: [String]) -> [Alarm] {
        guard let other = neighbor(alarms, id: id, up: up, groups: groups),
              let a = alarms.firstIndex(where: { $0.id == id }), let b = alarms.firstIndex(where: { $0.id == other }) else { return alarms }
        var list = alarms
        list.swapAt(a, b)
        return list
    }
}
