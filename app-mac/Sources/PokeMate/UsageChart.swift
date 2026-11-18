import Charts
import PokeMateCore
import SwiftUI

struct DailyUsageChart: View {
    let days: [DailyUsage]
    @State private var range = 14
    @State private var selected: Date?

    private static let parser: DateFormatter = {
        let f = DateFormatter()
        f.calendar = Calendar(identifier: .gregorian)
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyy-MM-dd"
        return f
    }()

    private struct Point: Identifiable {
        let usage: DailyUsage
        let date: Date
        var id: String { usage.day }
    }

    private var points: [Point] {
        days.suffix(range).compactMap { u in Self.parser.date(from: u.day).map { Point(usage: u, date: $0) } }
    }

    private func point(near date: Date?) -> Point? {
        guard let date else { return nil }
        return points.min { abs($0.date.timeIntervalSince(date)) < abs($1.date.timeIntervalSince(date)) }
    }

    var body: some View {
        let points = points
        let focus = point(near: selected) ?? points.last
        let total = points.reduce(0) { $0 + $1.usage.total }
        let cost = points.reduce(0.0) { $0 + $1.usage.cost }
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Picker("", selection: $range) {
                    Text("7일").tag(7)
                    Text("14일").tag(14)
                    Text("30일").tag(30)
                }
                .pickerStyle(.segmented).labelsHidden().fixedSize()
                Spacer()
                Text("합계 \(TokenFormat.compact(total)) · \(TokenFormat.usd(cost))  하루 평균 \(TokenFormat.compact(points.isEmpty ? 0 : total / points.count))")
                    .font(.caption).foregroundStyle(.secondary).monospacedDigit()
            }
            if let focus {
                HStack(spacing: 6) {
                    Text(focus.date, format: .dateTime.month().day().weekday(.abbreviated)).fontWeight(.semibold)
                    Text("\(TokenFormat.compact(focus.usage.total)) · \(TokenFormat.usd(focus.usage.cost))").monospacedDigit()
                    Text(models(focus.usage)).foregroundStyle(.tertiary).lineLimit(1)
                }
                .font(.caption)
            }
            Chart(points) { p in
                BarMark(x: .value("날짜", p.date, unit: .day), y: .value("토큰", p.usage.total))
                    .foregroundStyle(p.id == focus?.id ? Color.orange : Color.orange.opacity(0.45))
                    .cornerRadius(2)
            }
            .chartXSelection(value: $selected)
            .chartYAxis {
                AxisMarks(position: .leading, values: .automatic(desiredCount: 3)) { value in
                    AxisGridLine()
                    AxisValueLabel { if let v = value.as(Int.self) { Text(TokenFormat.compact(v)) } }
                }
            }
            .chartXAxis {
                AxisMarks(values: .stride(by: .day, count: range > 14 ? 7 : range > 7 ? 2 : 1)) { _ in
                    AxisGridLine()
                    AxisValueLabel(format: .dateTime.month(.defaultDigits).day(), centered: true)
                }
            }
            .frame(height: 120)
        }
    }

    private func models(_ u: DailyUsage) -> String {
        u.byModel.sorted { $0.value > $1.value }.prefix(3)
            .map { "\($0.key.replacingOccurrences(of: "claude-", with: "")) \(TokenFormat.compact($0.value))" }
            .joined(separator: "  ")
    }
}
