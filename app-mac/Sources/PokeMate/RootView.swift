import PokeMateCore
import SwiftUI

@MainActor
final class PopoverSize: ObservableObject {
    static let shared = PopoverSize()
    static let minSize = CGSize(width: 380, height: 520)
    static let maxSize = CGSize(width: 960, height: 1200)
    static let defaultSize = CGSize(width: 496, height: 1008)
    @Published var size: CGSize

    init() {
        let d = UserDefaults.standard
        let w = d.double(forKey: "popoverWidth"), h = d.double(forKey: "popoverHeight")
        size = CGSize(width: w > 0 ? w : Self.defaultSize.width, height: h > 0 ? h : Self.defaultSize.height)
    }

    func set(_ new: CGSize) {
        let w = min(Self.maxSize.width, max(Self.minSize.width, new.width.rounded()))
        let h = min(Self.maxSize.height, max(Self.minSize.height, new.height.rounded()))
        size = CGSize(width: w, height: h)
    }

    func save() {
        UserDefaults.standard.set(Double(size.width), forKey: "popoverWidth")
        UserDefaults.standard.set(Double(size.height), forKey: "popoverHeight")
    }
}

@MainActor
final class PopoverPin: ObservableObject {
    static let shared = PopoverPin()
    @Published var pinned = UserDefaults.standard.bool(forKey: "popoverPinned") {
        didSet { UserDefaults.standard.set(pinned, forKey: "popoverPinned") }
    }
}

struct ResizeGrip: View {
    @ObservedObject var popover = PopoverSize.shared
    @State private var start: (mouse: NSPoint, size: CGSize)?

    var body: some View {
        Image(systemName: "arrow.down.right.and.arrow.up.left")
            .rotationEffect(.degrees(90))
            .font(.system(size: 10, weight: .semibold))
            .foregroundStyle(.tertiary)
            .frame(width: 18, height: 18)
            .contentShape(Rectangle())
            .help("끌어서 크기 조절 · 두 번 누르면 기본 크기")
            .gesture(DragGesture(minimumDistance: 1)
                .onChanged { _ in
                    let mouse = NSEvent.mouseLocation
                    if start == nil { start = (mouse, popover.size) }
                    guard let s = start else { return }
                    popover.set(CGSize(width: s.size.width + (mouse.x - s.mouse.x) * 2,
                                       height: s.size.height + (s.mouse.y - mouse.y)))
                }
                .onEnded { _ in
                    start = nil
                    popover.save()
                })
            .onTapGesture(count: 2) {
                popover.set(PopoverSize.defaultSize)
                popover.save()
            }
    }
}

struct RootView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var popover = PopoverSize.shared

    private var tabSelection: Binding<SidebarItem?> {
        Binding(get: { SidebarItem.tabs.contains(model.selected) ? model.selected : nil },
                set: { if let tab = $0 { model.selected = tab } })
    }

    var body: some View {
        VStack(spacing: 10) {
            Picker("", selection: tabSelection) {
                ForEach(SidebarItem.tabs) { Text($0.title).tag(SidebarItem?.some($0)) }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .padding(.horizontal, Theme.gutter)
            .padding(.top, Theme.gutter)
            Group {
                switch model.selected {
                case .chat: HomeView(model: model)
                case .notes: NotesView(model: model)
                case .connections: ConnectionsView(model: model)
                case .alarms: AlarmsView(model: model)
                case .work: WorkTabView(model: model)
                case .collection: CollectionView(model: model)
                case .settings: SettingsView(model: model)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            FooterBar(model: model)
        }
        .frame(width: popover.size.width, height: popover.size.height)
        .overlay(alignment: .bottomTrailing) { ResizeGrip().padding(2) }
    }
}

struct FooterBar: View {
    @ObservedObject var model: AppModel
    @ObservedObject var usage = UsageStore.shared
    @ObservedObject var pin = PopoverPin.shared

    var body: some View {
        HStack(spacing: 14) {
            Button { usage.refresh() } label: { Image(systemName: "arrow.clockwise") }
                .help("토큰 다시 계산")
            if let updated = usage.updated {
                (Text("갱신 ") + Text(updated, style: .relative))
                    .font(.caption).foregroundStyle(.tertiary)
            }
            Spacer()
            Button { pin.pinned.toggle() } label: {
                Image(systemName: pin.pinned ? "pin.fill" : "pin")
                    .foregroundStyle(pin.pinned ? Color.accentColor : Color.secondary)
            }
            .help(pin.pinned ? "고정 풀기 · 다른 곳을 누르면 닫힘" : "고정하기 · 다른 창을 눌러도 계속 떠 있음")
            ForEach(SidebarItem.tools) { item in
                Button { model.selected = model.selected == item ? .chat : item } label: {
                    Image(systemName: item.symbol)
                        .foregroundStyle(model.selected == item ? Color.accentColor : Color.secondary)
                }
                .help(item.title)
            }
            Button { NSApp.terminate(nil) } label: { Image(systemName: "power") }.help("종료")
        }
        .buttonStyle(.borderless)
        .font(.system(size: 15))
        .foregroundStyle(.secondary)
        .padding(.horizontal, Theme.gutter + 2)
        .padding(.bottom, 12)
        .padding(.trailing, 10)
    }
}

struct HomeView: View {
    @ObservedObject var model: AppModel

    var body: some View {
        VStack(spacing: 10) {
            MateCard().pmCard(12)
            HomeHeader(model: model).pmCard(12)
            ChatView(model: model, chat: model.chat)
                .background(Color(nsColor: .textBackgroundColor).opacity(0.75))
                .clipShape(RoundedRectangle(cornerRadius: Theme.corner, style: .continuous))
        }
        .padding(.horizontal, Theme.gutter)
    }
}

@MainActor
final class UsageStore: ObservableObject {
    static let shared = UsageStore()
    @Published private(set) var summary = UsageSummary()
    @Published private(set) var updated: Date?
    @Published private(set) var loading = false
    @Published private(set) var mate = MateState()
    @Published private(set) var mateMessage: String?
    @Published private(set) var allSpecies: [MateSpecies] = []
    @Published private(set) var knownSpecies: [MateSpecies] = []
    @Published private(set) var pokedexError: String?
    @Published private(set) var pokedexProgress: PokedexProgress?
    @Published private(set) var range: PokedexRange
    private var pokedexLoading: PokedexRange?
    private let reader: ClaudeUsageReader
    private let mateFile: URL
    private var timer: Timer?

    init() {
        let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("poke-mate", isDirectory: true)
        reader = ClaudeUsageReader(cacheFile: dir.appendingPathComponent("token-cache.json"))
        mateFile = dir.appendingPathComponent("mates.json")
        mate = MateStore.load(from: mateFile)
        range = AppPaths.loadSettings().pokedexRange
        installCached()
    }

    func species(_ id: String) -> MateSpecies? { MateSpecies.find(id) }

    var hasRangeCatalog: Bool { PokedexLoader.loadCached(range) != nil }

    func applyRange(_ new: PokedexRange) {
        guard new != range else { return }
        range = new
        pokedexError = nil
        installCached()
        loadPokedex()
    }

    private func installCached() {
        let catalogs = PokedexRange.allCases.compactMap { r in PokedexLoader.loadCached(r).map { (r, $0) } }
        guard let pool = catalogs.first(where: { $0.0 == range })?.1 ?? catalogs.first?.1 else { return }
        install(pool, lookup: catalogs.map(\.1))
    }

    var pokemonName: String? {
        guard let current = mate.current, let sp = species(current.species) else { return nil }
        return sp.form(tokens: current.tokens, final: current.form)?.name
    }

    func mateName(_ settings: AppSettings) -> String {
        if let pokemonName { return pokemonName }
        let name = settings.assistantName.trimmingCharacters(in: .whitespacesAndNewlines)
        return name.isEmpty ? AppSettings.defaultAssistantName : name
    }

    func makeRepresentative(_ id: String) {
        var state = mate
        guard state.makeRepresentative(id) else { return }
        mate = state
        mateMessage = nil
        MateStore.save(state, to: mateFile)
    }

    private func install(_ catalog: PokedexCatalog, lookup: [PokedexCatalog]) {
        MateSpecies.install(catalog, lookup: lookup)
        allSpecies = MateSpecies.all
        knownSpecies = MateSpecies.known
        var state = mate
        if state.adoptUnknown() {
            mate = state
            MateStore.save(state, to: mateFile)
        }
        for mate in [state.current].compactMap({ $0 }) + state.party {
            species(mate.species)?.path(mate.form).forEach { PokemonSprites.shared.load($0.id) }
        }
    }

    func loadPokedex() {
        let target = range
        guard !hasRangeCatalog, pokedexLoading != target else { return }
        pokedexLoading = target
        pokedexError = nil
        pokedexProgress = nil
        Task {
            do {
                let catalog = try await PokedexLoader.fetch(target) { progress in
                    Task { @MainActor in if self.pokedexLoading == target { self.pokedexProgress = progress } }
                }
                PokedexLoader.save(catalog, to: PokedexLoader.catalogFile(target))
                installCached()
                if updated != nil { applyMate(allTime: summary.allTime) }
            } catch {
                if range == target { pokedexError = "\(target.label) 도감을 받지 못했어요: \(error.localizedDescription)" }
            }
            if pokedexLoading == target {
                pokedexLoading = nil
                pokedexProgress = nil
            }
        }
    }

    private func applyMate(allTime: Int) {
        var state = mate
        let events = state.advance(allTime: allTime, now: Date())
        if let last = events.last(where: { if case .started = $0 { return false } else { return true } }) {
            let base: (String) -> String = { MateSpecies.find($0)?.name ?? $0 }
            let final: (String) -> String = { id in
                let form = id == state.current?.species ? state.current?.form : state.lastForm(of: id)
                return MateSpecies.find(id)?.final(form).name ?? id
            }
            let evolved: (String, Int) -> String = { id, to in
                let form = state.lastForm(of: id) ?? state.current?.form
                let path = MateSpecies.find(id)?.paths.first { $0.contains { $0.id == to } } ?? MateSpecies.find(id)?.path(form) ?? []
                guard let i = path.firstIndex(where: { $0.id == to }), i > 0 else { return "\(base(id))(이)가 진화했어요!" }
                return "\(path[i - 1].name)(이)가 \(path[i].name)(으)로 진화했어요!"
            }
            switch last {
            case .started: break
            case .hatched(let id): mateMessage = "알에서 \(base(id))(이)가 나왔어요!"
            case .evolved(let id, let to): mateMessage = evolved(id, to)
            case .completed(let id): mateMessage = "\(final(id))(이)가 도감에 등록됐어요. 새 알이 왔어요."
            }
        }
        if state != mate {
            mate = state
            MateStore.save(state, to: mateFile)
        }
    }

    func start() {
        guard timer == nil else { return }
        loadPokedex()
        refresh()
        timer = Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.refresh() }
        }
    }

    func refresh() {
        guard !loading else { return }
        loading = true
        let reader = reader
        Task.detached(priority: .utility) {
            let s = reader.refresh()
            await MainActor.run {
                self.applyMate(allTime: s.allTime)
                self.summary = s
                self.updated = Date()
                let snapshot = s.snapshot(updated: Date())
                Task.detached(priority: .utility) { try? snapshot.write(to: UsageSummary.snapshotFile, options: .atomic) }
                self.loading = false
            }
        }
    }
}

struct HomeHeader: View {
    @ObservedObject var model: AppModel
    @ObservedObject var usage = UsageStore.shared
    @AppStorage("showDailyChart") private var showChart = true

    var body: some View {
        let s = usage.summary
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text("오늘 사용한 토큰").font(.caption).foregroundStyle(.secondary)
                Spacer()
                if usage.loading { ProgressView().controlSize(.small) }
                Image(systemName: "chart.bar.fill").font(.caption).foregroundStyle(showChart ? Color.orange : Color.secondary)
                Image(systemName: showChart ? "chevron.up" : "chevron.down").font(.caption2).foregroundStyle(.secondary)
            }
            HStack(alignment: .firstTextBaseline) {
                Text(TokenFormat.compact(s.today.total)).font(.system(size: 26, weight: .bold)).monospacedDigit()
                Text(s.today.total.formatted()).font(.caption).foregroundStyle(.secondary).monospacedDigit()
                Spacer()
                Text(TokenFormat.usd(s.todayCost)).font(.callout).foregroundStyle(.secondary)
                    .help("모델 단가로 환산한 추정 비용. 구독료·실제 청구액이 아님")
            }
            HStack(spacing: 12) {
                Text("이번 주 \(TokenFormat.compact(s.week.total)) · \(TokenFormat.usd(s.weekCost))")
                Text("이번 달 \(TokenFormat.compact(s.month.total)) · \(TokenFormat.usd(s.monthCost))")
            }
            .font(.caption).foregroundStyle(.secondary).monospacedDigit()
            if !s.todayByModel.isEmpty {
                HStack(spacing: 10) {
                    ForEach(s.todayByModel.prefix(3), id: \.model) { row in
                        Text("\(row.model.replacingOccurrences(of: "claude-", with: "")) \(TokenFormat.compact(row.bucket.total))")
                    }
                }
                .font(.caption2).foregroundStyle(.tertiary)
            }
            if !s.unpricedModels.isEmpty {
                Text("단가 없는 모델은 비용에서 빠짐: \(s.unpricedModels.joined(separator: ", "))")
                    .font(.caption2).foregroundStyle(.tertiary)
            }
            if showChart {
                Divider().padding(.vertical, 2)
                DailyUsageChart(days: s.daily)
                    .contentShape(Rectangle())
                    .onTapGesture {}
            }
        }
        .contentShape(Rectangle())
        .onTapGesture { withAnimation(.easeInOut(duration: 0.15)) { showChart.toggle() } }
        .help(showChart ? "눌러서 일별 그래프 접기" : "눌러서 일별 그래프 보기")
        .onAppear { usage.start() }
    }
}
