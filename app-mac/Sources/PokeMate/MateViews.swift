import PokeMateCore
import SwiftUI

struct PixelSprite: View {
    let grid: [String]
    let palette: [Character: UInt32]
    var size: CGFloat = 64
    var silhouette = false

    var body: some View {
        Canvas { ctx, canvas in
            let rows = grid.count
            let cols = grid.first?.count ?? 1
            let cell = min(canvas.width / CGFloat(cols), canvas.height / CGFloat(rows))
            var paths: [Character: Path] = [:]
            for (y, row) in grid.enumerated() {
                for (x, ch) in row.enumerated() where ch != "." {
                    let key: Character = silhouette ? "#" : ch
                    paths[key, default: Path()].addRect(CGRect(x: CGFloat(x) * cell, y: CGFloat(y) * cell, width: cell + 0.5, height: cell + 0.5))
                }
            }
            for (key, path) in paths {
                let color: Color = silhouette ? Color.secondary.opacity(0.35) : Color(hex: palette[key] ?? 0xFF00FF)
                ctx.fill(path, with: .color(color), style: FillStyle(eoFill: false))
            }
        }
        .frame(width: size, height: size)
    }
}

extension Color {
    init(hex: UInt32) {
        self.init(red: Double((hex >> 16) & 0xFF) / 255, green: Double((hex >> 8) & 0xFF) / 255, blue: Double(hex & 0xFF) / 255)
    }
}

struct MateSprite: View {
    let species: MateSpecies
    let tokens: Int
    var final: Int?
    var shown: Int?
    var size: CGFloat = 64
    var animated = true

    var body: some View {
        TimelineView(.animation(minimumInterval: 0.5, paused: !animated)) { context in
            let phase = Int(context.date.timeIntervalSinceReferenceDate * 2) % 2
            sprite.offset(y: animated && phase == 1 ? -2 : 0)
        }
    }

    @ViewBuilder private var sprite: some View {
        if let form = species.shownForm(tokens: tokens, final: final, shown: shown) {
            PokemonImage(id: form.id, size: size)
        } else {
            PixelSprite(grid: MateSpecies.eggGrid, palette: species.eggPalette(), size: size)
        }
    }
}

struct MateCard: View {
    @ObservedObject var usage = UsageStore.shared

    var body: some View {
        let state = usage.mate
        if let current = state.current, let species = usage.species(current.species) {
            let tokens = state.tokens
            let count = species.path(current.form).count
            let step = MateRules.step(for: tokens, count: count)
            let form = species.shownForm(tokens: tokens, final: current.form, shown: current.shown)
            let live = species.form(tokens: tokens, final: current.form)
            HStack(alignment: .center, spacing: 12) {
                MateSprite(species: species, tokens: tokens, final: current.form, shown: current.shown, size: 72)
                    .frame(width: 84, height: 84)
                    .background(Color.primary.opacity(0.06), in: RoundedRectangle(cornerRadius: 12, style: .continuous))
                VStack(alignment: .leading, spacing: 4) {
                    HStack(spacing: 6) {
                        Text(form?.name ?? "???의 알").font(.headline)
                        if let form {
                            Text("#\(form.id)").font(.caption).foregroundStyle(.secondary)
                        }
                        Text(MateRules.label(step: step)).font(.caption2.weight(.semibold))
                            .padding(.horizontal, 6).padding(.vertical, 1)
                            .background(Color.accentColor.opacity(0.15), in: Capsule())
                    }
                    if let live, let form, live.id != form.id {
                        Text("키우는 모습: \(live.name) · 컬렉션에서 고른 모습으로 보여요").font(.caption2).foregroundStyle(.secondary)
                    }
                    ProgressView(value: MateRules.stageProgress(for: tokens, count: count)).tint(.orange)
                    Text("\(MateRules.goalLabel(for: tokens, count: count))까지 \(TokenFormat.compact(max(0, MateRules.nextGoal(for: tokens, count: count) - tokens)))")
                        .font(.caption).foregroundStyle(.secondary)
                    Text(usage.mateMessage ?? form?.flavor ?? "토큰을 쓰면 알이 따뜻해져요.")
                        .font(.caption).foregroundStyle(.secondary).lineLimit(2)
                    if let progress = usage.pokedexProgress {
                        Text("\(usage.range.label) 도감 받는 중 · \(progress.text)").font(.caption2).foregroundStyle(.tertiary)
                    } else if let error = usage.pokedexError {
                        HStack(spacing: 6) {
                            Text(error).font(.caption2).foregroundStyle(.orange).lineLimit(1)
                            Button("다시 받기") { usage.loadPokedex() }.controlSize(.mini)
                        }
                    }
                }
                Spacer(minLength: 0)
            }
        } else {
            HStack(spacing: 12) {
                PixelSprite(grid: MateSpecies.eggGrid, palette: ["k": 0x3A3A4A, "f": 0xF3EADB, "s": 0x9A9AA5], size: 72)
                    .frame(width: 84, height: 84)
                    .background(Color.primary.opacity(0.06), in: RoundedRectangle(cornerRadius: 12, style: .continuous))
                VStack(alignment: .leading, spacing: 4) {
                    Text("포켓몬 도감 준비 중").font(.headline)
                    Text(usage.pokedexError ?? "PokeAPI 에서 \(usage.range.label) 도감을 받고 있어요. 처음 한 번만 받아요.\(usage.pokedexProgress.map { " (\($0.text))" } ?? "")")
                        .font(.caption).foregroundStyle(.secondary).lineLimit(2)
                    if usage.pokedexError != nil {
                        Button("다시 받기") { usage.loadPokedex() }.controlSize(.small)
                    }
                }
                Spacer(minLength: 0)
            }
        }
    }
}

extension MateRarity {
    var color: Color {
        switch self {
        case .legendary: return .orange
        case .rare: return .blue
        case .uncommon: return .green
        case .common: return .gray
        }
    }
}

struct CollectionView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var usage = UsageStore.shared
    @State private var query = ""
    @State private var rarityFilter: Set<MateRarity> = []
    @State private var sort: Sort = .number

    enum Sort: String, CaseIterable {
        case number = "번호순", name = "이름순", rarity = "희귀도순", count = "모은 횟수순"
    }

    var body: some View {
        let state = usage.mate
        let seen = state.seenDex()
        let all = entries(state, seen: seen)
        let shown = filtered(all, state: state, seen: seen)
        let inRange = Set(MateDex.entries(usage.allSpecies, maxDex: usage.range.fixedMaxDex).map(\.id))
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                HStack(spacing: 6) {
                    Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                    TextField("", text: $query, prompt: Text("이름 또는 #번호 검색…")).textFieldStyle(.plain)
                }
                .padding(.horizontal, 10).padding(.vertical, 7)
                .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 9, style: .continuous))
                Menu {
                    Picker("정렬", selection: $sort) {
                        ForEach(Sort.allCases, id: \.self) { Text($0.rawValue).tag($0) }
                    }
                    .pickerStyle(.inline)
                } label: {
                    Image(systemName: "arrow.up.arrow.down")
                }
                .menuStyle(.borderlessButton)
                .fixedSize()
                .help("정렬: \(sort.rawValue)")
            }
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text("도감").font(.headline)
                Text("\(seen.intersection(inRange).count) / \(inRange.count)마리 · \(usage.range.label)").font(.caption).foregroundStyle(.secondary)
            }
            HStack(spacing: 6) {
                ForEach(MateRarity.allCases, id: \.self) { rarity in
                    chip(rarity, count: all.filter { seen.contains($0.id) && $0.species.rarity == rarity }.count)
                }
            }
            ScrollView {
                LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 8), count: 4), spacing: 8) {
                    ForEach(shown) { entry in
                        cell(entry, state: state, seen: seen)
                    }
                }
                if shown.isEmpty {
                    Text(query.isEmpty && rarityFilter.isEmpty ? "아직 만난 친구가 없어요." : "찾는 친구가 없어요.").font(.callout).foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity).padding(.top, 30)
                }
            }
        }
        .padding(.horizontal, Theme.gutter)
    }

    private func eggEntries(_ state: MateState) -> [DexEntry] {
        ([state.current].compactMap { $0 } + state.party).compactMap { mate in
            guard let line = MateSpecies.find(mate.species), line.form(tokens: mate.tokens, final: mate.form) == nil else { return nil }
            return DexEntry(form: line.base, species: line)
        }
    }

    private func entries(_ state: MateState, seen: Set<Int>) -> [DexEntry] {
        var list = MateDex.entries(usage.knownSpecies, maxDex: nil).filter { seen.contains($0.id) }
        for egg in eggEntries(state) where !list.contains(where: { $0.id == egg.id }) { list.append(egg) }
        return list
    }

    private func filtered(_ list: [DexEntry], state: MateState, seen: Set<Int>) -> [DexEntry] {
        let q = query.trimmingCharacters(in: .whitespaces).lowercased().replacingOccurrences(of: "#", with: "")
        var out = list.filter { rarityFilter.isEmpty || rarityFilter.contains($0.species.rarity) }
        if !q.isEmpty {
            out = out.filter { String($0.form.id) == q || (seen.contains($0.id) && $0.form.name.lowercased().contains(q)) }
        }
        switch sort {
        case .number: return out.sorted { $0.form.id < $1.form.id }
        case .name: return out.sorted { $0.form.name < $1.form.name }
        case .rarity: return out.sorted { ($0.species.rarity.rawValue, $0.form.id) < ($1.species.rarity.rawValue, $1.form.id) }
        case .count: return out.sorted { (state.completedCount(form: $1.id), $0.form.id) < (state.completedCount(form: $0.id), $1.form.id) }
        }
    }

    private func chip(_ rarity: MateRarity, count: Int) -> some View {
        let on = rarityFilter.contains(rarity)
        return Button {
            if on { rarityFilter.remove(rarity) } else { rarityFilter.insert(rarity) }
        } label: {
            HStack(spacing: 4) {
                Circle().fill(rarity.color).frame(width: 7, height: 7)
                Text(rarity.label)
                Text("\(count)").fontWeight(.bold)
            }
            .font(.caption.weight(.semibold))
            .padding(.horizontal, 8).padding(.vertical, 3)
            .background(rarity.color.opacity(on ? 0.3 : 0.12), in: Capsule())
            .overlay(Capsule().stroke(rarity.color.opacity(on ? 0.8 : 0.35), lineWidth: 1))
            .opacity(count == 0 && !on ? 0.45 : 1)
        }
        .buttonStyle(.plain)
        .help(on ? "\(rarity.label) 필터 끄기" : "\(rarity.label)만 보기")
    }

    private func cell(_ entry: DexEntry, state: MateState, seen: Set<Int>) -> some View {
        let species = entry.species
        let raising = state.raising(species.id)
        let tokens = raising?.tokens ?? MateRules.completeAt
        let live = raising.flatMap { species.form(tokens: tokens, final: $0.form) }
        let shown = raising.flatMap { species.shownForm(tokens: tokens, final: $0.form, shown: $0.shown) }
        let isEgg = raising != nil && live == nil && entry.id == species.base.id
        let isNow = raising != nil && (shown?.id == entry.id || isEgg)
        let isLive = raising != nil && live?.id == entry.id
        let isCurrent = isNow && state.current?.species == species.id
        let pickable = raising != nil && !isCurrent && !isEgg && raising.flatMap { mate in
            species.step(tokens: tokens, final: mate.form).map { step in species.path(mate.form).prefix(step + 1).contains { $0.id == entry.id } }
        } == true
        let known = seen.contains(entry.id)
        let count = state.completedCount(form: entry.id)
        return VStack(spacing: 4) {
            HStack(spacing: 3) {
                Text("#\(entry.id)").font(.caption.weight(.bold)).foregroundStyle(known || isEgg ? species.rarity.color : .secondary)
                if isCurrent { Image(systemName: "star.fill").font(.caption2).foregroundStyle(Color.accentColor) }
                Spacer()
                if count > 0 { Text("×\(count)").font(.caption2.weight(.semibold)).foregroundStyle(.secondary).help("도감 등록 \(count)번") }
            }
            ZStack(alignment: .bottom) {
                if isNow, let raising {
                    MateSprite(species: species, tokens: tokens, final: raising.form, shown: raising.shown, size: 48, animated: isCurrent)
                } else {
                    PokemonImage(id: entry.id, size: 48, silhouette: !known)
                }
                if isNow || isLive {
                    Text(isCurrent ? "대표" : MateRules.label(step: species.step(tokens: tokens, final: raising?.form)))
                        .font(.caption2.weight(.bold)).foregroundStyle(isCurrent ? Color.accentColor : Color.secondary)
                        .padding(.horizontal, 5).padding(.vertical, 1)
                        .background((isCurrent ? Color.accentColor : Color.secondary).opacity(0.15), in: Capsule())
                        .offset(y: 8)
                }
            }
            .frame(height: 56)
            Text(known ? entry.form.name : "???")
                .font(.callout).lineLimit(1)
                .foregroundStyle(known ? Color.primary : Color.secondary)
                .padding(.top, 4)
        }
        .padding(8)
        .frame(maxWidth: .infinity)
        .background(isCurrent ? Color.accentColor.opacity(0.14) : Theme.card,
                    in: RoundedRectangle(cornerRadius: 12, style: .continuous))
        .contentShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
        .contextMenu {
            if isCurrent {
                Text("지금 대표예요")
            } else if pickable {
                Button("대표로 지정") { usage.makeRepresentative(species.id, form: entry.id) }
            } else if raising != nil {
                Text("아직 진화하지 않은 단계예요")
            } else {
                Text("도감 등록이 끝난 포켓몬은 대표로 지정할 수 없어요")
            }
        }
        .help(known ? "\(species.rarity.label) · \(entry.form.flavor)\(pickable ? " · 우클릭해서 대표로 지정" : "")"
                    : isEgg ? "\(species.rarity.label) · 아직 알이에요" : "\(species.rarity.label) · 아직 못 만난 친구")
    }
}
