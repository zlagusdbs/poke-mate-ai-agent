import CoreGraphics
import Foundation

public enum PanelPlacement {
    public enum Edge: String, Sendable { case bottom, left, right }

    public static func safeFrame(visible: CGRect, dockEdge: Edge, dockHidden: Bool, dockSize: CGFloat) -> CGRect {
        guard dockHidden else { return visible }
        let inset = max(0, dockSize)
        switch dockEdge {
        case .bottom: return CGRect(x: visible.minX, y: visible.minY + inset, width: visible.width, height: max(0, visible.height - inset))
        case .left: return CGRect(x: visible.minX + inset, y: visible.minY, width: max(0, visible.width - inset), height: visible.height)
        case .right: return CGRect(x: visible.minX, y: visible.minY, width: max(0, visible.width - inset), height: visible.height)
        }
    }

    public static func defaultFrame(size: CGSize, safe: CGRect, margin: CGFloat = 16) -> CGRect {
        let height = min(size.height, max(0, safe.height - margin * 2))
        return CGRect(x: safe.maxX - size.width - margin, y: safe.minY + margin, width: size.width, height: height)
    }

    public static func clamp(_ frame: CGRect, into safe: CGRect, margin: CGFloat = 16) -> CGRect {
        let height = min(frame.height, max(0, safe.height - margin * 2))
        let width = min(frame.width, max(0, safe.width - margin * 2))
        let x = min(max(frame.minX, safe.minX + margin), safe.maxX - margin - width)
        let y = min(max(frame.minY, safe.minY + margin), safe.maxY - margin - height)
        return CGRect(x: x, y: y, width: width, height: height)
    }
}
