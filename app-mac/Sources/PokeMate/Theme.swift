import SwiftUI

enum Theme {
    static let corner: CGFloat = 14
    static let card = Color.primary.opacity(0.045)
    static let gutter: CGFloat = 14
}

extension View {
    func pmCard(_ padding: CGFloat = 14) -> some View {
        self.padding(padding)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Theme.card, in: RoundedRectangle(cornerRadius: Theme.corner, style: .continuous))
    }

    func pmListRow() -> some View {
        self.listRowSeparator(.hidden)
            .listRowInsets(EdgeInsets(top: 3, leading: Theme.gutter, bottom: 3, trailing: Theme.gutter))
            .listRowBackground(
                RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Theme.card)
                    .padding(.vertical, 2).padding(.horizontal, Theme.gutter - 4))
    }
}

struct PillButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.callout.weight(.semibold))
            .padding(.horizontal, 10)
            .padding(.vertical, 4)
            .background(Color.primary.opacity(configuration.isPressed ? 0.2 : 0.1),
                        in: RoundedRectangle(cornerRadius: 7, style: .continuous))
            .foregroundStyle(.primary)
    }
}

extension ButtonStyle where Self == PillButtonStyle {
    static var pill: PillButtonStyle { PillButtonStyle() }
}
