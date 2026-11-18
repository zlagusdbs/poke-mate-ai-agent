import AppKit
import PokeMateCore
import SwiftUI

@MainActor
final class PokemonSprites: ObservableObject {
    static let shared = PokemonSprites()
    @Published private(set) var images: [Int: NSImage] = [:]
    private var loading: Set<Int> = []

    func image(_ id: Int) -> NSImage? {
        if let image = images[id] { return image }
        load(id)
        return images[id]
    }

    func load(_ id: Int) {
        guard images[id] == nil, !loading.contains(id) else { return }
        if let data = try? Data(contentsOf: PokedexLoader.spriteFile(id)), let image = NSImage(data: data).flatMap(Self.trimmed) {
            images[id] = image
            return
        }
        loading.insert(id)
        Task {
            let data = await PokedexLoader.sprite(id)
            loading.remove(id)
            if let data, let image = NSImage(data: data).flatMap(Self.trimmed) { images[id] = image }
        }
    }

    static func trimmed(_ image: NSImage) -> NSImage? {
        guard let source = image.cgImage(forProposedRect: nil, context: nil, hints: nil),
              let ctx = CGContext(data: nil, width: source.width, height: source.height, bitsPerComponent: 8, bytesPerRow: source.width * 4,
                                  space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return image }
        ctx.draw(source, in: CGRect(x: 0, y: 0, width: source.width, height: source.height))
        guard let cg = ctx.makeImage(), let raw = ctx.data else { return image }
        let bytes = raw.assumingMemoryBound(to: UInt8.self)
        let alphaOffset = 3
        var minX = cg.width, minY = cg.height, maxX = -1, maxY = -1
        for y in 0..<cg.height {
            for x in 0..<cg.width where bytes[y * cg.bytesPerRow + x * 4 + alphaOffset] > 0 {
                minX = min(minX, x); maxX = max(maxX, x)
                minY = min(minY, y); maxY = max(maxY, y)
            }
        }
        guard maxX >= minX, maxY >= minY,
              let cropped = cg.cropping(to: CGRect(x: minX, y: minY, width: maxX - minX + 1, height: maxY - minY + 1)) else { return image }
        return NSImage(cgImage: cropped, size: NSSize(width: cropped.width, height: cropped.height))
    }

    func menuBarIcon(species: MateSpecies, tokens: Int, final: Int?, height: CGFloat = 20) -> NSImage? {
        guard let form = species.form(tokens: tokens, final: final) else {
            return Self.pixelImage(grid: MateSpecies.eggGrid, palette: species.eggPalette(), side: height)
        }
        guard let sprite = image(form.id) else { return nil }
        let icon = NSImage(size: NSSize(width: height, height: height), flipped: false) { rect in
            NSGraphicsContext.current?.imageInterpolation = .none
            let scale = min(rect.width / sprite.size.width, rect.height / sprite.size.height)
            let w = sprite.size.width * scale, h = sprite.size.height * scale
            sprite.draw(in: NSRect(x: (rect.width - w) / 2, y: 0, width: w, height: h))
            return true
        }
        icon.isTemplate = false
        return icon
    }

    static func emptyEggIcon(height: CGFloat = 20) -> NSImage {
        pixelImage(grid: MateSpecies.eggGrid, palette: ["k": 0x3A3A4A, "f": 0xF3EADB, "s": 0x9A9AA5], side: height)
    }

    static func pixelImage(grid: [String], palette: [Character: UInt32], side: CGFloat) -> NSImage {
        NSImage(size: NSSize(width: side, height: side), flipped: true) { rect in
            let rows = grid.count, cols = grid.first?.count ?? 1
            let cell = min(rect.width / CGFloat(cols), rect.height / CGFloat(rows))
            for (y, row) in grid.enumerated() {
                for (x, ch) in row.enumerated() where ch != "." {
                    let hex = palette[ch] ?? 0xFF00FF
                    NSColor(red: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
                            blue: CGFloat(hex & 0xFF) / 255, alpha: 1).setFill()
                    NSRect(x: CGFloat(x) * cell, y: CGFloat(y) * cell, width: cell, height: cell).fill()
                }
            }
            return true
        }
    }
}

struct PokemonImage: View {
    let id: Int
    var size: CGFloat = 64
    var silhouette = false
    @ObservedObject private var sprites = PokemonSprites.shared

    var body: some View {
        Group {
            if let image = sprites.images[id] {
                if silhouette {
                    Image(nsImage: image).renderingMode(.template).interpolation(.none).resizable()
                        .aspectRatio(contentMode: .fit).foregroundStyle(Color.secondary.opacity(0.35))
                } else {
                    Image(nsImage: image).interpolation(.none).resizable().aspectRatio(contentMode: .fit)
                }
            } else {
                ProgressView().controlSize(.small)
            }
        }
        .frame(width: size, height: size)
        .onAppear { sprites.load(id) }
    }
}
