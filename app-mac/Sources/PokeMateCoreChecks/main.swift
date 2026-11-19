import Foundation
import PokeMateCore

// Xcode 없이 XCTest 를 쓸 수 없어서 만든 검증 프로그램. `swift run PokeMateCoreChecks`
// 안내 문구 미리 보기: POKEMATE_PRINT=rule-hint / worklog-hint / hints(POKEMATE_TEXT) / jira-hint
if let what = ProcessInfo.processInfo.environment["POKEMATE_PRINT"] {
    if what == "rule-hint" { print(RuleProposal.hint); exit(0) }
    if what == "rules" { print(Rules.prompt(for: ProcessInfo.processInfo.environment["POKEMATE_TEXT"] ?? "")); exit(0) }
    if what == "hints" {   // POKEMATE_TEXT 의 문장에 붙을 안내 전체
        let text = ProcessInfo.processInfo.environment["POKEMATE_TEXT"] ?? ""
        print(ChatHints.for(text, now: Date(), calendar: .current)); exit(0)
    }
    if what == "worklog-hint" { print(WorklogProposal.hint(now: Date(), calendar: .current)); exit(0) }
    print(JiraProposal.hint(now: Date(), calendar: .current))
    exit(0)
}
var failures = 0
var passed = 0

func check(_ condition: @autoclosure () -> Bool, _ name: String) {
    if condition() { passed += 1 } else { failures += 1; print("FAIL: \(name)") }
}

// MARK: ClaudeCommand
do {
    let a = ClaudeCommand.arguments(sessionId: nil, mcpConfigPath: "/c.json", systemPrompt: "sys")
    check(a.first == "-p" && a.contains("stream-json") && !a.contains("질문"), "arguments: 질문은 인자가 아니라 표준 입력으로")
    let line = ClaudeCommand.inputLine(prompt: "질문", images: [ImageAttachment(mediaType: "image/png", data: Data([1, 2]), path: "/x")])
    let parsed = (try? JSONSerialization.jsonObject(with: Data(line.utf8))) as? [String: Any]
    let content = ((parsed?["message"] as? [String: Any])?["content"] as? [[String: Any]]) ?? []
    check(content.count == 2 && content[0]["type"] as? String == "image" && content[1]["text"] as? String == "질문"
          && line.hasSuffix("\n"), "inputLine: 이미지 먼저, 질문 뒤, 한 줄")
    check(a.contains("--mcp-config"), "arguments: 메이트 MCP 설정")
    if let i = a.firstIndex(of: "--allowedTools") {
        let allowed = a[i + 1].split(separator: ",").map(String.init)
        check(allowed.contains("mcp__pokemate__jira_search") && allowed.contains("mcp__pokemate__wiki_search")
              && allowed.contains("mcp__claude_ai_Microsoft_365__outlook_email_search")
              && allowed.allSatisfy { $0.hasPrefix("mcp__pokemate__") || $0.hasPrefix("mcp__claude_ai_Microsoft_365__") }, "arguments: 허용 도구는 메이트·M365 조회만")
    } else { check(false, "arguments: 허용 도구 옵션") }
    check(!a.contains("--resume"), "arguments: 세션 없으면 resume 없음")
    check(!a.contains("--model") && !a.contains("--effort") && !a.contains("--settings"), "arguments: 값을 안 주면 모델·추론·홈 지침 옵션 없음")
    check(ClaudeCommand.arguments(sessionId: nil, mcpConfigPath: "/c.json", systemPrompt: "s", homeExcludes: ClaudeCommand.homeExcludes(settings: .default, automation: false)).contains("--settings")
          && AppSettings.default.useHomeInstructions == false, "arguments: 앱 기본 설정은 홈 지침을 뺌")
    var homeOn = AppSettings.default
    homeOn.useHomeInstructions = true
    let ruleFiles = ["cli-usage.md", "commit-pr.md", "domain-knowledge.md", "new-rule.md", "production-env.md"]
    check(ClaudeCommand.homeExcludes(settings: homeOn, automation: false, home: "/u", rules: ruleFiles).isEmpty
          && ClaudeCommand.arguments(sessionId: nil, mcpConfigPath: "/c.json", systemPrompt: "s", homeExcludes: []).contains("--settings") == false,
          "home: 대화는 개인 지침 전부 읽기")
    check(ClaudeCommand.homeExcludes(settings: homeOn, automation: true, home: "/u", rules: ruleFiles)
          == ["/u/.claude/CLAUDE.md", "/u/CLAUDE.md", "/u/.claude/rules/*/**", "/u/.claude/rules/cli-usage.md", "/u/.claude/rules/new-rule.md"],
          "home: 자동 처리는 목차 빼고 체크한 rules 만 (새 파일은 해제 상태)")
    homeOn.homeInAutomation = false
    homeOn.homeInChat = false
    check(ClaudeCommand.homeExcludes(settings: homeOn, automation: true, home: "/u", rules: ruleFiles) == ["/u/.claude/**", "/u/CLAUDE.md"]
          && ClaudeCommand.homeExcludes(settings: homeOn, automation: false, home: "/u", rules: ruleFiles) == ["/u/.claude/**", "/u/CLAUDE.md"],
          "home: 끄면 전부 뺌")
    let oldOn = try? JSONDecoder().decode(AppSettings.self, from: Data(#"{"useHomeInstructions": true}"#.utf8))
    let oldOff = try? JSONDecoder().decode(AppSettings.self, from: Data(#"{"useHomeInstructions": false}"#.utf8))
    check(oldOn?.homeInChat == true && oldOn?.homeInAutomation == true && oldOn?.automationRules == ["commit-pr.md", "domain-knowledge.md", "production-env.md"]
          && oldOff?.homeInChat == false && oldOff?.homeInAutomation == false, "home: 예전 설정 이전 (켜져 있으면 기본값, 꺼져 있으면 둘 다 끔)")
    let tuned = ClaudeCommand.arguments(sessionId: nil, mcpConfigPath: "/c.json", systemPrompt: "s", model: "sonnet", effort: "low", homeExcludes: ["/x"])
    check(tuned[tuned.firstIndex(of: "--model")! + 1] == "sonnet" && tuned[tuned.firstIndex(of: "--effort")! + 1] == "low", "arguments: 모델·추론 수준")
    check(ClaudeCommand.homeInstructionExcludes(home: "/Users/u") == #"{"claudeMdExcludes":["/Users/u/.claude/**","/Users/u/CLAUDE.md"]}"#, "arguments: 홈 지침 빼기")
    let wrong = ClaudeCommand.arguments(sessionId: nil, mcpConfigPath: "/c.json", systemPrompt: "s", model: "gpt", effort: "ultra")
    check(!wrong.contains("--model") && !wrong.contains("--effort"), "arguments: 모르는 값은 안 넘김")
    let b = ClaudeCommand.arguments(sessionId: "s1", mcpConfigPath: "/c.json", systemPrompt: "sys")
    if let i = b.firstIndex(of: "--resume") { check(b[i + 1] == "s1", "arguments: resume 뒤 세션 ID") }
    else { check(false, "arguments: 세션 있으면 resume") }
}

// MARK: 쓰기 허용 알림
do {
    let plain = ClaudeCommand.arguments(sessionId: nil, mcpConfigPath: "/c.json", systemPrompt: "s")
    check(plain[plain.firstIndex(of: "--tools")! + 1] == "", "write: 기본은 기본 도구 모두 끔")
    let w = ClaudeCommand.arguments(sessionId: nil, mcpConfigPath: "/c.json", systemPrompt: "s", writeTools: ["Bash(python3 /x/tool.py:*)"])
    check(w[w.firstIndex(of: "--tools")! + 1] == "Read,Grep,Glob,Bash", "write: 쓰기 허용이면 Read·Grep·Glob·Bash 만 켬")
    let allowed = w[w.firstIndex(of: "--allowedTools")! + 1].split(separator: ",").map(String.init)
    check(allowed.contains("Bash(python3 /x/tool.py:*)") && !allowed.contains("Read") && !allowed.contains("Bash")
          && allowed.contains("mcp__pokemate__jira_search"), "write: 허용은 적은 명령만, Bash·Read 전체는 아님 (읽기는 작업 폴더·추가 폴더 안만)")
    let e = ClaudeCommand.arguments(sessionId: nil, mcpConfigPath: "/c.json", systemPrompt: "s",
                                    writeTools: ["Bash(x:*)", "Edit(//w/**)", "Write(//w/**)"], writeDirs: ["/w", "/refs"])
    check(e[e.firstIndex(of: "--tools")! + 1] == "Read,Grep,Glob,Bash,Edit,Write"
          && (e[e.firstIndex(of: "--allowedTools")! + 1]).contains("Edit(//w/**)") && e.filter { $0 == "--add-dir" }.count == 2,
          "write: Edit·Write 패턴이 있을 때만 그 도구를 켜고 허용은 그 경로만")
    let k = ClaudeCommand.arguments(sessionId: nil, mcpConfigPath: "/c.json", systemPrompt: "s", writeTools: ["Bash(x:*)", "Skill(code-review)"])
    check(k[k.firstIndex(of: "--tools")! + 1] == "Read,Grep,Glob,Bash,Skill"
          && (k[k.firstIndex(of: "--allowedTools")! + 1]).contains("Skill(code-review)"),
          "write: Skill 패턴이 있을 때만 Skill 도구를 켜고 허용은 그 스킬만")
    let file = FileManager.default.temporaryDirectory.appendingPathComponent("kb-write-tools-\(UUID().uuidString).json")
    try? #"[" Bash(git -C:*) ", ""]"#.write(to: file, atomically: true, encoding: .utf8)
    check(ClaudeCommand.loadWriteTools(from: file) == ["Bash(git -C:*)"], "write: 목록 파일 읽기 (공백·빈 값 제거)")
    check(ClaudeCommand.loadWriteTools(from: file.appendingPathExtension("none")).isEmpty, "write: 파일 없으면 빈 목록")
    try? #"{"tools":["Bash(x:*)"],"dirs":["~/Workspace"," /abs "]}"#.write(to: file, atomically: true, encoding: .utf8)
    check(ClaudeCommand.loadWriteAccess(from: file, home: "/Users/u") == .init(tools: ["Bash(x:*)"], dirs: ["/Users/u/Workspace", "/abs"]), "write: 도구·폴더 목록 형식")
    let d = ClaudeCommand.arguments(sessionId: nil, mcpConfigPath: "/c.json", systemPrompt: "s", writeTools: ["Bash(x:*)"], writeDirs: ["/w"])
    check(d[d.firstIndex(of: "--add-dir")! + 1] == "/w", "write: 쓰기 허용이면 폴더 추가")
    check(!ClaudeCommand.arguments(sessionId: nil, mcpConfigPath: "/c.json", systemPrompt: "s", writeDirs: ["/w"]).contains("--add-dir"), "write: 쓰기 꺼지면 폴더 추가 없음")
    func value(_ args: [String], _ flag: String) -> String? { args.firstIndex(of: flag).map { args[$0 + 1] } }
    let chat = ClaudeCommand.arguments(sessionId: nil, mcpConfigPath: "/c.json", systemPrompt: "s", writeTools: ["Bash(x:*)"], writeDirs: ["/w"],
                                       interactive: true, sessionAllow: ["Bash(ls:*)"], home: "/Users/u")
    check(value(chat, "--tools") == "default" && value(chat, "--permission-prompts") == "host" && value(chat, "--permission-prompt-tool") == "stdio"
          && value(chat, "--add-dir") == "/Users/u" && !chat.contains("/w"), "tools: 대화는 기본 도구 + 앱이 권한 응답 + 홈 폴더")
    let chatAllowed = value(chat, "--allowedTools") ?? ""
    check(chatAllowed.contains("Bash(ls:*)") && chatAllowed.contains("mcp__pokemate__usage_summary") && !chatAllowed.contains("Bash(x:*)"),
          "tools: 대화 허용은 조회 도구 + 이 대화에서 항상 허용한 것만")
    check(value(d, "--permission-prompts") == "none" && value(plain, "--permission-prompts") == "none" && !d.contains("--permission-prompt-tool"),
          "tools: 알람·조회 전용 실행은 권한 요청을 묻지 않고 거부")
    check((value(plain, "--allowedTools") ?? "").contains("mcp__pokemate__usage_summary"), "tools: 토큰 조회 도구는 늘 켜짐")
    try? FileManager.default.removeItem(at: file)
    let old = #"{"id":"6F9619FF-8B86-D011-B42D-00C04FC964FF","title":"t","prompt":"p","hour":9,"minute":0,"repeatRule":{"daily":{}},"enabled":true}"#
    let decoded = try? JSONDecoder().decode(Alarm.self, from: Data(old.utf8))
    check(decoded != nil && decoded?.canWrite == false, "write: 예전 알림은 쓰기 꺼짐")
    check(AlarmStore.promptFilePath("{{file:~/a/b.md}}", home: "/Users/u") == "/Users/u/a/b.md"
          && AlarmStore.promptFilePath(" {{file:/abs/c.md}} ") == "/abs/c.md"
          && AlarmStore.promptFilePath("그냥 내용") == nil && AlarmStore.promptFilePath("{{file:}}") == nil, "write: 내용을 파일에서 읽기")
}

// MARK: 토큰 집계
do {
    let fm = FileManager.default
    let root = fm.temporaryDirectory.appendingPathComponent("pm-usage-\(UUID().uuidString)")
    let proj = root.appendingPathComponent("projects/p", isDirectory: true)
    try? fm.createDirectory(at: proj, withIntermediateDirectories: true)
    var cal = Calendar(identifier: .gregorian)
    cal.timeZone = TimeZone(identifier: "Asia/Seoul")!
    func line(_ id: String, _ ts: String, model: String = "claude-opus-5-5", input: Int = 10, out: Int = 5) -> String {
        #"{"type":"assistant","timestamp":"\#(ts)","requestId":"r\#(id)","message":{"id":"m\#(id)","model":"\#(model)","usage":{"input_tokens":\#(input),"output_tokens":\#(out),"cache_creation_input_tokens":0,"cache_read_input_tokens":100}}}"#
    }
    let a = proj.appendingPathComponent("a.jsonl")
    let b = proj.appendingPathComponent("b.jsonl")
    try? (line("1", "2026-11-17T01:00:00.000Z") + "\n" + line("1", "2026-11-17T01:00:00.000Z") + "\n"
          + line("s", "2026-11-17T01:00:00.000Z", model: "<synthetic>") + "\n").write(to: a, atomically: true, encoding: .utf8)
    try? (line("1", "2026-11-17T01:00:00.000Z") + "\n" + line("2", "2026-11-16T14:30:00.000Z") + "\n").write(to: b, atomically: true, encoding: .utf8)
    let cacheFile = root.appendingPathComponent("cache.json")
    let now = ISO8601DateFormatter().date(from: "2026-11-17T05:00:00Z")!
    let reader = ClaudeUsageReader(roots: [root.appendingPathComponent("projects")], cacheFile: cacheFile)
    let s1 = reader.refresh(now: now, calendar: cal)
    check(s1.today.total == 115 && s1.month.total == 230, "usage: 같은 메시지는 파일이 달라도 한 번만, synthetic 제외 (KST 날짜)")
    let expectedCost: Double = 160.0 / 1_000_000
    check(abs(s1.todayCost - expectedCost) < 1e-12, "usage: opus-5-5 단가로 비용")
    let h = try? FileHandle(forWritingTo: b)
    _ = try? h?.seekToEnd()
    try? h?.write(contentsOf: Data((line("3", "2026-11-17T02:00:00.000Z", input: 1, out: 1) + "\n" + #"{"type":"assistant","timestamp":"#).utf8))
    try? h?.close()
    let s2 = ClaudeUsageReader(roots: [root.appendingPathComponent("projects")], cacheFile: cacheFile).refresh(now: now, calendar: cal)
    check(s2.today.total == 217, "usage: 캐시 이어 읽기, 끝이 잘린 줄은 다음에")
    check(s2.month.total == 332, "usage: 이번 달 합계")
    check(s2.daily.count == ClaudeUsageReader.dailyDays && s2.daily.last?.day == "2026-11-17" && s2.daily.last?.total == 217
          && s2.daily.dropLast().last?.total == 115 && s2.daily.first?.total == 0, "usage: 최근 30일 일별 (빈 날은 0)")
    try? fm.removeItem(at: root)
}

struct SplitMix: RandomNumberGenerator {
    var state: UInt64
    init(seed: UInt64) { state = seed }
    mutating func next() -> UInt64 {
        state &+= 0x9E3779B97F4A7C15
        var z = state
        z = (z ^ (z >> 30)) &* 0xBF58476D1CE4E5B9
        z = (z ^ (z >> 27)) &* 0x94D049BB133111EB
        return z ^ (z >> 31)
    }
}

// MARK: 포켓몬 도감
do {
    func species(_ id: Int, _ name: String, rate: Int, from: Int? = nil, chain: Int = 1, legendary: Bool = false) -> Data {
        let fromJSON = from.map { #"{"name":"x","url":"https://pokeapi.co/api/v2/pokemon-species/\#($0)/"}"# } ?? "null"
        return Data(#"{"id":\#(id),"name":"en\#(id)","capture_rate":\#(rate),"is_legendary":\#(legendary),"is_mythical":false,"color":{"name":"green"},"names":[{"language":{"name":"en"},"name":"E"},{"language":{"name":"ko"},"name":"\#(name)"}],"flavor_text_entries":[{"language":{"name":"ko"},"flavor_text":"줄\n바꿈\u000c설명"}],"evolves_from_species":\#(fromJSON),"evolution_chain":{"url":"https://pokeapi.co/api/v2/evolution-chain/\#(chain)/"}}"#.utf8)
    }
    let pikachu = PokedexLoader.parseSpecies(species(25, "피카츄", rate: 190, from: 172, chain: 10))
    check(pikachu?.form == PokemonForm(id: 25, name: "피카츄", flavor: "줄 바꿈 설명", color: "green") && pikachu?.evolvesFrom == 172,
          "pokedex: 한국어 이름·설명(줄바꿈 정리)·이전 진화")
    func node(_ id: Int, _ kids: String = "") -> String {
        #"{"species":{"name":"x","url":"https://pokeapi.co/api/v2/pokemon-species/\#(id)/"},"evolves_to":[\#(kids)]}"#
    }
    let eevee = Data(#"{"chain":\#(node(133, [134, 135, 136, 196].map { node($0) }.joined(separator: ",")))}"#.utf8)
    check(PokedexLoader.finals(chain: eevee, base: 133, maxDex: 151) == [134, 135, 136], "pokedex: 갈래 진화는 범위 안 끝 단계 전부")
    let pichu = Data(#"{"chain":\#(node(172, node(25, node(26))))}"#.utf8)
    check(PokedexLoader.finals(chain: pichu, base: 25, maxDex: 151) == [26], "pokedex: 범위 밖 아기 포켓몬 아래에서 기본형 찾기")
    let oddish = Data(#"{"chain":\#(node(43, node(44, node(45) + "," + node(182))))}"#.utf8)
    check(PokedexLoader.finals(chain: oddish, base: 43, maxDex: 151) == [45], "pokedex: 범위 밖 갈래는 뺌")
    check(PokedexLoader.paths(chain: oddish, base: 43, maxDex: 151) == [[43, 44, 45]], "pokedex: 기본형→중간→최종 경로")
    check(PokedexLoader.paths(chain: eevee, base: 133, maxDex: 151) == [[133, 134], [133, 135], [133, 136]], "pokedex: 갈래마다 경로")
    let infos = [species(1, "이상해씨", rate: 45, chain: 1), species(2, "이상해풀", rate: 45, from: 1, chain: 1),
                 species(3, "이상해꽃", rate: 45, from: 2, chain: 1), species(128, "켄타로스", rate: 45, chain: 60)]
        .compactMap(PokedexLoader.parseSpecies)
    let chains = [URL(string: "https://pokeapi.co/api/v2/evolution-chain/1/")!: Data(#"{"chain":\#(node(1, node(2, node(3))))}"#.utf8),
                  URL(string: "https://pokeapi.co/api/v2/evolution-chain/60/")!: Data(#"{"chain":\#(node(128))}"#.utf8)]
    let built = PokedexLoader.build(species: infos, chains: chains, maxDex: 151)
    check(built.lines == [PokemonLine(base: 1, paths: [[1, 2, 3]], captureRate: 45, legendary: false),
                          PokemonLine(base: 128, paths: [[128]], captureRate: 45, legendary: false)]
          && built.forms.count == 4, "pokedex: 기본형마다 한 줄, 진화 없는 포켓몬은 자기 자신")
    check([PokemonLine(base: 1, paths: [[1]], captureRate: 255, legendary: false).rarity,
           PokemonLine(base: 1, paths: [[1]], captureRate: 190, legendary: false).rarity,
           PokemonLine(base: 1, paths: [[1]], captureRate: 45, legendary: false).rarity,
           PokemonLine(base: 1, paths: [[1]], captureRate: 3, legendary: true).rarity] == [.common, .uncommon, .rare, .legendary],
          "pokedex: 포획률로 희귀도")
}

// MARK: 캐릭터 성장
do {
    let t0 = Date(timeIntervalSince1970: 1_800_000_000)
    var empty = MateState()
    check(empty.advance(allTime: 5_000_000_000, now: t0).isEmpty && empty.current == nil && empty.lastAllTime == nil,
          "mate: 도감을 아직 못 받았으면 기다림 (토큰 기록도 안 넘김)")
    func form(_ id: Int, _ name: String) -> PokemonForm { PokemonForm(id: id, name: name, flavor: "", color: "green") }
    var forms: [PokemonForm] = []
    var lines: [PokemonLine] = []
    for (i, rate) in [255, 255, 120, 120, 45, 45].enumerated() {
        let base = i * 10 + 1
        forms += [form(base, "기본\(base)"), form(base + 1, "중간\(base)"), form(base + 2, "최종\(base)"), form(base + 3, "갈래\(base)")]
        lines.append(PokemonLine(base: base, paths: [[base, base + 1, base + 2], [base, base + 3]], captureRate: rate, legendary: false))
    }
    forms.append(form(99, "전설"))
    lines.append(PokemonLine(base: 99, paths: [[99]], captureRate: 3, legendary: true))
    MateSpecies.install(PokedexCatalog(maxDex: 151, forms: forms, lines: lines))
    let first = MateSpecies.all[0].id
    var m = MateState()
    check(m.advance(allTime: 5_000_000_000, now: t0) == [.started(first)] && m.tokens == 0, "mate: 처음엔 지금까지 쓴 토큰과 상관없이 1번 알")
    check([3, 4].contains(m.current?.form ?? 0), "mate: 시작할 때 진화 갈래를 정해 둠")
    m.current?.form = 3
    check(m.advance(allTime: 5_000_000_000 + 29_999_999, now: t0).isEmpty && MateSpecies.all[0].form(tokens: m.tokens, final: 3) == nil, "mate: 3천만 전은 알")
    check(m.advance(allTime: 5_030_000_000, now: t0) == [.hatched(first)], "mate: 3천만에 부화")
    check(MateSpecies.all[0].form(tokens: m.tokens, final: 3)?.name == "기본1", "mate: 부화하면 기본형부터")
    check(m.advance(allTime: 4_000_000_000, now: t0).isEmpty && m.tokens == 30_000_000, "mate: 기록이 지워져 합계가 줄어도 뒤로 안 감")
    check(m.advance(allTime: 4_000_000_000 + 270_000_000, now: t0) == [.evolved(first, 2)], "mate: 3억에 중간 진화")
    check(m.advance(allTime: 4_000_000_000 + 570_000_000, now: t0) == [.evolved(first, 3)], "mate: 6억에 최종 진화")
    let later = t0.addingTimeInterval(86_400 * 2)
    var jump = MateState()
    _ = jump.advance(allTime: 0, now: t0)
    jump.current?.form = 3
    let ev = jump.advance(allTime: 1_130_000_000, now: later)
    let next = jump.current?.species ?? ""
    check(ev == [.hatched(first), .evolved(first, 2), .evolved(first, 3), .completed(first), .started(next), .hatched(next)]
          && MateSpecies.find(next) != nil, "mate: 한 번에 넘어도 부화·진화 차례로·등록·다음 알 순서 (단계 안 건너뜀)")
    check(jump.tokens == 130_000_000 && jump.collected.first?.days == 3 && jump.lastForm(of: first) == 3, "mate: 넘친 토큰은 다음 알로, 함께한 날 수, 진화 모습 기록")
    check(jump.seenDex() == [1, 2, 3, MateSpecies.find(next)!.base.id], "dex: 등록한 친구는 지나온 단계 모두, 키우는 친구는 지금 단계까지 만남")
    var raising = MateState(current: CurrentMate(species: first, tokens: 400_000_000, startedAt: t0, form: 3),
                            party: [CurrentMate(species: first, tokens: 10_000_000, startedAt: t0, form: 4)])
    check(raising.seenDex() == [1, 2], "dex: 1진화 중이면 기본형·중간형만, 알은 아직 안 만남")
    raising.collected = [CollectedMate(species: first, completedAt: t0, days: 1, form: 4)]
    check(raising.seenDex() == [1, 2, 4] && raising.completedCount(form: 4) == 1 && raising.completedCount(form: 3) == 0,
          "dex: 갈래 진화로 등록하면 그 갈래 최종형이 만남·등록 횟수")
    let line1 = MateSpecies.find(first)!
    var pick = MateState(current: CurrentMate(species: first, tokens: 400_000_000, startedAt: t0, form: 3))
    check(pick.show(first, form: 1) && pick.current?.shown == 1 && line1.shownForm(tokens: 400_000_000, final: 3, shown: 1)?.name == "기본1"
          && pick.tokens == 400_000_000, "dex: 지나온 단계를 대표 모습으로 (성장은 그대로)")
    check(!pick.show(first, form: 3) && pick.current?.shown == 1, "dex: 아직 안 된 단계는 고를 수 없음")
    check(line1.shownForm(tokens: 700_000_000, final: 3, shown: 1)?.name == "기본1" && line1.shownForm(tokens: 700_000_000, final: 3, shown: nil)?.name == "최종1",
          "dex: 진화해도 고른 모습 유지, 안 고르면 지금 모습")
    check(pick.show(first, form: 2) && pick.current?.shown == nil, "dex: 지금 단계를 고르면 원래 모습으로")
    var partyPick = MateState(current: CurrentMate(species: "x", tokens: 0, startedAt: t0),
                              party: [CurrentMate(species: first, tokens: 400_000_000, startedAt: t0, form: 3)])
    check(partyPick.show(first, form: 1) && partyPick.current?.species == first && partyPick.current?.shown == 1 && partyPick.party.first?.species == "x",
          "dex: 파티 포켓몬의 지나온 단계를 고르면 그 계열이 대표가 되고 모습만 바뀜")
    let dexEntries = MateDex.entries(MateSpecies.all, maxDex: 151)
    check(dexEntries.count == 25 && dexEntries.first?.form.id == 1 && dexEntries.first?.species.id == first
          && Set(dexEntries.map(\.id)).count == dexEntries.count, "dex: 도감 칸은 포켓몬 한 마리마다 (계열 6개·4마리씩 + 전설 1)")
    check(MateDex.entries(MateSpecies.all, maxDex: 40).count == 16, "dex: 범위 밖 번호는 칸에서 뺌")
    let sp = MateSpecies.find(first)!
    check(sp.form(tokens: 400_000_000, final: 4)?.name == "갈래1" && sp.form(tokens: 700_000_000, final: 4)?.name == "갈래1"
          && sp.form(tokens: 400_000_000, final: 3)?.name == "중간1", "mate: 2단계 갈래는 3억에 최종, 3단계는 중간을 거침")
    let legend = MateSpecies.find("99")!
    check(legend.form(tokens: 900_000_000, final: nil)?.name == "전설" && MateRules.goalLabel(for: 900_000_000, count: 1) == "도감 등록",
          "mate: 진화 없는 포켓몬은 그대로")
    var counts: [MateRarity: Int] = [:]
    var lcg = SplitMix(seed: 42)
    let picker = MateState(collected: [CollectedMate(species: first, completedAt: t0, days: 1)])
    for _ in 0..<10_000 { counts[MateSpecies.find(picker.nextSpecies(using: &lcg))!.rarity, default: 0] += 1 }
    check((counts[.common] ?? 0) > 6_500 && (counts[.common] ?? 0) < 7_500 && (counts[.uncommon] ?? 0) > 1_700 && (counts[.uncommon] ?? 0) < 2_300
          && (counts[.rare] ?? 0) > 600 && (counts[.rare] ?? 0) < 1_000 && (counts[.legendary] ?? 0) > 100 && (counts[.legendary] ?? 0) < 300,
          "mate: 다음 알은 희귀도 가중 무작위 (일반≈70%, 고급≈20%, 희귀≈8%, 전설≈2%)")
    check(abs(MateRules.stageProgress(for: 165_000_000, count: 3) - 0.5) < 1e-9, "mate: 단계 안 진행률")
    var old = MateState(current: CurrentMate(species: "mochi", tokens: 51_756_346, startedAt: t0), lastAllTime: 10)
    check(old.adoptUnknown() && MateSpecies.find(old.current!.species) != nil && old.tokens == 51_756_346
          && old.current?.startedAt == t0 && !old.adoptUnknown(), "mate: 옛 캐릭터는 토큰 그대로 포켓몬으로 바꿈 (한 번만)")
    var party = MateState(current: CurrentMate(species: "1", tokens: 50_000_000, startedAt: t0),
                          party: [CurrentMate(species: "21", tokens: 30_000_000, startedAt: t0)], lastAllTime: 100)
    check(party.makeRepresentative("21") && party.current?.species == "21" && party.party.map(\.species) == ["1"]
          && party.party.first?.tokens == 50_000_000, "mate: 대표 바꾸면 이전 대표는 토큰 그대로 쉬기")
    check(!party.makeRepresentative("21") && !party.makeRepresentative("41"), "mate: 이미 대표거나 키우지 않는 포켓몬은 안 바뀜")
    _ = party.advance(allTime: 1_100, now: t0)
    check(party.current?.tokens == 30_001_000 && party.party.first?.tokens == 50_000_000, "mate: 토큰은 대표만 먹음")
    let decoded = try? JSONDecoder().decode(MateState.self, from: Data(#"{"collected":[],"lastAllTime":5}"#.utf8))
    check(decoded?.party == [] && decoded?.lastAllTime == 5, "mate: party 없는 옛 파일도 읽음")
    check([nil, 0, 1, 2].map(MateRules.label(step:)) == ["알", "기본", "1진화", "2진화"], "mate: 단계 표기는 공식 용어")
    func line(_ base: Int, _ paths: [[Int]]) -> PokemonLine { PokemonLine(base: base, paths: paths, captureRate: 45, legendary: false) }
    let gen1 = PokedexCatalog(maxDex: 151, forms: [form(25, "피카츄"), form(26, "라이츄"), form(111, "뿔카노"), form(112, "코뿌리")],
                              lines: [line(25, [[25, 26]]), line(111, [[111, 112]])])
    let whole = PokedexCatalog(maxDex: 1025, forms: [form(172, "피츄"), form(25, "피카츄"), form(26, "라이츄"), form(111, "뿔카노"),
                                                     form(112, "코뿌리"), form(464, "거대코뿌리")],
                               lines: [line(172, [[172, 25, 26]]), line(111, [[111, 112, 464]])])
    MateSpecies.install(whole, lookup: [gen1])
    check(MateSpecies.all.map(\.id) == ["111", "172"] && MateSpecies.find("25")?.name == "피카츄", "range: 고르는 범위는 전체, 1세대에만 있는 줄도 찾음")
    check(MateSpecies.find("111")?.path(112).map(\.id) == [111, 112] && MateSpecies.find("111")?.path(464).map(\.id) == [111, 112, 464],
          "range: 정해 둔 최종 모습까지만 진화 (1세대에서 정한 코뿌리는 거대코뿌리로 안 늘어남)")
    check(MateSpecies.find("111")?.form(tokens: 900_000_000, final: 112)?.name == "코뿌리", "range: 전체로 바꿔도 뿔카노는 코뿌리에서 멈춤")
    MateSpecies.install(gen1, lookup: [whole])
    check(MateSpecies.all.map(\.id) == ["25", "111"] && MateSpecies.find("172")?.name == "피츄", "range: 1세대로 좁혀도 범위 밖 포켓몬을 찾음")
    check((0..<50).allSatisfy { _ in MateState.newMate("111", now: t0).form == 112 }, "range: 새 알의 진화 경로는 고른 범위에서")
    var outside = MateState(current: CurrentMate(species: "172", tokens: 40_000_000, startedAt: t0, form: 26), lastAllTime: 1)
    check(!outside.adoptUnknown() && outside.current?.species == "172", "range: 범위 밖 포켓몬을 다른 포켓몬으로 바꾸지 않음")
    var unknownNumber = MateState(current: CurrentMate(species: "999", tokens: 5, startedAt: t0), lastAllTime: 1)
    check(!unknownNumber.adoptUnknown(), "range: 번호로 된 포켓몬은 도감에 없어도 그대로 둠")
    check(MateSpecies.eggGrid.count == 14 && MateSpecies.eggGrid.allSatisfy { $0.count == 14 }, "mate: 알 그림 14x14")
}

// MARK: Claude Code 도구 승인
do {
    let line = #"{"type":"control_request","request_id":"r1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"ls ~/Downloads","description":"목록"},"permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"ls:*"}],"behavior":"allow","destination":"localSettings"},{"type":"setMode","mode":"acceptEdits","destination":"session"}]}}"#
    let request = ClaudeStream.object(line).flatMap(ClaudeStream.permissionRequest)
    check(request?.tool == "Bash" && request?.summary == "ls ~/Downloads" && request?.rules == ["Bash(ls:*)"], "approve: 권한 요청 읽기 (규칙 제안만)")
    if let request {
        let deny = ClaudeStream.object(ClaudeStream.response(to: request, decision: .deny("거부")))
        let denyBody = ((deny?["response"] as? [String: Any])?["response"] as? [String: Any])
        check(denyBody?["behavior"] as? String == "deny" && (deny?["response"] as? [String: Any])?["request_id"] as? String == "r1", "approve: 거부 응답")
        let always = ClaudeStream.object(ClaudeStream.response(to: request, decision: .allowAlways))
        let body = ((always?["response"] as? [String: Any])?["response"] as? [String: Any])
        let perms = body?["updatedPermissions"] as? [[String: Any]]
        check(body?["behavior"] as? String == "allow" && (body?["updatedInput"] as? [String: Any])?["command"] as? String == "ls ~/Downloads"
              && perms?.count == 1 && perms?.first?["destination"] as? String == "session", "approve: 항상 허용은 이 실행(session) 규칙으로만")
        let once = ClaudeStream.object(ClaudeStream.response(to: request, decision: .allow))
        check(((once?["response"] as? [String: Any])?["response"] as? [String: Any])?["updatedPermissions"] == nil, "approve: 한 번 허용은 규칙 안 남김")
    }
    check(ClaudeStream.permissionRequest(["type": "control_request", "request_id": "x", "request": ["subtype": "initialize"]]) == nil, "approve: 다른 제어 요청은 무시")
    check(ClaudeStream.activity(tool: "Bash", input: ["command": "ls ~/Downloads"]) == "Bash 실행 중: ls ~/Downloads"
          && ClaudeStream.activity(tool: "mcp__pokemate__usage_summary", input: [:]) == "usage_summary 조회 중", "approve: 진행 한 줄")
    let dir = FileManager.default.temporaryDirectory.appendingPathComponent("pm-perm-\(UUID().uuidString)")
    let permFile = dir.appendingPathComponent(".claude/settings.json")
    check(ClaudeCommand.installPermissions(at: permFile, dataDir: URL(fileURLWithPath: "/d")) && !ClaudeCommand.installPermissions(at: permFile, dataDir: URL(fileURLWithPath: "/d")),
          "approve: 권한 파일은 없을 때만 만듦")
    let perm = (try? Data(contentsOf: permFile)).flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
    let deny = (perm?["permissions"] as? [String: Any])?["deny"] as? [String] ?? []
    check(deny.contains("Bash(sudo:*)") && deny.contains("Edit(./.claude/**)") && deny.contains("Write(//d/write-tools.json)"), "approve: 기본 deny 에 sudo·권한 파일 보호")
    try? FileManager.default.removeItem(at: dir)
    var gate = PermissionAlertGate()
    let t = Date(timeIntervalSince1970: 1_800_000_000)
    check(gate.shouldAlert(now: t) && !gate.shouldAlert(now: t.addingTimeInterval(3)) && gate.shouldAlert(now: t.addingTimeInterval(9)),
          "approve: 카드가 연달아 뜨면 5초 안에는 알림 하나")
    if let request {
        check(PermissionAlertGate.body(for: request) == "Bash: ls ~/Downloads", "approve: 알림 본문은 도구와 명령 한 줄")
        check(PermissionAlertGate.body(for: request, limit: 6) == "Bash: …", "approve: 긴 본문은 자름")
    }
    check(AppSettings.default.claudeTools == false && (try? JSONDecoder().decode(AppSettings.self, from: Data(#"{"claudeTools":true}"#.utf8)))?.claudeTools == true,
          "approve: 도구 쓰기는 기본 꺼짐, 저장값 읽음")
}

// MARK: 알람 실행 기록
do {
    var cal = Calendar(identifier: .gregorian)
    cal.timeZone = TimeZone(identifier: "Asia/Seoul")!
    check([AlarmRunLog.short("끝났어요. 결과는 알림 창과 채팅에 있어요", failed: false), AlarmRunLog.short("x", failed: true),
           AlarmRunLog.short("새로 생긴 것 없어요", failed: false), AlarmRunLog.short("새로 3건, 알림 창에 보냈어요", failed: false),
           AlarmRunLog.short("처음이라 지금 있는 5건을 기준으로 잡았어요", failed: false), AlarmRunLog.short("알림 창에 보냈어요", failed: false)]
          == ["성공", "실패", "새 건 없음", "새 건 3", "기준 잡음", "알림 보냄"], "alarmlog: 결과 문구 짧게")
    let id = UUID(), other = UUID()
    let t = ISO8601DateFormatter().date(from: "2026-11-17T01:00:00Z")!
    var log = AlarmRunLog.record([:], id: id, message: "끝났어요", failed: false, now: t, calendar: cal)
    log = AlarmRunLog.record(log, id: id, message: "새로 생긴 것 없어요", failed: false, now: t.addingTimeInterval(60), calendar: cal)
    log = AlarmRunLog.record(log, id: other, message: "x", failed: true, now: t, calendar: cal)
    check(log[id]?.todayCount == 1 && log[id]?.result == "새 건 없음" && AlarmRunLog.todayTotal(log, ids: [id, other], now: t, calendar: cal) == 2,
          "alarmlog: 오늘 횟수는 실제로 한 일만, 마지막 결과는 늘 갱신")
    log = AlarmRunLog.record(log, id: id, message: "끝났어요", failed: false, now: t.addingTimeInterval(86_400), calendar: cal)
    check(log[id]?.todayCount == 1 && log[id]?.day == "2026-11-18", "alarmlog: 날이 바뀌면 오늘 횟수 새로")
    let file = FileManager.default.temporaryDirectory.appendingPathComponent("pm-runs-\(UUID().uuidString).json")
    AlarmRunLog.save(log, to: file)
    check(AlarmRunLog.load(from: file) == log, "alarmlog: 저장하고 다시 읽음")
    try? FileManager.default.removeItem(at: file)
}

// MARK: 명령 실행 알람
do {
    let old = #"[{"id":"6F1D6F5E-0000-0000-0000-000000000009","title":"t","prompt":"p","hour":9,"minute":0,"repeatRule":{"daily":{}},"enabled":true}]"#
    check((try? JSONDecoder().decode([Alarm].self, from: Data(old.utf8)))?.first?.shellCommand == nil, "command: 예전 알람 파일은 명령 없음")
    var alarm = Alarm(title: "근무", prompt: "", hour: 10, minute: 0, repeatRule: .weekdays)
    alarm.command = "  python3 ~/x.py check  "
    let round = (try? JSONEncoder().encode([alarm])).flatMap { try? JSONDecoder().decode([Alarm].self, from: $0) }?.first
    check(round?.shellCommand == "python3 ~/x.py check", "command: 저장하고 다시 읽음")
    alarm.trigger = Trigger(system: "jira", event: "jira.assigned_to_me")
    check(alarm.shellCommand == nil, "command: 트리거 알람에서는 명령 실행 안 함")
    let proposed = AlarmProposal.extract(from: "```pokemate-alarm\n[{\"title\":\"x\",\"hour\":9,\"repeat\":\"daily\",\"command\":\"rm -rf ~\",\"prompt\":\"\"}]\n```").alarms
    check(proposed.count == 1 && proposed.first?.command == nil && proposed.first?.shellCommand == nil, "command: 대화 제안 블록으로는 명령 알람을 못 만듦")
    check(ShellCommand.tail("a\nb\n\nc\nd\ne\nf\n", lines: 3) == "d\ne\nf", "command: 출력 마지막 줄만")
    let ok = await ShellCommand.run("echo hello; echo world", home: NSTemporaryDirectory())
    check(ok.ok && ok.tail == "hello\nworld", "command: 성공은 종료 코드 0, 출력 마지막 줄")
    let bad = await ShellCommand.run("echo oops >&2; exit 3", home: NSTemporaryDirectory())
    check(!bad.ok && bad.exitCode == 3 && bad.tail.contains("oops"), "command: 실패는 종료 코드와 오류 출력")
    let slow = await ShellCommand.run("sleep 5", timeout: 1, home: NSTemporaryDirectory())
    check(!slow.ok && slow.timedOut, "command: 제한 시간 넘기면 멈춤")
    let log = AlarmRunLog.record([:], id: UUID(), message: "종료 코드 3", failed: true, now: Date(), output: "oops")
    check(log.values.first?.output == "oops" && log.values.first?.result == "실패", "command: 실행 기록에 출력 남김")
    check(ClaudeCommand.defaultDeny(dataDir: URL(fileURLWithPath: "/d")).contains("Edit(//d/alarms.json)"), "command: 메이트 도구로 알람 파일 수정 금지 (기본 deny)")
}

// MARK: prompts
do {
    check(AlarmStore.promptName("  {{prompt:jira}} ") == "jira" && AlarmStore.promptName("{{file:~/a.md}}") == nil && AlarmStore.promptName("{{prompt:}}") == nil,
          "prompts: {{prompt:이름}} 읽기")
    let dir = FileManager.default.temporaryDirectory.appendingPathComponent("pm-prompts-\(UUID().uuidString)")
    try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    try? "# 지라\n할 일".write(to: dir.appendingPathComponent("jira.md"), atomically: true, encoding: .utf8)
    try? "x".write(to: dir.appendingPathComponent("notes.txt"), atomically: true, encoding: .utf8)
    check(Rules.promptNames(in: dir) == ["jira"], "prompts: 목록은 md 파일 이름")
    let a = Alarm(title: "t", prompt: "{{prompt:jira}}", hour: 9, minute: 0, repeatRule: .daily)
    check(AlarmStore.resolvePrompt(a, settings: .default, promptsDir: dir) == "# 지라\n할 일", "prompts: 알람 내용에서 프롬프트 파일을 읽음")
    let missing = Alarm(title: "t", prompt: "{{prompt:none}}", hour: 9, minute: 0, repeatRule: .daily)
    check(AlarmStore.resolvePrompt(missing, settings: .default, promptsDir: dir) == nil, "prompts: 없는 프롬프트는 읽지 못함(실패로 알림)")
    check(Rules.File(url: dir.appendingPathComponent("prompts/x.md")).kind == .prompt
          && Rules.File(url: URL(fileURLWithPath: "/d/prompts/x.md")).name == "prompts/x.md", "prompts: 폴더 종류")
    try? FileManager.default.removeItem(at: dir)
}

// MARK: 옛 이름 호환
do {
    let legacy = "```" + ChatBlock.legacyTag + "-alarm\n[{\"title\":\"a\",\"hour\":9,\"repeat\":\"daily\"}]\n```"
    let fresh = "```" + ChatBlock.tag + "-alarm\n[{\"title\":\"b\",\"hour\":9,\"repeat\":\"daily\"}]\n```"
    check(AlarmProposal.extract(from: "안내\n" + legacy).alarms.first?.title == "a" && AlarmProposal.extract(from: legacy).text.isEmpty
          && AlarmProposal.extract(from: fresh).alarms.first?.title == "b", "legacy: 옛 블록 태그도 읽음")
}

// MARK: 알람 순서
do {
    func al(_ t: String, _ g: String?) -> Alarm { Alarm(title: t, prompt: "", hour: 9, minute: 0, repeatRule: .daily, group: g) }
    let a = al("a", "G"), b = al("b", "G"), c = al("c", nil), d = al("d", "G"), e = al("e", "없는그룹")
    let groups = ["G"]
    let list = [a, b, c, d, e]
    let titles: ([Alarm]) -> [String] = { $0.map(\.title) }
    check(titles(AlarmOrder.move(list, id: d.id, to: "G", before: a.id, groups: groups)) == ["d", "a", "b", "c", "e"], "order: 같은 그룹 안에서 끌어 놓은 자리로")
    let moved = AlarmOrder.move(list, id: c.id, to: "G", before: b.id, groups: groups)
    check(titles(moved) == ["a", "c", "b", "d", "e"] && moved[1].group == "G", "order: 다른 그룹 칸 사이에 놓으면 그 그룹 그 자리로")
    check(titles(AlarmOrder.move(list, id: a.id, to: nil, before: nil, groups: groups)) == ["b", "c", "d", "e", "a"] , "order: 그룹 머리줄에 놓으면 그 그룹 맨 끝으로")
    check(titles(AlarmOrder.move(list, id: c.id, to: "G", before: nil, groups: groups)) == ["a", "b", "d", "c", "e"], "order: 그룹 끝은 그 그룹 마지막 알람 다음")
    check(AlarmOrder.move(list, id: a.id, to: "G", before: a.id, groups: groups) == list, "order: 제자리에 놓으면 그대로")
    check(AlarmOrder.neighbor(list, id: a.id, up: true, groups: groups) == nil && AlarmOrder.neighbor(list, id: d.id, up: false, groups: groups) == nil
          && AlarmOrder.neighbor(list, id: c.id, up: false, groups: groups) == e.id, "order: 그룹 맨 위·맨 아래는 더 못 옮김 (없는 그룹은 그룹 없음)")
    let up = AlarmOrder.shift(list, id: d.id, up: true, groups: groups)
    check(titles(up) == ["a", "d", "c", "b", "e"] && up.map(\.enabled) == list.map(\.enabled), "order: 위로 옮기기는 같은 그룹 앞 알람과 자리 바꿈, 켜짐 그대로")
    let encoded = try? JSONEncoder().encode(up)
    check(encoded.flatMap { try? JSONDecoder().decode([Alarm].self, from: $0) }.map(titles) == ["a", "d", "c", "b", "e"], "order: 순서는 파일 배열 순서로 저장")
    let old = #"[{"id":"6F1D6F5E-0000-0000-0000-00000000000A","title":"2","prompt":"","hour":9,"minute":0,"repeatRule":{"daily":{}},"enabled":true},{"id":"6F1D6F5E-0000-0000-0000-00000000000B","title":"1","prompt":"","hour":8,"minute":0,"repeatRule":{"daily":{}},"enabled":true}]"#
    check((try? JSONDecoder().decode([Alarm].self, from: Data(old.utf8))).map(titles) == ["2", "1"], "order: 예전 파일은 지금 순서 그대로")
    check(Alarm.displayCommand("uv --directory /Users/u/Workspace/poke-mate-ai-agent/mcp-server run --quiet pokemate auto worklog-check", home: "/Users/u")
          == "pokemate auto worklog-check", "command: uv 앞부분은 빼고 실제 명령만")
    check(Alarm.displayCommand("/opt/homebrew/bin/uv --directory \"/a b/c\" run tool x", home: "/Users/u") == "tool x", "command: uv 경로·따옴표 경로도")
    check(Alarm.displayCommand("python3 /Users/u/scripts/check.py --all", home: "/Users/u") == "python3 ~/scripts/check.py --all", "command: 다른 명령은 홈을 ~ 로")
    check(Alarm.displayCommand(String(repeating: "x", count: 50), home: "/Users/u") == String(repeating: "x", count: 40) + "…", "command: 그래도 길면 자름")
}

// MARK: 알람별 제한 시간
do {
    var alarm = Alarm(title: "j", prompt: "", hour: 0, minute: 0, repeatRule: .daily, trigger: Trigger(system: "jira", event: "jira.assigned_to_me"))
    check(alarm.automationTimeout == ClaudeRunner.automationTimeout, "timeout: 없으면 기본 30분")
    alarm.timeoutMinutes = 90
    let round = (try? JSONEncoder().encode([alarm])).flatMap { try? JSONDecoder().decode([Alarm].self, from: $0) }?.first
    check(round?.automationTimeout == 5400, "timeout: 알람별 90분 저장·읽기")
}

// MARK: 작업 세션
do {
    let line = #"{"type":"result","is_error":false,"result":"ok","session_id":"s1","total_cost_usd":0.25,"usage":{"input_tokens":10,"output_tokens":20,"cache_read_input_tokens":300,"cache_creation_input_tokens":40},"modelUsage":{"m":{"contextWindow":1000}}}"#
    let usage = RunUsage.parse(stdout: Data(("{\"type\":\"system\"}\n" + line + "\n").utf8))
    check(usage == RunUsage(input: 10, output: 20, cacheRead: 300, cacheWrite: 40, costUSD: 0.25, contextWindow: 1000) && usage?.context == 350 && usage?.total == 370,
          "session: 결과 줄에서 토큰·비용·문맥 창 읽기")
    check(RunUsage.parse(stdout: Data("not json".utf8)) == nil, "session: 결과 줄 없으면 nil")
    let t0 = Date(timeIntervalSince1970: 1_800_000_000)
    var book = WorkSessionBook()
    check(WorkSessionBook.repoKey(nil) == "운영" && WorkSessionBook.repoKey(" r ") == "r", "session: 레포 없으면 운영 세션")
    book.record(repo: "r", sessionId: "s1", key: "K-1", usage: usage, now: t0)
    book.record(repo: "r", sessionId: "s1", key: "K-2", usage: RunUsage(input: 1, output: 1, cacheRead: 500, cacheWrite: 0, costUSD: 0.1, contextWindow: 1000), now: t0.addingTimeInterval(60))
    check(book.sessions["r"]?.keys == ["K-1", "K-2"] && book.sessions["r"]?.usage.total == 872 && book.sessions["r"]?.contextPercent == 50,
          "session: 같은 세션에 티켓·토큰 누적, 문맥 % 는 마지막 실행 기준")
    var alerts = WorkSessionBook()
    alerts.record(repo: "a", sessionId: "x", key: "K", usage: RunUsage(input: 0, output: 0, cacheRead: 650, cacheWrite: 0, costUSD: 0, contextWindow: 1000), now: t0)
    check(alerts.contextAlert(repo: "a", threshold: 70) == nil, "alert: 문턱 아래면 알림 없음")
    alerts.record(repo: "a", sessionId: "x", key: "K2", usage: RunUsage(input: 0, output: 0, cacheRead: 720, cacheWrite: 0, costUSD: 0, contextWindow: 1000), now: t0)
    check(alerts.contextAlert(repo: "a", threshold: 70)?.contextPercent == 72, "alert: 처음 넘으면 1번 알림")
    alerts.record(repo: "a", sessionId: "x", key: "K3", usage: RunUsage(input: 0, output: 0, cacheRead: 800, cacheWrite: 0, costUSD: 0, contextWindow: 1000), now: t0)
    check(alerts.contextAlert(repo: "a", threshold: 70) == nil && alerts.sessions["a"]?.contextAlerted == true, "alert: 넘은 채로는 다시 안 보냄 (sessions.json 에 기록)")
    alerts.record(repo: "a", sessionId: "x", key: nil, usage: RunUsage(input: 0, output: 0, cacheRead: 300, cacheWrite: 0, costUSD: 0, contextWindow: 1000), now: t0)
    check(alerts.contextAlert(repo: "a", threshold: 70) == nil && alerts.sessions["a"]?.contextAlerted == nil, "alert: 다시 내려가면 기록을 풂")
    alerts.record(repo: "a", sessionId: "x", key: nil, usage: RunUsage(input: 0, output: 0, cacheRead: 900, cacheWrite: 0, costUSD: 0, contextWindow: 1000), now: t0)
    check(alerts.contextAlert(repo: "a", threshold: 70) != nil, "alert: 내려갔다 다시 넘으면 다시 알림")
    alerts.record(repo: "a", sessionId: "y", key: nil, usage: RunUsage(input: 0, output: 0, cacheRead: 100, cacheWrite: 0, costUSD: 0, contextWindow: 1000), now: t0)
    check(alerts.sessions["a"]?.contextAlerted == nil && alerts.contextAlert(repo: "a", threshold: 0) == nil, "alert: 새 세션은 기록 없음, 끄기(0)면 알림 없음")
    let oldSettings = try? JSONDecoder().decode(AppSettings.self, from: Data(#"{"workSessionContextPercent":60,"workSessionDays":7}"#.utf8))
    check(oldSettings?.workSessionAlertPercent == 70, "alert: 예전 교체 설정은 무시, 기본 70%")
    book.replace(repo: "r", with: "s2", summaryFrom: "s1", now: t0)
    check(book.sessions["r"]?.id == "s2" && book.sessions["r"]?.summaryFrom == "s1" && book.archived.map(\.id) == ["s1"], "session: 교체하면 옛 세션 보관")
    book.record(repo: "r", sessionId: "s3", key: "K-3", usage: nil, now: t0)
    check(book.sessions["r"]?.id == "s3" && book.archived.map(\.id) == ["s1", "s2"], "session: 다른 id 로 기록되면 새 세션으로 보고 보관")
    let file = FileManager.default.temporaryDirectory.appendingPathComponent("pm-sessions-\(UUID().uuidString).json")
    book.save(to: file)
    check(WorkSessionBook.load(from: file) == book, "session: sessions.json 저장·읽기")
    try? #"{"sessions":{}}"#.write(to: file, atomically: true, encoding: .utf8)
    check(WorkSessionBook.load(from: file).archived.isEmpty, "session: archived 없는 예전 파일도 읽음")
    try? FileManager.default.removeItem(at: file)
    check(WorkRoute.extract(from: "레포 정함\n```pokemate-route\n{\"repo\":\"price-service\"}\n```") == .some("price-service")
          && WorkRoute.extract(from: "```pokemate-route\n{\"repo\":\"\"}\n```") == .some("운영")
          && WorkRoute.extract(from: "```pokemate-route\n{\"skip\":true}\n```") == .some(nil)
          && WorkRoute.extract(from: "없음") == nil, "session: 레포 정하기 결과 읽기 (없으면 운영, skip 이면 처리 안 함)")
    let jsonl = [
        #"{"type":"user","timestamp":"2026-11-17T08:07:24.000Z","message":{"role":"user","content":"이번 건: K-1"}}"#,
        #"{"type":"assistant","timestamp":"2026-11-17T08:07:30.000Z","message":{"content":[{"type":"text","text":"계획"},{"type":"tool_use","name":"Bash","input":{"command":"ls"}}]}}"#,
        #"{"type":"user","timestamp":"2026-11-17T08:07:31.000Z","message":{"content":[{"type":"tool_result","content":"a.txt","is_error":false}]}}"#,
        #"{"type":"summary","summary":"x"}"#,
    ].joined(separator: "\n")
    let entries = SessionTranscript.parse(Data(jsonl.utf8))
    check(entries.map(\.kind) == [.user, .assistant, .tool, .result] && entries[2].text == "Bash 실행 중: ls" && entries[0].time == "2026-11-17 08:07:24",
          "session: 대화 기록을 사용자·답·도구·결과 줄로")
    let usageLines = [
        #"{"type":"assistant","message":{"id":"m1","model":"claude-opus-5-5","usage":{"input_tokens":10,"output_tokens":5,"cache_read_input_tokens":100,"cache_creation_input_tokens":20}}}"#,
        #"{"type":"assistant","message":{"id":"m1","model":"claude-opus-5-5","usage":{"input_tokens":10,"output_tokens":5,"cache_read_input_tokens":100,"cache_creation_input_tokens":20}}}"#,
        #"{"type":"user","message":{"content":"x"}}"#,
        #"{"type":"assistant","message":{"id":"m2","model":"claude-opus-5-5","usage":{"input_tokens":1,"output_tokens":2,"cache_read_input_tokens":300,"cache_creation_input_tokens":0}}}"#,
    ].joined(separator: "\n")
    let fromLog = TranscriptUsage.total(Data(usageLines.utf8))
    check(fromLog.usage.total == 438 && fromLog.usage.output == 7 && fromLog.lastContext == 301 && fromLog.usage.costUSD > 0,
          "session: 기록에서 사용량 다시 계산 (같은 메시지 id 는 한 번만, 비용은 단가표)")
    var refreshed = WorkSessionBook()
    refreshed.record(repo: "p", sessionId: "s", key: "K", usage: RunUsage(input: 999, output: 999, cacheRead: 0, cacheWrite: 0, costUSD: 9, contextWindow: 1000), now: t0)
    refreshed.applyTranscript(repo: "p", usage: fromLog.usage, lastContext: fromLog.lastContext)
    check(refreshed.sessions["p"]?.usage.total == 438 && refreshed.sessions["p"]?.contextPercent == 30, "session: 기록 기준으로 덮어써서 두 번 세지 않음 (문맥 창은 유지)")
    let queue = SerialQueue()
    final class Log: @unchecked Sendable { var items: [String] = []; let lock = NSLock(); func add(_ s: String) { lock.lock(); items.append(s); lock.unlock() } }
    let log = Log()
    let a1 = queue.run("r") { log.add("a-start"); try? await Task.sleep(nanoseconds: 50_000_000); log.add("a-end") }
    let b1 = queue.run("r") { log.add("b") }
    let c1 = queue.run("other") { log.add("c") }
    await a1.value; await b1.value; await c1.value
    check(log.items.firstIndex(of: "b")! > log.items.firstIndex(of: "a-end")!, "session: 같은 세션 두 건은 차례로 (큐)")
}

// MARK: 미리 알림 창 위치
do {
    let visible = CGRect(x: 0, y: 80, width: 1440, height: 795)
    check(PanelPlacement.safeFrame(visible: visible, dockEdge: .bottom, dockHidden: false, dockSize: 64) == visible, "panel: Dock 이 보이면 visibleFrame 그대로")
    check(PanelPlacement.safeFrame(visible: CGRect(x: 0, y: 4, width: 1440, height: 871), dockEdge: .bottom, dockHidden: true, dockSize: 80)
          == CGRect(x: 0, y: 84, width: 1440, height: 791), "panel: 자동 숨김 Dock(아래)은 그 크기만큼 비킴")
    check(PanelPlacement.safeFrame(visible: CGRect(x: 4, y: 0, width: 1436, height: 875), dockEdge: .left, dockHidden: true, dockSize: 70).minX == 74,
          "panel: 자동 숨김 Dock(왼쪽)")
    let d = PanelPlacement.defaultFrame(size: CGSize(width: 340, height: 300), safe: visible)
    check(d == CGRect(x: 1084, y: 96, width: 340, height: 300), "panel: 기본은 오른쪽 아래 여백 16")
    check(PanelPlacement.defaultFrame(size: CGSize(width: 340, height: 2000), safe: visible).height == 763, "panel: 화면보다 크면 높이를 줄임(안에서 스크롤)")
    let pulled = PanelPlacement.clamp(CGRect(x: 1400, y: 0, width: 340, height: 300), into: visible)
    check(pulled == CGRect(x: 1084, y: 96, width: 340, height: 300), "panel: 화면 밖·Dock 위로 옮긴 자리는 안으로 당김")
    check(PanelPlacement.clamp(CGRect(x: 500, y: 400, width: 340, height: 300), into: visible) == CGRect(x: 500, y: 400, width: 340, height: 300),
          "panel: 안에 있는 자리는 그대로")
}

// MARK: 마크다운 표
do {
    let text = "세션 현황이에요.\n\n| 구분 | 개수 | 비고 |\n|:---|---:|:---:|\n| **작업 세션** | 1 | `price-service` |\n| 대화·알람 | 28 | a \\| b |\n\n끝."
    let blocks = MarkdownBlocks.split(text)
    let t: MarkdownTable = { if blocks.count > 1, case .table(let found) = blocks[1] { return found }; return MarkdownTable(header: [], aligns: [], rows: []) }()
    check(blocks.count == 3 && blocks[0] == .text("세션 현황이에요.") && blocks[2] == .text("끝."), "table: 글·표·글로 나눔")
    check(t.header == ["구분", "개수", "비고"] && t.aligns == [.leading, .trailing, .center], "table: 머리줄과 열 정렬")
    check(t.rows == [["**작업 세션**", "1", "`price-service`"], ["대화·알람", "28", "a | b"]], "table: 칸 (꾸밈 그대로, \\| 는 글자)")
    check(MarkdownBlocks.split("```\n| a | b |\n|---|---|\n```") .count == 1 && !MarkdownBlocks.hasTable("```\n| a | b |\n|---|---|\n```"),
          "table: 코드 블록 안의 표 모양은 그대로 글")
    check(MarkdownBlocks.split("a | b 는 파이프\n그냥 글") == [.text("a | b 는 파이프\n그냥 글")], "table: 구분줄 없으면 표 아님")
    check(MarkdownBlocks.plain("| 이름 | 수 |\n|---|---|\n| x | 1 |\n| y |") == "이름: x · 수: 1\n이름: y · 수: ", "table: 미리 알림 카드용 정렬 글 (모자란 칸 채움)")
}

// MARK: 표 열 너비
do {
    let fits = TableLayout.widths(natural: [80, 50, 100], available: 400, spacing: 10)
    check(fits == [80, 60, 100], "tablelayout: 다 들어가면 자연 폭 (최소 60)")
    let squeezed = TableLayout.widths(natural: [300, 100, 400, 200, 120], available: 450, spacing: 10)!
    check(abs(squeezed.reduce(0, +) - 410) < 0.001 && squeezed.allSatisfy { $0 >= 60 } && squeezed[2] > squeezed[0] && squeezed[0] > squeezed[3],
          "tablelayout: 넘치면 자연 폭 비례로 줄이고 최소 60, 말풍선 폭 안에")
    let withMin = TableLayout.widths(natural: [1000, 10, 10], available: 300, spacing: 0)!
    check(withMin[1] == 60 && withMin[2] == 60 && abs(withMin[0] - 180) < 0.001, "tablelayout: 좁은 열은 최소 폭 고정, 남는 폭은 넓은 열에")
    check(TableLayout.widths(natural: Array(repeating: 100, count: 8), available: 450, spacing: 10) == nil, "tablelayout: 최소 폭으로도 안 들어가면 카드로")
    check(TableLayout.breakable("sample-shop-price-service") == "sample-\u{200B}shop-\u{200B}price-\u{200B}service"
          && TableLayout.breakable("[a](http://x-y)") == "[a](http://x-y)" && TableLayout.breakable("`a-b`") == "`a-b`",
          "tablelayout: 긴 이름은 칸 안 줄바꿈 자리, 링크·코드는 그대로")
}

// MARK: 표 미리보기·복사
do {
    let fit = TablePreview.make(natural: [100, 80, 90], rowCount: 3, available: 450, spacing: 12)
    check(fit.columns == 3 && fit.hiddenColumns == 0 && fit.hiddenRows == 0 && !fit.truncated, "tablepreview: 들어가면 그대로")
    let wide = TablePreview.make(natural: Array(repeating: 100, count: 9), rowCount: 3, available: 450, spacing: 12)
    let used = wide.widths.reduce(0, +) + Double(wide.columns) * 12 + TablePreview.moreColumnWidth
    check(wide.columns == 5 && wide.hiddenColumns == 4 && wide.truncated && used <= 450 + 0.001, "tablepreview: 들어가는 앞쪽 열만 + N열")
    let tall = TablePreview.make(natural: [80, 80], rowCount: 20, available: 450, spacing: 12)
    check(tall.rows == 8 && tall.hiddenRows == 12 && tall.hiddenColumns == 0 && tall.truncated, "tablepreview: 8행 넘으면 앞 8행 + M행")
    let t = MarkdownTable(header: ["a", "b"], aligns: [.leading, .trailing], rows: [["**x**", "1|2"], ["`y`", "3"]])
    check(t.markdown == "| a | b |\n|---|---:|\n| **x** | 1\\|2 |\n| `y` | 3 |", "tablepreview: 마크다운 그대로 복사")
    check(t.tabSeparated == "a\tb\nx\t1|2\ny\t3", "tablepreview: 엑셀용 탭 구분 복사 (꾸밈 기호 뺌)")
}

// MARK: 항상 허용 규칙
do {
    check(ShellPrograms.programs("find ~/x -type f | sed 's|a;b|c|' | awk -F. '{print $NF}' | sort && uniq -c").programs == ["find", "sed", "awk", "sort", "uniq"],
          "allow: 파이프·&& 로 나누고 따옴표 안의 | ; 는 무시")
    check(ShellPrograms.programs("git log -1 --oneline; git push origin HEAD; ls -la").programs == ["git log", "git push", "ls"], "allow: git 은 하위 명령까지")
    check(ShellPrograms.programs("C=~/Downloads; find \"$C\"").problem?.contains("변수 대입") == true, "allow: 변수 대입은 항상 허용 대상에서 뺌")
    check(ShellPrograms.programs("echo $(whoami)").problem?.contains("$(") == true && ShellPrograms.programs("echo \"$(date)\"").problem != nil,
          "allow: $(…) 은 뺌 (따옴표 안 포함)")
    check(ShellPrograms.programs("echo '$(not)' > /tmp/out.txt 2>&1").programs == ["echo"], "allow: 작은따옴표 안 $( 는 글자, 리다이렉트 대상은 프로그램 아님")
    check(ShellPrograms.programs("sed -i '' 's/a/b/' f").programs == ["sed -i"] && ShellPrograms.isRisky("sed -i") && ShellPrograms.isRisky("git push")
          && !ShellPrograms.isRisky("git log") && ShellPrograms.isRisky("rm"), "allow: 바꾸는 프로그램은 주의")
    let deny = ["Bash(sudo:*)", "Bash(defaults write:*)"]
    let m1 = AllowRules.menu(tool: "Bash", input: ["command": "find . | sort | head -3"], suggested: [], deny: deny)
    check(m1.unavailable == nil && m1.options.first?.rules == ["Bash(find:*)", "Bash(sort:*)", "Bash(head:*)"] && m1.options.count == 4,
          "allow: 제안이 없어도 묶음 + 프로그램마다 항목")
    let m2 = AllowRules.menu(tool: "Bash", input: ["command": "ls && sudo -n true"], suggested: [], deny: deny)
    check(m2.options.isEmpty && m2.unavailable?.contains("sudo") == true, "allow: deny 프로그램이 들어 있으면 메뉴에 안 나옴")
    let m3 = AllowRules.menu(tool: "Bash", input: ["command": "rm -f /tmp/x"], suggested: ["Bash(rm -f /tmp/x)"], deny: deny)
    check(m3.options.first?.label.hasPrefix("Claude Code 제안") == true && m3.options.last?.caution == true, "allow: 제안 + rm 은 주의")
    let m4 = AllowRules.menu(tool: "WebFetch", input: ["url": "https://docs.example.com/a"], suggested: [], deny: deny)
    check(m4.options.first?.rules == ["WebFetch(domain:docs.example.com)"], "allow: WebFetch 는 도메인 규칙")
    let req = ClaudeStream.permissionRequest(["type": "control_request", "request_id": "r9",
                                              "request": ["subtype": "can_use_tool", "tool_name": "Bash", "input": ["command": "find ."], "permission_suggestions": []] as [String: Any]])!
    let resp = ClaudeStream.object(ClaudeStream.response(to: req, decision: .allowRules(["Bash(find:*)", "Bash(sort:*)"])))
    let perms = (((resp?["response"] as? [String: Any])?["response"] as? [String: Any])?["updatedPermissions"] as? [[String: Any]])?.first
    let rules = perms?["rules"] as? [[String: Any]]
    check(perms?["destination"] as? String == "session" && rules?.count == 2 && rules?.first?["ruleContent"] as? String == "find:*" && rules?.first?["toolName"] as? String == "Bash",
          "allow: 고른 규칙을 이번 실행(session) 규칙으로 보냄")
}

// MARK: ClaudeResponse
do {
    let ok = #"{"type":"result","is_error":false,"result":"답","session_id":"s1"}"#
    check(ClaudeResponse.parse(stdout: Data(ok.utf8), exitCode: 0) == .success(text: "답", sessionId: "s1"), "parse: 성공")
    let stream = "{\"type\":\"system\",\"subtype\":\"init\"}\n{\"type\":\"assistant\"}\n{\"type\":\"result\",\"is_error\":false,\"result\":\"빨강\",\"session_id\":\"s9\"}\n"
    check(ClaudeResponse.parse(stdout: Data(stream.utf8), exitCode: 0) == .success(text: "빨강", sessionId: "s9"), "parse: stream-json 의 result 줄")
    let auth = #"{"is_error":true,"result":"Failed to authenticate: OAuth session expired","session_id":"x"}"#
    check(ClaudeResponse.parse(stdout: Data(auth.utf8), exitCode: 1) == .failure(.notLoggedIn), "parse: 로그인 만료 JSON")
    check(ClaudeResponse.parse(stdout: Data("Please run /login".utf8), exitCode: 1) == .failure(.notLoggedIn), "parse: JSON 아닌 로그인 안내")
    if case .failure(.malformed) = ClaudeResponse.parse(stdout: Data("segfault".utf8), exitCode: 139) { check(true, "") }
    else { check(false, "parse: 알 수 없는 출력은 malformed") }
}

// MARK: ToolErrorCode
check(ToolErrorCode.detect(in: "AUTH_EXPIRED:jira\nJira 연결이 만료됐어요") == [.authExpired("jira")], "detect: 만료")
check(ToolErrorCode.detect(in: "NETWORK_UNREACHABLE:jira, NOT_CONNECTED:hr") == [.networkUnreachable("jira"), .notConnected("hr")], "detect: 여러 개")
check(ToolErrorCode.detect(in: "정상 답변") == [], "detect: 없음")

// MARK: ReportSchedule
do {
    var cal = Calendar(identifier: .gregorian)
    cal.timeZone = TimeZone(identifier: "Asia/Seoul")!
    func at(_ day: Int, _ h: Int, _ m: Int) -> Date {
        cal.date(from: DateComponents(year: 2026, month: 10, day: day, hour: h, minute: m))!
    }
    let yesterday = "2026-10-20", today = "2026-10-21"
    check(!ReportSchedule.shouldRun(now: at(21, 9, 59), lastRunDay: nil, hour: 10, minute: 0, calendar: cal), "schedule: 10시 전")
    check(ReportSchedule.shouldRun(now: at(21, 10, 0), lastRunDay: nil, hour: 10, minute: 0, calendar: cal), "schedule: 10시 정각")
    check(!ReportSchedule.shouldRun(now: at(21, 15, 0), lastRunDay: today, hour: 10, minute: 0, calendar: cal), "schedule: 오늘 이미 실행")
    check(ReportSchedule.shouldRun(now: at(21, 15, 0), lastRunDay: yesterday, hour: 10, minute: 0, calendar: cal), "schedule: 잠자기 후 깨어남")
    check(!ReportSchedule.shouldRun(now: at(21, 0, 5), lastRunDay: yesterday, hour: 10, minute: 0, calendar: cal), "schedule: 자정 직후")
    check(ReportSchedule.dayString(at(21, 23, 59), calendar: cal) == today, "schedule: 날짜 문자열")
}

// MARK: Connections JSON (pokemate 출력 형식)
do {
    let status = #"[{"system":"jira","name":"Jira","group":"ad","status":"expired"},{"system":"wiki","name":"Wiki","group":"ad","status":"unavailable"}]"#
    let s = try? JSONDecoder().decode([SystemStatus].self, from: Data(status.utf8))
    check(s?.map(\.status) == [.expired, .unavailable], "status: 디코딩")
    let connect = #"[{"system":"jira","status":"network_unreachable","message":"사내망 연결을 확인해 주세요"}]"#
    let c = try? JSONDecoder().decode([ConnectResult].self, from: Data(connect.utf8))
    check(c?.first?.status == "network_unreachable", "connect: 디코딩")
    check(ConnectTarget.group("ad").arguments == ["connect", "--group", "ad"], "connect: 그룹 인자")
}

// MARK: Alarm
do {
    var cal = Calendar(identifier: .gregorian)
    cal.timeZone = TimeZone(identifier: "Asia/Seoul")!
    // 2026-10-24 토, 2026-10-26 월
    func at(_ day: Int, _ h: Int, _ m: Int) -> Date { cal.date(from: DateComponents(year: 2026, month: 10, day: day, hour: h, minute: m))! }
    let weekday = Alarm(title: "t", prompt: "p", hour: 9, minute: 30, repeatRule: .weekdays)
    check(!weekday.shouldRun(now: at(24, 10, 0), calendar: cal), "alarm: 평일 알림은 토요일에 안 울림")
    check(weekday.shouldRun(now: at(26, 9, 30), calendar: cal), "alarm: 평일 알림 월요일 9:30")
    check(!weekday.shouldRun(now: at(26, 9, 29), calendar: cal), "alarm: 시각 전")
    var off = weekday; off.enabled = false
    check(!off.shouldRun(now: at(26, 10, 0), calendar: cal), "alarm: 꺼진 알림")
    let saturday = Alarm(title: "t", prompt: "p", hour: 8, minute: 0, repeatRule: .days([7]))
    check(saturday.shouldRun(now: at(24, 8, 1), calendar: cal), "alarm: 요일 선택(토)")
    let once = Alarm(title: "t", prompt: "p", hour: 14, minute: 0, repeatRule: .once("2026-10-26"))
    check(once.shouldRun(now: at(26, 15, 0), calendar: cal) && !once.shouldRun(now: at(27, 15, 0), calendar: cal), "alarm: 한 번은 그날만")
    var ran = weekday; ran.lastRunDay = "2026-10-26"
    check(!ran.shouldRun(now: at(26, 11, 0), calendar: cal), "alarm: 오늘 이미 실행")
    check(Alarm.Repeat.days([2, 4, 6]).description == "월·수·금", "alarm: 요일 표시")
    let encoded = try? JSONEncoder().encode([saturday, once])
    check(encoded.flatMap { try? JSONDecoder().decode([Alarm].self, from: $0) } == [saturday, once], "alarm: 저장 형식 왕복")
}

// MARK: ConnectorStatus
do {
    let ok = "claude.ai Google Docs: https://x - ✔ Connected\nclaude.ai Microsoft 365: https://microsoft365.mcp.claude.com/mcp - ✔ Connected"
    let need = "claude.ai Microsoft 365: https://microsoft365.mcp.claude.com/mcp - ! Needs authentication"
    check(ConnectorStatus.parse(output: ok, name: "Microsoft 365") == .connected, "connector: 연결됨")
    check(ConnectorStatus.parse(output: need, name: "Microsoft 365") == .disconnected, "connector: 인증 필요")
    check(ConnectorStatus.parse(output: "", name: "Microsoft 365") == .disconnected, "connector: 목록에 없음")
}

// MARK: ChatMessage
do {
    let old = #"[{"id":"6F1D6F5E-0000-0000-0000-000000000001","role":"user","text":"hi","date":0}]"#
    let m = try? JSONDecoder().decode([ChatMessage].self, from: Data(old.utf8))
    check(m?.first?.images == [], "chat: 예전 대화 기록도 읽음 (이미지 없음)")
}

// MARK: Reminder
do {
    let t0 = Date(timeIntervalSince1970: 1_800_000_000)
    var q = ReminderQueue()
    let a = Reminder(title: "미완료 Jira 보고", summary: "미완료 Jira 3건", firedAt: t0)
    let b = Reminder(title: "메일", summary: "답장 필요 2통", firedAt: t0.addingTimeInterval(60))
    q.add(a); q.add(b)
    check(q.visible(now: t0.addingTimeInterval(61)).map(\.id) == [b.id, a.id], "reminder: 최근 것이 위")
    q.snooze(a.id, minutes: 10, now: t0.addingTimeInterval(120))
    check(q.visible(now: t0.addingTimeInterval(120 + 599)).map(\.id) == [b.id], "reminder: 다시 알림 전에는 안 보임")
    check(q.visible(now: t0.addingTimeInterval(120 + 600)).map(\.id).contains(a.id), "reminder: 다시 알림 시각이 되면 보임")
    q.dismiss(b.id)
    check(!q.items.contains { $0.id == b.id }, "reminder: 확인하면 사라짐")
    q.add(b)
    q.dismissVisible(now: t0.addingTimeInterval(130))
    check(q.items.map(\.id) == [a.id], "reminder: 모두 확인은 보이는 카드만 (미룬 카드는 남음)")
    let data = try? JSONEncoder().encode(q)
    check(data.flatMap { try? JSONDecoder().decode(ReminderQueue.self, from: $0) } == q, "reminder: 저장 형식 왕복")
    check(Reminder.summary(of: "\n  미완료 Jira 3건\nNOVA-1") == "미완료 Jira 3건", "reminder: 첫 줄 요약")
}

// MARK: Hotkey, GitHub
do {
    check(Hotkey.default.display == "⌃⌥⇧⌘ Space", "hotkey: 기본 표기")
    check(Hotkey(keyCode: 40, modifiers: Hotkey.control | Hotkey.option).display == "⌃⌥ K", "hotkey: ⌃⌥K 표기")
    check(!Hotkey(keyCode: 40, modifiers: Hotkey.shift).isValid, "hotkey: Shift 만은 안 됨")
    check(GitHubLogin.code(in: "! First copy your one-time code: 1A2B-3C4D\nPress Enter") == "1A2B-3C4D", "github: 한 번용 코드 찾기")
    let prs = #"[{"id":7,"repo":"acme/sample-service","number":1290,"title":"t","author":"kim","url":"https://g/pull/1290","updated":"2026-10-22T10:42"}]"#
    check((try? JSONDecoder().decode([PullRequest].self, from: Data(prs.utf8)))?.first?.number == 1290, "github: PR 디코딩")
}

// MARK: Trigger
do {
    let t = Alarm.defaultReviewRequestTrigger()
    check(!t.shouldRun(now: Date(), calendar: .current), "trigger: 시각 판단으로는 울리지 않음")
    let old = #"[{"id":"6F1D6F5E-0000-0000-0000-000000000002","title":"t","prompt":"p","hour":9,"minute":0,"repeatRule":{"daily":{}},"enabled":true}]"#
    check((try? JSONDecoder().decode([Alarm].self, from: Data(old.utf8)))?.first?.trigger == nil, "trigger: 예전 알림 파일도 읽음")
    let enc = try? JSONEncoder().encode([t])
    check(enc.flatMap { try? JSONDecoder().decode([Alarm].self, from: $0) }?.first?.trigger == t.trigger, "trigger: 저장 형식 왕복")
    let a = EventItem(key: "1", title: "a", summary: "", url: ""), b = EventItem(key: "2", title: "b", summary: "", url: "")
    check(TriggerDiff.newItems(current: [a, b], seen: nil).isEmpty, "trigger: 처음엔 기준만 잡고 알리지 않음")
    check(TriggerDiff.newItems(current: [a, b], seen: ["1"]) == [b], "trigger: 새 항목만")
    let t5 = Trigger(system: "github", event: "e")
    let t1 = Trigger(system: "github", event: "e", intervalMinutes: 1)
    let base = Date(timeIntervalSince1970: 1_000_000)
    check(t5.interval == 5 && t5.isDue(lastChecked: nil, now: base) && !t5.isDue(lastChecked: base, now: base.addingTimeInterval(240))
          && t5.isDue(lastChecked: base, now: base.addingTimeInterval(298)) && t1.isDue(lastChecked: base, now: base.addingTimeInterval(60)),
          "trigger: 주기 판단 (기본 5분, 1분)")
    let oldTrigger = #"{"system":"github","event":"e"}"#
    check((try? JSONDecoder().decode(Trigger.self, from: Data(oldTrigger.utf8)))?.interval == 5, "trigger: 예전 트리거는 5분")
    check(TriggerEvent.events(for: "github").count == 3 && TriggerEvent.systems == ["github", "deploy", "jira"], "trigger: 이벤트 목록")
}

// MARK: AlarmProposal
do {
    let reply = "알림 3개를 준비했어요.\n\n```pokemate-alarm\n[{\"title\":\"1:1 요청\",\"hour\":9,\"minute\":0,\"repeat\":\"daily\",\"prompt\":\"\"},{\"title\":\"1:1 요청\",\"hour\":13,\"repeat\":\"weekdays\"},{\"title\":\"x\",\"hour\":25}]\n```"
    let (text, alarms) = AlarmProposal.extract(from: reply)
    check(text == "알림 3개를 준비했어요.", "proposal: 블록은 본문에서 뺌")
    check(alarms.count == 2 && alarms[0].hour == 9 && alarms[0].repeatRule == .daily && alarms[1].minute == 0
          && alarms[1].repeatRule == .weekdays && alarms[0].prompt.isEmpty, "proposal: 알림 읽기, 잘못된 시각은 버림")
    check(AlarmProposal.extract(from: "그냥 답").alarms.isEmpty, "proposal: 블록 없으면 없음")
    check(AlarmProposal.extract(from: "```pokemate-alarm\n깨진 json\n```").text.isEmpty, "proposal: 깨진 블록도 본문에서 뺌")
    check(AlarmProposal.repeatRule("days:4,2,2") == .days([2, 4]) && AlarmProposal.repeatRule("once:2026-10-23") == .once("2026-10-23")
          && AlarmProposal.repeatRule("days:9") == nil && AlarmProposal.repeatRule("hourly") == nil, "proposal: 반복 규칙")
    let grouped = AlarmProposal.extract(from: "```pokemate-alarm\n[{\"title\":\"a\",\"hour\":9,\"group\":\" 팀 미팅 \"},{\"title\":\"b\",\"hour\":10,\"group\":\"\"}]\n```").alarms
    check(grouped.map(\.group) == ["팀 미팅", nil], "proposal: 그룹 이름 (빈 이름은 그룹 없음)")
    let loose = AlarmProposal.extract(from: "```pokemate-alarm\n[{\"time\":\"18:30\",\"repeat\":\"daily\",\"message\":\"m\"}]\n```").alarms
    check(loose.count == 1 && loose[0].hour == 18 && loose[0].minute == 30 && loose[0].title == "m", "proposal: time·message 키도 받음")
    check(AlarmProposal.looksLikeRequest("매일 9시에 잊지말라고 알림 기록해줘") && AlarmProposal.looksLikeRequest("매일 오전 9시에 1:1 알람을 해야 한다고 알람설정해줘")
          && AlarmProposal.looksLikeRequest("잊지 않게 리마인드 해줘")
          && !AlarmProposal.looksLikeRequest("내 미완료 Jira"), "proposal: 알림 요청 판단 (알람도)")
    check(AlarmProposal.extract(from: AlarmProposal.hint).alarms.count == 1, "proposal: 안내 속 예시 형식이 실제로 읽힘")
    let msg = ChatMessage(role: .assistant, text: "t", alarms: alarms)
    let back = (try? JSONEncoder().encode(msg)).flatMap { try? JSONDecoder().decode(ChatMessage.self, from: $0) }
    check(back?.alarms.map(\.id) == alarms.map(\.id), "proposal: 대화 기록에 저장 (같은 id 라 다시 등록 안 됨)")

    var cal = Calendar(identifier: .gregorian); cal.timeZone = TimeZone(identifier: "Asia/Seoul")!
    let afternoon = cal.date(from: DateComponents(year: 2026, month: 10, day: 22, hour: 14))!
    var nine = alarms[0]; nine.resetSchedule(now: afternoon, calendar: cal)
    check(!nine.shouldRun(now: afternoon, calendar: cal), "proposal: 오후에 등록한 9시 알림은 바로 울리지 않음")
    var six = nine; six.hour = 18; six.resetSchedule(now: afternoon, calendar: cal)
    check(six.lastRunDay == nil && six.shouldRun(now: cal.date(byAdding: .hour, value: 4, to: afternoon)!, calendar: cal), "proposal: 오늘 남은 18시는 울림")
}

// MARK: AlarmGroup
do {
    let a = Alarm(title: "1:1 요청", prompt: "", hour: 9, minute: 0, repeatRule: .daily, group: "1:1")
    let b = Alarm(title: "근무일정", prompt: "p", hour: 10, minute: 0, repeatRule: .weekdays)
    let c = Alarm(title: "1:1 요청", prompt: "", hour: 13, minute: 0, repeatRule: .daily, enabled: false, group: "1:1")
    let d = Alarm(title: "x", prompt: "p", hour: 11, minute: 0, repeatRule: .daily, group: "지운 그룹")
    let t = Alarm(title: "리뷰", prompt: "", hour: 0, minute: 0, repeatRule: .daily,
                  trigger: Trigger(system: "github", event: "github.review_requested"), group: "1:1")
    let split = AlarmGroup.make([a, b, c, d, t], groups: ["1:1", "빈 그룹"])
    check(split.groups.map(\.alarms.count) == [3, 0] && split.ungrouped.map(\.id) == [b.id, d.id],
          "group: 그룹 순서대로, 빈 그룹도, 없는 그룹 이름은 그룹 없음으로")
    check(split.groups[0].summary == "09:00 · 13:00 매일 · 트리거 1" && split.groups[0].anyEnabled, "group: 요약과 켜짐")
    check(split.groups[1].summary.contains("끌어다"), "group: 빈 그룹 안내")
    let old = #"[{"id":"6F1D6F5E-0000-0000-0000-000000000003","title":"t","prompt":"p","hour":9,"minute":0,"repeatRule":{"daily":{}},"enabled":true}]"#
    check((try? JSONDecoder().decode([Alarm].self, from: Data(old.utf8)))?.first?.group == nil, "group: 예전 알림 파일은 그룹 없음")
}

// MARK: JiraProposal
do {
    var cal = Calendar(identifier: .gregorian); cal.timeZone = TimeZone(identifier: "Asia/Seoul")!
    let now = cal.date(from: DateComponents(year: 2026, month: 10, day: 22, hour: 14))!
    check(JiraProposal.looksLikeRequest("지라 등록해줘: 쿠폰 API 정리") && JiraProposal.looksLikeRequest("Jira 이슈 하나 만들어줘")
          && JiraProposal.looksLikeRequest("다시 담당자 내이름으로") && JiraProposal.looksLikeRequest("어싸인은 나지 ?")
          && !JiraProposal.looksLikeRequest("내 미완료 Jira 알려줘") && !JiraProposal.looksLikeRequest("매일 9시 알림 등록"), "jira: 등록 요청 판단")
    let reply = "초안이에요.\n```pokemate-jira\n{\"issue\":{\"project\":\"MYPROJ\",\"issueType\":\"Story\",\"summary\":\" 쿠폰 정리 \",\"description\":\"h3. 배경\",\"targetEnd\":\"2026-12-31\"}}\n```"
    let (text, draft) = JiraProposal.extract(from: reply)
    check(text == "초안이에요." && draft?.summary == "쿠폰 정리" && draft?.originalEstimate == "" && draft?.targetEnd == "2026-12-31", "jira: 초안 읽기 (빠진 항목은 빈칸)")
    check(JiraProposal.extract(from: "```pokemate-jira\n{\"issue\":{\"project\":\"P\"}}\n```").draft == nil, "jira: 제목·유형 없는 초안은 버림")
    check(JiraProposal.extract(from: "```pokemate-jira\n{\"project\":\"P\",\"issueType\":\"Story\",\"summary\":\"s\"}\n```").draft?.project == "P", "jira: 감싸지 않은 초안도 읽음")
    let hint = JiraProposal.hint(now: now, calendar: cal)
    check(hint.contains("오늘은 2026-10-22.") && hint.contains("Jira 스킬(skills/jira.md)")
          && hint.contains("pokemate-rule") && !hint.contains("\\("), "jira: 규칙을 따르라는 안내 (날짜 계산됨)")
    let example = JiraProposal.extract(from: hint).draft
    check(!hint.contains("365d") && !hint.contains("12-31") && example?.originalEstimate == "" && example?.targetEnd == ""
          && hint.contains("견적이 며칠인가요"), "jira: 견적·끝 날짜를 지어내지 않고, 없으면 비워 두고 묻기")
    check(hint.contains("담당자(Assignee)") && hint.contains("본인으로 자동"), "jira: 담당자는 본인 자동이라고 안내")
    check(JiraProposal.extract(from: hint).draft?.targetStart == "2026-10-22", "jira: 안내 예시 형식이 읽힘")
    var m = ChatMessage(role: .assistant, text: "t", jira: draft)
    m.jiraCreated = JiraCreated(key: "MYPROJ-1", url: "u")
    let back = (try? JSONEncoder().encode(m)).flatMap { try? JSONDecoder().decode(ChatMessage.self, from: $0) }
    check(back?.jira == draft && back?.jiraCreated?.key == "MYPROJ-1", "jira: 대화 기록에 초안·결과 저장")
    let oldChat = #"{"id":"6F1D6F5E-0000-0000-0000-000000000009","role":"assistant","text":"t","date":0,"jiraDefaults":{"project":"P"}}"#
    check((try? JSONDecoder().decode(ChatMessage.self, from: Data(oldChat.utf8))) != nil, "jira: 예전 대화 기록(기본값 포함)도 읽음")
}

// MARK: ClaudeAccount
do {
    let status = #"{"loggedIn":true,"email":"kim@example.com","orgName":"Example Inc.","subscriptionType":"team"}"#
    check(ClaudeAccount.parse(Data(status.utf8))?.display == "kim@example.com · Example Inc. (team)", "account: 표시")
    check(ClaudeAccount.parse(Data(#"{"loggedIn":false}"#.utf8))?.display == "로그인 안 됨", "account: 로그인 안 됨")
    let limit = #"{"type":"result","is_error":true,"result":"You've hit your individual spend limit · run /usage-credits to ask your admin for a higher limit"}"#
    check(ClaudeResponse.parse(stdout: Data(limit.utf8), exitCode: 1) == .failure(.usageLimit), "account: 개인 사용 한도 오류")
    check(AppPaths.processEnvironment(settings: .default)["CLAUDE_CONFIG_DIR"] == AppPaths.claudeConfigDir.path, "account: 메이트는 항상 전용 로그인 폴더")
}

// MARK: Rules
do {
    check(Rules.safeName("Wiki 규칙.md") == "wiki규칙" && Rules.safeName("!!") == "memory", "rules: 파일 이름 정리")
    let jiraFile = Rules.File(url: Rules.skillsDir.appendingPathComponent("jira.md"))
    let memoryFile = Rules.File(url: Rules.myDir.appendingPathComponent("memory.md"))
    let root = Rules.File(url: Rules.claudeMd)
    check(jiraFile.name == "skills/jira.md" && jiraFile.kind == .skill && memoryFile.name == "rules/memory.md" && memoryFile.kind == .rule
          && root.name == "CLAUDE.md" && root.kind == .root, "rules: root·rules·skills 구분")
    check(!Rules.prompt(for: "아무 질문").contains("## CLAUDE.md"), "rules: CLAUDE.md 는 Claude Code 가 읽으므로 안 붙임")
    let jira = Rules.Info.parse(name: "skills/jira.md", text: "---\ndescription: Jira 이슈 등록·worklog\nkeywords: jira, 지라, work log\n---\n# Jira 규칙\n- 프로젝트 키: X")
    let wiki = Rules.Info.parse(name: "skills/wiki.md", text: "# 위키 검색 규칙\n- 공간은 PRICE")
    let memory = Rules.Info.parse(name: "rules/memory.md", text: "# memory\n- 팀은 가격팀")
    check(jira.description == "Jira 이슈 등록·worklog" && jira.body == "# Jira 규칙\n- 프로젝트 키: X", "rules: 머리말 읽기")
    check(wiki.description == "위키 검색 규칙" && wiki.keywords == ["wiki"], "rules: 머리말 없으면 제목·파일 이름")
    check(jira.matches("회의 WorkLog 10-11") && jira.matches("지라 등록해줘") && !jira.matches("오늘 메일 요약"), "rules: 키워드로 판단")
    let p = Rules.render(rules: [memory], skills: [jira, wiki], message: "지라 등록해줘")
    check(p.contains("- skills/wiki.md: 위키 검색 규칙") && p.contains("## skills/jira.md\n# Jira 규칙") && !p.contains("공간은 PRICE")
          && p.contains("## rules/memory.md\n# memory\n- 팀은 가격팀") && p.contains("read_skill"), "rules: rules 는 늘, skills 는 목록 + 맞는 본문")
    let unrelated = Rules.render(rules: [memory], skills: [jira, wiki], message: "오늘 메일 요약")
    check(!unrelated.contains("프로젝트 키") && unrelated.contains("팀은 가격팀"), "rules: 상관없는 질문엔 스킬 본문 없음, 규칙은 있음")
    let reply = "알겠어요.\n```pokemate-rule\n[{\"file\":\"jira\",\"text\":\"제목 앞에 [회의]\"},{\"file\":\"memory\",\"text\":\" \"}]\n```"
    let (text, notes) = RuleProposal.extract(from: reply)
    check(text == "알겠어요." && notes == [RuleNote(file: "jira", text: "제목 앞에 [회의]")], "rules: 제안 읽기 (빈 줄은 버림)")
    check(RuleProposal.extract(from: "```pokemate-rule\n{\"file\":\"memory\",\"text\":\"팀은 가격팀\"}\n```").notes.count == 1, "rules: 한 개짜리도 읽음")
    check(RuleProposal.extract(from: RuleProposal.hint).notes.count == 2, "rules: 안내 예시가 읽힘")
    check(RuleProposal.looksLikeRequest("앞으로 지라 제목 앞에 [회의] 붙여줘") && !RuleProposal.looksLikeRequest("내 미완료 Jira"), "rules: 요청 판단")
    let old = #"{"assistantName":"옛 이름","repoDir":"/r","claudePath":"/c","uvPath":"/u","reportHour":10,"reportMinute":0}"#
    let oldSettings = try? JSONDecoder().decode(AppSettings.self, from: Data(old.utf8))
    check(oldSettings?.ownerName == "", "rules: 예전 설정은 주인 이름 없음 (처음 인사)")
    let oldAlarm = #"[{"id":"6F1D6F5E-0000-0000-0000-000000000004","title":"t","prompt":"p","hour":9,"minute":0,"repeatRule":{"daily":{}},"enabled":true}]"#
    let decodedAlarm = (try? JSONDecoder().decode([Alarm].self, from: Data(oldAlarm.utf8)))?.first
    check(decodedAlarm?.toMac == true && decodedAlarm?.toPanel == true, "alarm: 예전 알림은 받을 곳 둘 다 켜짐")
    var quiet = decodedAlarm!; quiet.notifyMac = false
    let round = (try? JSONEncoder().encode([quiet])).flatMap { try? JSONDecoder().decode([Alarm].self, from: $0) }?.first
    check(round?.toMac == false && round?.toPanel == true, "alarm: 받을 곳 저장 왕복")
    let forget = RuleProposal.extract(from: "```pokemate-rule\n[{\"file\":\"memory\",\"text\":\"가격팀\",\"forget\":true}]\n```").notes
    check(forget.count == 1 && forget[0].isForget && forget[0].isMemory, "memory: 잊기 제안 읽기")
    check(RuleProposal.extract(from: RuleProposal.hint).notes.first?.isMemory == true, "memory: 안내 예시의 기억이 읽힘")
    check(RuleProposal.looksLikeRequest("그거 잊어줘") && RuleProposal.looksLikeRequest("내 이름 기억하고 있어?"), "memory: 잊기·기억 질문도 안내")
}

// MARK: Memory (실제 데이터 폴더 대신 임시 폴더를 쓰도록 앱 데이터 폴더는 건드리지 않고 파일 단위로 검사)
do {
    let backup = try? Data(contentsOf: Memory.file)
    try? FileManager.default.removeItem(at: Memory.file)
    try? Memory.add("팀은 가격팀")
    try? Memory.add("팀은 가격팀")
    try? Memory.add("화요일 10시 팀 회의")
    check(Memory.items() == ["팀은 가격팀", "화요일 10시 팀 회의"], "memory: 추가 (같은 건 한 번만)")
    check((try? Memory.forget("가격팀")) == 1 && Memory.items() == ["화요일 10시 팀 회의"], "memory: 들어간 말로 잊기")
    check((try? Memory.forget("화요일 10시 팀 회의")) == 1 && Memory.items().isEmpty, "memory: 똑같은 줄 잊기")
    try? FileManager.default.removeItem(at: Memory.file)
    if let backup { try? backup.write(to: Memory.file) }
}

// MARK: Worklog
do {
    let reply = "준비했어요.\n```pokemate-worklog\n[{\"issueKey\":\"myproj-100\",\"summary\":\"회의\",\"date\":\"2026-10-22\",\"start\":\"10\",\"end\":\"11:30\"},{\"issueKey\":\"A-1\",\"date\":\"2026-10-22\",\"start\":\"14:00\",\"minutes\":0}]\n```"
    let (text, drafts) = WorklogProposal.extract(from: reply)
    check(text == "준비했어요." && drafts.count == 1, "worklog: 블록 읽기 (0분은 버림)")
    let d = drafts[0]
    check(d.issueKey == "MYPROJ-100" && d.start == "10:00" && d.minutes == 90 && d.timeText == "10:00~11:30 (1시간 30분)", "worklog: end 로 시간 계산")
    check(d.started(timeZone: TimeZone(identifier: "Asia/Seoul")!) == "2026-10-22T10:00:00.000+0900", "worklog: Jira 시작 시각 형식")
    let back = (try? JSONEncoder().encode(drafts)).flatMap { try? JSONDecoder().decode([WorklogDraft].self, from: $0) }
    check(back == drafts, "worklog: 저장 왕복")
    check(WorklogProposal.looksLikeRequest("회의 jira에 오늘 work log 10-11 등록해줘") && WorklogProposal.looksLikeRequest("오늘 뭐 했지?") && WorklogProposal.looksLikeRequest("HR랑 맞춰봐")
          && !WorklogProposal.looksLikeRequest("지라 등록해줘"), "worklog: 요청 판단")
    var cal = Calendar(identifier: .gregorian); cal.timeZone = TimeZone(identifier: "Asia/Seoul")!
    let hint = WorklogProposal.hint(now: cal.date(from: DateComponents(year: 2026, month: 10, day: 22))!, calendar: cal)
    check(WorklogProposal.extract(from: hint).drafts.first?.date == "2026-10-22", "worklog: 안내 예시가 읽힘")
}

// MARK: ChatHints
do {
    let now = Date()
    let both = ChatHints.for("평일 17시 30분에 오늘 HR랑 worklog 맞춰봐 알림 등록해줘", now: now, calendar: .current)
    check(both.contains("pokemate-alarm") && both.contains("pokemate-worklog") && both.contains("여러 개 붙었다"), "hints: 알림·worklog 둘 다 붙음")
    check(ChatHints.for("회의 worklog 10-11 기록해줘", now: now, calendar: .current).contains("여러 개 붙었다") == false, "hints: 하나면 묶음 안내 없음")
    check(ChatHints.for("안녕", now: now, calendar: .current).isEmpty, "hints: 해당 없으면 안 붙임")
}

// MARK: Notes
do {
    let reply = "정리했어요.\n```pokemate-note\n{\"title\":\"회의\",\"body\":\"- TTL 10분\"}\n```"
    let (text, notes) = NoteProposal.extract(from: reply)
    check(text == "정리했어요." && notes == [NoteDraft(title: "회의", body: "- TTL 10분")], "notes: 제안 읽기")
    check(NoteProposal.extract(from: NoteProposal.hint).notes.count == 1, "notes: 안내 예시가 읽힘")
    check(NoteProposal.looksLikeRequest("이거 메모해줘") && NoteProposal.looksLikeRequest("적어 둬") && !NoteProposal.looksLikeRequest("내 미완료 Jira"), "notes: 요청 판단")
    check(Notes.cleanTitle("## 회의 메모") == "회의 메모", "notes: 제목 정리")
    let url = try? Notes.create(title: "점검 테스트", body: "- 한 줄", now: Date(timeIntervalSince1970: 0))
    if let url {
        check(Notes.load(url)?.title == "점검 테스트" && Notes.load(url)?.preview == "- 한 줄", "notes: 만들고 읽기")
        try? Notes.delete(url)
        check(!FileManager.default.fileExists(atPath: url.path), "notes: 삭제")
    } else { check(false, "notes: 만들기") }
}

// MARK: AppSettings
do {
    let old = #"{"repoDir":"/r","claudePath":"/c","uvPath":"/u","reportHour":10,"reportMinute":0,"lastReportDay":"2026-10-21"}"#
    let s = try? JSONDecoder().decode(AppSettings.self, from: Data(old.utf8))
    check(s?.assistantName == "메이트" && s?.repoDir == "/r" && s?.lastReportDay == "2026-10-21", "settings: 예전 파일도 읽음 (이름 기본값)")
    let stored = try? JSONDecoder().decode(AppSettings.self, from: Data(#"{"assistantName":"옛 이름"}"#.utf8))
    check(stored?.assistantName == "옛 이름" && stored?.mateName == nil, "settings: 저장된 이름 읽음")
    var named = AppSettings.default
    named.assistantName = "옛 이름"
    named.mateName = "뿔카노"
    let saved = (try? JSONEncoder().encode(named)).map { String(decoding: $0, as: UTF8.self) } ?? ""
    check(saved.contains("옛 이름") && !saved.contains("뿔카노"), "settings: 이름은 저장, 대표 포켓몬 이름은 저장 안 함")
    check(ClaudeRunner.nameLine(named).contains("대표 포켓몬 \"뿔카노\""), "name: 부화한 뒤엔 대표 포켓몬 이름")
    named.mateName = nil
    check(ClaudeRunner.nameLine(named).contains("너의 이름은 \"옛 이름\"") && !ClaudeRunner.nameLine(named).contains("포켓몬"), "name: 알 단계엔 설정 이름")
    check(ClaudeRunner.nameLine(AppSettings.default).isEmpty, "name: 기본 이름이면 덧붙이지 않음")
    var owner = AppSettings.default
    let calls = ["홍길동", "트레이너", "주인님", " 길동 ", ""].map { name -> String in owner.ownerName = name; return owner.ownerCallName }
    check(calls == ["홍길동님", "트레이너님", "주인님", "길동님", ""], "owner: 호칭 끝에 님 하나만 (이미 님이면 그대로)")
    owner.ownerName = "트레이너"
    check(ClaudeRunner.ownerLine(owner).contains("\"트레이너님\"이라고 부른다") && ClaudeRunner.ownerLine(AppSettings.default).isEmpty, "owner: 프롬프트에 부를 말 그대로")
}

// MARK: 작업 탭·보낼 곳
do {
    check(SidebarItem.tabs == [.chat, .alarms, .work, .notes, .collection] && SidebarItem.tabs.map(\.title) == ["홈", "알람", "작업", "메모", "컬렉션"],
          "tab: 홈 | 알람 | 작업 | 메모 | 컬렉션")
    check(SidebarItem.target(open: "workSessions") == .work && SidebarItem.workSessionsTab == .work && SidebarItem.target(open: nil) == .chat,
          "tab: 문맥 알림·새 세션으로 바꾸기는 작업 탭으로")

    var routing = ChatRouting()
    check(routing.isMate && routing.repo == nil, "route: 기본은 메이트")
    routing.choose(" sample-shop-price-service ")
    routing.noteSent(repo: "sample-shop-price-service", text: "가격 API 타임아웃 설정 어디 있어?\n둘째 줄")
    let restored = try? JSONDecoder().decode(ChatRouting.self, from: JSONEncoder().encode(routing))
    check(restored?.repo == "sample-shop-price-service" && restored?.mateNotes == ["작업 세션 sample-shop-price-service 로 보냄: 가격 API 타임아웃 설정 어디 있어?"],
          "route: 고른 보낼 곳·메이트 기록 줄은 저장 뒤에도 유지")
    var taken = routing
    let prefix = taken.takeMatePrefix()
    check(prefix.contains("[앱 기록]") && prefix.contains("작업 세션 sample-shop-price-service 로 보냄") && taken.mateNotes.isEmpty && taken.repo == "sample-shop-price-service",
          "route: 메이트 기록 줄은 다음 메이트 메시지 앞에 한 번만, 보낼 곳은 그대로")
    check(taken.takeMatePrefix().isEmpty, "route: 두 번째엔 붙이지 않음")
    routing.reset()
    check(routing.isMate && routing.mateNotes.isEmpty, "route: 새 대화면 메이트로")
    routing.choose("  ")
    check(routing.isMate, "route: 빈 이름은 메이트")
    check(ChatRouting.summary(String(repeating: "가", count: 70)).count == 61, "route: 요약은 60자 + …")

    var message = ChatMessage(role: .assistant, text: "답")
    message.session = "r"
    message.routeSuggest = "r"
    let decoded = try? JSONDecoder().decode(ChatMessage.self, from: JSONEncoder().encode(message))
    check(decoded?.session == "r" && decoded?.routeSuggest == "r" && decoded?.routeSent == false, "route: 말풍선 세션 라벨·제안 저장")
    let old = try? JSONDecoder().decode(ChatMessage.self, from: Data(#"{"id":"6F9619FF-8B86-D011-B42D-00C04FC964FF","role":"user","text":"a","date":0}"#.utf8))
    check(old?.session == nil && old?.routeSuggest == nil, "route: 예전 대화 기록도 읽음")

    let suggested = RouteSuggestion.extract(from: "타임아웃은 설정 파일에 있어요.\n\n```pokemate-route-suggest\n{\"repo\": \"sample-shop-price-service\"}\n```\n")
    check(suggested.repo == "sample-shop-price-service" && suggested.text == "타임아웃은 설정 파일에 있어요.", "suggest: 블록 읽고 본문에서 지움")
    check(RouteSuggestion.extract(from: "그냥 답").repo == nil && RouteSuggestion.extract(from: "그냥 답").text == "그냥 답", "suggest: 블록 없으면 그대로")
    let bad = RouteSuggestion.extract(from: "답\n```pokemate-route-suggest\n{\"repo\": \"../etc\"}\n```")
    check(bad.repo == nil && bad.text == "답", "suggest: 경로가 섞인 이름은 버림")
    check(RouteSuggestion.extract(from: "답\n```pokemate-route-suggest\nnot json\n```").repo == nil, "suggest: JSON 아니면 버림")
    check(WorkRoute.extract(from: "```pokemate-route-suggest\n{\"repo\":\"x\"}\n```") == nil, "suggest: 자동 처리 레포 정하기 블록과 섞이지 않음")

    let t0 = Date(timeIntervalSince1970: 1_800_000_000)
    var book = WorkSessionBook()
    check(book.addEmpty(repo: "r", now: t0) && !book.addEmpty(repo: "r", now: t0) && book.resumeId("r") == nil,
          "new: 빈 세션 만들기, 같은 레포는 한 번만, resume 없음")
    book.record(repo: "r", sessionId: "s1", key: nil, usage: nil, now: t0)
    check(book.sessions["r"]?.id == "s1" && book.archived.isEmpty && book.resumeId("r") == "s1", "new: 첫 실행 세션 id 를 그대로 이어받음 (보관 안 함)")
    check(WorkspaceRepos.bootstrap(repo: "r", root: URL(fileURLWithPath: "/w")).contains("/w/r") && WorkspaceRepos.bootstrap(repo: "r").contains("CLAUDE.md"),
          "new: 첫 메시지에 지식 모으기 지시")

    let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("pm-repos-\(UUID().uuidString)")
    for name in ["b-repo", "a-repo", "plain"] { try? FileManager.default.createDirectory(at: tmp.appendingPathComponent(name), withIntermediateDirectories: true) }
    for name in ["b-repo", "a-repo"] { try? FileManager.default.createDirectory(at: tmp.appendingPathComponent(name).appendingPathComponent(".git"), withIntermediateDirectories: true) }
    check(WorkspaceRepos.list(root: tmp) == ["a-repo", "b-repo"], "new: ~/Workspace 아래 git 레포만 이름순")
    try? FileManager.default.removeItem(at: tmp)

    let lines = [
        #"{"type":"assistant","timestamp":"2026-10-31T23:00:00.000Z","message":{"id":"m1","model":"x","usage":{"input_tokens":100,"output_tokens":0}}}"#,
        #"{"type":"assistant","timestamp":"2026-11-02T01:00:00.000Z","message":{"id":"m2","model":"x","usage":{"input_tokens":7,"output_tokens":3}}}"#,
    ].joined(separator: "\n")
    let since = ISO8601DateFormatter().date(from: "2026-11-01T00:00:00Z")!
    check(TranscriptUsage.total(Data(lines.utf8), since: since).usage.total == 10 && TranscriptUsage.total(Data(lines.utf8)).usage.total == 110,
          "month: 이번 달 토큰은 기록 시각으로 거름")
}

// MARK: 로컬 시스템 이름
do {
    let json = #"[{"id":"hr","name":"ACME-HR","aliases":["근태판"," "],"group":"ad","enabled":true},{"id":"permit","name":"Acme Permit","group":"ad","enabled":true},{"id":"jira","name":"Jira","group":"ad","enabled":true},{"kind":"settings","id":"automation","worklogCalendarTitle":"Acme 캘린더","worklogAliases":["아크미달력"]}]"#
    let names = LocalNames.parse(Data(json.utf8))
    check(names.entries.map(\.id) == ["hr", "permit", "jira"] && names.name("hr") == "ACME-HR" && names.name("x") == nil, "local: 연결 이름 읽기 (설정 줄은 빼고)")
    check(names.entries.first?.aliases == ["근태판"] && names.worklogWords == ["Acme 캘린더", "아크미달력"], "local: 별칭·worklog 캘린더 이름 (빈 값 버림)")
    let line = names.promptLine(tools: ["mcp__pokemate__hr_holidays", "mcp__pokemate__jira_search"])
    check(line.contains("\"ACME-HR\", \"근태판\" → hr (hr_ 로 시작하는 도구)") && line.contains("\"Acme Permit\" → permit (조회 도구 없음")
          && !line.contains("\"Jira\"") && line.contains("\"Acme 캘린더\", \"아크미달력\" → worklog 캘린더"), "local: 프롬프트에 부르는 이름 → 도구 안내 (같은 이름은 생략)")
    check(LocalNames().promptLine().isEmpty, "local: 로컬 이름이 없으면 안내도 없음")
    check(LocalNames.parse(Data("not json".utf8)) == LocalNames(), "local: 깨진 파일은 빈 값")
    check(WorklogProposal.looksLikeRequest("아크미 달력이랑 비교", extra: ["아크미달력"]) && !WorklogProposal.looksLikeRequest("내일 회의실 잡아줘", extra: []),
          "local: worklog 캘린더 로컬 이름으로도 worklog 안내")
    let repo = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
    let example = (try? Data(contentsOf: repo.appendingPathComponent("shared/connections.example.json"))).map(LocalNames.parse) ?? LocalNames()
    check(example.name("hr") == "HR" && example.entries.first { $0.id == "hr" }?.aliases == ["근태 시스템"] && example.worklogWords == ["근무 캘린더"],
          "local: 예시 연결 파일 읽기")
}

// MARK: Jira 역할 파이프라인
extension PipelineState.Arrival {
    var isSkip: Bool { if case .skip = self { return true } else { return false } }
}

actor PipelineScript {
    var answers: [PipelineRole: [String]]
    var prompts: [(PipelineRole, String, ClaudeCommand.WriteAccess)] = []
    var helpers: [[String]] = []
    var sessions: [String: String] = [:]
    var failPush = false
    init(_ answers: [PipelineRole: [String]]) { self.answers = answers }
    var runs: [(String?, String?)] = []
    var notices: [String] = []
    var failResume = false
    func noticed(_ title: String) { notices.append(title) }
    func setFailResume() { failResume = true }
    func answer(_ role: PipelineRole, _ prompt: String, _ access: ClaudeCommand.WriteAccess, resume: String?, fresh: String?) -> PipelineRoleResult {
        runs.append((resume, fresh))
        if failResume, resume == "s-cut" { return PipelineRoleResult(failure: "No conversation found") }
        prompts.append((role, prompt, access))
        guard var list = answers[role], !list.isEmpty else { return PipelineRoleResult(failure: "답 없음") }
        let text = list.removeFirst()
        answers[role] = list
        return PipelineRoleResult(text: text, sessionId: "s-\(role.rawValue)", usage: RunUsage(costUSD: 0.5))
    }
    func helper(_ args: [String]) -> (ok: Bool, output: String) {
        helpers.append(args)
        if args.first == "worktree-add" { return (true, "/tmp/acme-ws/.jira-auto-worktrees/ACME-1") }
        if args.first == "commit-push" { return failPush ? (false, "push rejected") : (true, "{\"commit\": \"abc\"}") }
        return (true, "ok")
    }
    func record(_ key: String, _ id: String) { sessions[key] = id }
    func session(_ key: String) -> String? { sessions[key] }
    func setFailPush() { failPush = true }
}

func nextBlock(_ next: String, _ extra: String = "") -> String {
    "답 본문\n```pokemate-next\n{\"next\": \"\(next)\", \"note\": \"\(next) 로 넘김\"\(extra)}\n```"
}

func pipelineIO(_ script: PipelineScript) -> PipelineIO {
    PipelineIO(runRole: { role, prompt, resume, fresh, access in await script.answer(role, prompt, access, resume: resume, fresh: fresh) },
               helper: { args in await script.helper(args) },
               diff: { _ in "diff --git a/x b/x" },
               sessionId: { key in await script.session(key) },
               recordSession: { key, id, _, _ in await script.record(key, id) },
               save: { _ in },
               notice: { title, _ in await script.noticed(title) })
}

do {
    let map = DomainMap.parse(Data(#"{"acme": {"repos": ["acme-sample-service", " acme-sample-web "]}, "bad:name": {"repos": ["x"]}}"#.utf8))
    check(map.domain(for: "acme-sample-web") == "acme" && map.domain(for: "sample-tool") == "sample-tool" && map.domains["bad:name"] == nil,
          "pipeline: 도메인 지도 — 등록된 레포는 도메인, 없으면 레포 이름")
    check(PipelineRole.split("acme:tester")! == ("acme", .tester) && PipelineRole.split("acme-sample-service") == nil
          && PipelineRole.sessionKey(domain: "acme", role: .dev) == "acme:dev", "pipeline: 세션 키는 {도메인}:{역할}")
    let two = "```pokemate-next\n{\"next\": \"dev\"}\n```\n끝\n```pokemate-next\n{\"next\": \"Tester\", \"note\": \"n\", \"create\": [{\"branch\": \"feature/A-1\", \"from\": \"origin/main\"}]}\n```"
    let parsed = PipelineNext.extract(from: two)
    check(parsed?.kind == .tester && parsed?.note == "n" && parsed?.create == [PipelineBranch(branch: "feature/A-1", from: "origin/main")],
          "pipeline: 다음 단계 블록은 마지막 것을 읽음")
    check(PipelineNext.extract(from: "```pokemate-next\n{\"next\": \"deploy\"}\n```") == nil && PipelineNext.extract(from: "```pokemate-next\nnot json\n```") == nil
          && PipelineNext.extract(from: "블록 없음") == nil, "pipeline: 모르는 next·깨진 JSON·블록 없음은 nil")

    let table = PipelineAccess.parse(Data(#"{"pm": {"tools": ["Bash(h jira-get:*)", "Bash(h commit-push:*)", "Edit(//x/**)"], "dirs": ["~/w"]}, "tester": {"tools": ["Bash(h run-tests:*)"]}}"#.utf8), home: "/home/u")
    let pmAccess = PipelineAccess.access(for: .pm, worktree: "/w/A-1", table: table)
    check(pmAccess.tools == ["Bash(h jira-get:*)"] && pmAccess.dirs == ["/home/u/w"], "pipeline: 권한 파일 — push 명령·PM 의 Edit 는 빠짐")
    let devAccess = PipelineAccess.access(for: .dev, worktree: "/w/A-1", table: PipelineAccess.defaults(helper: "h", workDir: "/ws", references: "/refs", pipelines: "/p"))
    check(devAccess.tools.contains("Edit(//w/A-1/**)") && devAccess.tools.contains("Write(//w/A-1/**)") && devAccess.dirs.contains("/w/A-1")
          && !devAccess.tools.contains { $0.contains("jira-comment") || $0.contains("commit-push") }, "pipeline: 개발은 W 안만 수정, Jira 쓰기·push 없음")
    let defaults = PipelineAccess.defaults(helper: "h", workDir: "/ws", references: "/refs", pipelines: "/p")
    let testerAccess = PipelineAccess.access(for: .tester, worktree: "/w/A-1", table: defaults)
    let reviewerAccess = PipelineAccess.access(for: .reviewer, worktree: "/w/A-1", table: defaults)
    check(testerAccess.tools == ["Bash(h run-tests:*)", "Bash(h git-read:*)"] && reviewerAccess.tools.contains("Skill(code-review)")
          && !reviewerAccess.tools.contains { $0.hasPrefix("Edit(") } && reviewerAccess.dirs.contains("/p"), "pipeline: 테스터·리뷰어는 읽기·실행만")
    let reviewerArgs = ClaudeCommand.arguments(sessionId: nil, mcpConfigPath: "/c.json", systemPrompt: "s", writeTools: reviewerAccess.tools)
    let testerArgs = ClaudeCommand.arguments(sessionId: nil, mcpConfigPath: "/c.json", systemPrompt: "s", writeTools: testerAccess.tools)
    let devArgs = ClaudeCommand.arguments(sessionId: nil, mcpConfigPath: "/c.json", systemPrompt: "s", writeTools: devAccess.tools)
    check(reviewerArgs[reviewerArgs.firstIndex(of: "--tools")! + 1] == "Read,Grep,Glob,Bash,Skill"
          && testerArgs[testerArgs.firstIndex(of: "--tools")! + 1] == "Read,Grep,Glob,Bash"
          && devArgs[devArgs.firstIndex(of: "--tools")! + 1] == "Read,Grep,Glob,Bash,Edit,Write", "pipeline: 역할별 --tools 강제")

    let dir = FileManager.default.temporaryDirectory.appendingPathComponent("pm-pipe-\(UUID().uuidString)")
    let config = PipelineConfig(prompts: [.pm: "PM 지시", .dev: "개발 지시", .tester: "테스터 지시", .reviewer: "리뷰어 지시"], common: "공통",
                                access: defaults, dir: dir)
    let pmDev = nextBlock("dev", #", "repo": "acme-sample-service", "branch": "feature/ACME-1", "create": [{"branch": "feature/ACME-1", "from": "origin/main"}], "message": "feat: sample change""#)
    let story = PipelineScript([
        .pm: [pmDev, nextBlock("reviewer"), nextBlock("reviewer"), nextBlock("stop")],
        .dev: [nextBlock("tester"), nextBlock("tester"), nextBlock("tester")],
        .tester: [nextBlock("dev"), nextBlock("pm"), nextBlock("pm")],
        .reviewer: [nextBlock("dev"), nextBlock("push")],
    ])
    let start = PipelineState(key: "ACME-1", title: "sample", event: "다음 이벤트가 생겼다.\n- ACME-1", domain: "acme", repo: "acme-sample-service", timeoutMinutes: 30, now: Date())
    let done = await JiraPipeline.run(start, io: pipelineIO(story), config: config)
    let helpers = await story.helpers
    let prompts = await story.prompts
    check(done.stage == .done && done.testerBacks == 1 && done.reviewerBacks == 1 && done.pushed != nil,
          "pipeline: Story 핑퐁 — 테스터·리뷰어 되돌림 한 번씩 뒤 push, 끝")
    let storyNotices = await story.notices
    let storyRuns = await story.runs
    check(helpers.filter { $0.first == "event" }.count == 2 && helpers.filter { $0.first == "event" }.allSatisfy { $0.contains("changed") && $0.contains("핑퐁") }
          && storyNotices == ["핑퐁 테스터→개발 1/2 · ACME-1", "핑퐁 리뷰어→개발 1/2 · ACME-1"],
          "pipeline: 되돌림마다 Teams(event changed)·패널 알림")
    check(storyRuns[0].0 == nil && storyRuns[0].1 != nil && storyRuns[5].0 == "s-pm" && storyRuns[5].1 == nil && done.inflight == nil,
          "pipeline: 새 역할 세션은 id 를 미리 정해 실행, 이어 쓰기는 --resume, 끝나면 실행 중 기록 지움")
    let nonEvent = helpers.filter { $0.first != "event" }
    check(nonEvent.map { $0.first ?? "" } == ["create-branch", "worktree-add", "commit-push"]
          && nonEvent[2] == ["commit-push", "--worktree", "/tmp/acme-ws/.jira-auto-worktrees/ACME-1", "--message", "feat: sample change"],
          "pipeline: 브랜치·작업 폴더·push 는 PokeMate 가 직접 실행")
    check(prompts.map(\.0) == [.pm, .dev, .tester, .dev, .tester, .pm, .reviewer, .dev, .tester, .pm, .reviewer, .pm], "pipeline: 역할 순서")
    let devPrompt = prompts[1].1
    check(devPrompt.contains("/tmp/acme-ws/.jira-auto-worktrees/ACME-1") && devPrompt.contains("dev 로 넘김") && devPrompt.contains("공통")
          && devPrompt.contains("개발 지시") && prompts[1].2.tools.contains("Edit(//tmp/acme-ws/.jira-auto-worktrees/ACME-1/**)"),
          "pipeline: 개발 프롬프트에 W·앞 단계 말·공통 지시, 권한은 W 수정")
    check(prompts[2].1.contains("## PM 개발 지시서\ndev 로 넘김") && prompts[6].1.contains("## PM 개발 지시서") && !prompts[5].1.contains("## PM 개발 지시서"),
          "pipeline: PM 개발 지시서는 개발·테스터·리뷰어에게 전달")
    let promptDir = FileManager.default.temporaryDirectory.appendingPathComponent("pm-prompts-\(UUID().uuidString)")
    try? FileManager.default.createDirectory(at: promptDir, withIntermediateDirectories: true)
    try? "H = `{{helper}}`".write(to: promptDir.appendingPathComponent("jira-common.md"), atomically: true, encoding: .utf8)
    try? "dev".write(to: promptDir.appendingPathComponent("jira-dev.md"), atomically: true, encoding: .utf8)
    let loaded = PipelineConfig.load(promptsDir: promptDir, access: defaults, dir: dir, helper: "uv --directory /x run pokemate auto")
    check(loaded.common == "H = `uv --directory /x run pokemate auto`" && loaded.prompts[.dev] == "dev" && loaded.prompts[.pm] == nil,
          "pipeline: 역할 프롬프트 파일 읽기, {{helper}} 바꾸기")
    try? FileManager.default.removeItem(at: promptDir)
    check(prompts[6].1.contains("ACME-1.diff") && prompts[3].1.contains("되돌림: 테스터→개발 1/2") && prompts[0].1.contains("\"repo\""),
          "pipeline: 리뷰어는 diff 파일, 되돌림 횟수 전달")
    check(prompts[11].1.contains("커밋·push 했다") && prompts[11].1.contains("next 는 다음 중 하나: stop") && !prompts[0].2.tools.contains { $0.contains("commit-push") },
          "pipeline: push 뒤 PM 은 stop 만, 어떤 역할에도 push 권한 없음")
    let storySessions = await story.sessions
    check(storySessions == ["acme:pm": "s-pm", "acme:dev": "s-dev", "acme:tester": "s-tester", "acme:reviewer": "s-reviewer"]
          && prompts[5].1.contains(JiraPipeline.resumeNote) && !prompts[0].1.contains(JiraPipeline.resumeNote) && abs(done.totalCost - 6.0) < 0.001,
          "pipeline: 역할 세션은 {도메인}:{역할} 로 기록·이어 쓰기, 역할별 비용 합산")

    let limit = PipelineScript([
        .pm: [pmDev, nextBlock("stop", #", "waiting": true"#)],
        .dev: [nextBlock("tester"), nextBlock("tester"), nextBlock("tester")],
        .tester: [nextBlock("dev"), nextBlock("dev"), nextBlock("dev")],
    ])
    let limited = await JiraPipeline.run(start, io: pipelineIO(limit), config: config)
    let limitPrompts = await limit.prompts
    let limitNotices = await limit.notices
    check(limitNotices.count == 3 && limitNotices.last == "핑퐁 상한 초과 · ACME-1", "pipeline: 상한 초과도 알림")
    check(limited.stage == .waiting && limited.testerBacks == 3 && limitPrompts.last?.0 == .pm
          && limitPrompts.last?.1.contains("되돌림이 2번을 넘어") == true && limitPrompts.last?.1.contains("next 는 다음 중 하나: stop") == true,
          "pipeline: 되돌림 상한 2번 넘으면 PM 이 멈추고 답을 기다림")
    var waiting = limited
    waiting.resume(now: Date())
    check(waiting.stage == .pm && waiting.testerBacks == 0 && !waiting.mustStop, "pipeline: 확인 필요 답이 오면 PM 부터 다시, 되돌림 0")

    let broken = PipelineScript([.pm: ["블록 없는 답"]])
    let stopped = await JiraPipeline.run(start, io: pipelineIO(broken), config: config)
    let brokenHelpers = await broken.helpers
    check(stopped.stage == .stopped && brokenHelpers.map { $0.first ?? "" } == ["event", "jira-state"] && brokenHelpers[0].contains("blocked"),
          "pipeline: 블록이 없으면 멈추고 blocked 알림·jira-state")

    let early = PipelineScript([.pm: [nextBlock("reviewer")]])
    let earlyState = await JiraPipeline.run(start, io: pipelineIO(early), config: config)
    check(earlyState.stage == .stopped && earlyState.reason?.contains("reviewer") == true, "pipeline: 테스트 통과 전엔 PM 이 리뷰로 못 넘김")

    let task = PipelineScript([.pm: [nextBlock("stop")]])
    let taskState = await JiraPipeline.run(PipelineState(key: "ACME-2", title: "ops", event: "e", domain: WorkSessionBook.operations, repo: nil, timeoutMinutes: 30, now: Date()),
                                           io: pipelineIO(task), config: config)
    let taskHelpers = await task.helpers
    let taskPrompts = await task.prompts
    check(taskState.stage == .done && taskHelpers.isEmpty && taskPrompts.count == 1, "pipeline: Task 는 PM 하나로 끝")

    let failing = PipelineScript([
        .pm: [pmDev, nextBlock("reviewer"), nextBlock("stop")],
        .dev: [nextBlock("tester")], .tester: [nextBlock("pm")], .reviewer: [nextBlock("push")],
    ])
    await failing.setFailPush()
    let failed = await JiraPipeline.run(start, io: pipelineIO(failing), config: config)
    let failingPrompts = await failing.prompts
    check(failed.stage == .done && failed.pushed == nil && failingPrompts.last?.1.contains("push 하지 못했다") == true,
          "pipeline: push 실패면 PM 이 개발 중단 처리")

    let noPrompt = PipelineConfig(prompts: [.pm: "x"], common: "", access: defaults, dir: dir)
    let missing = await JiraPipeline.run(start, io: pipelineIO(PipelineScript([.pm: [pmDev]])), config: noPrompt)
    check(missing.stage == .stopped && missing.reason?.contains("jira-dev.md") == true, "pipeline: 역할 프롬프트 파일이 없으면 멈춤")

    var cut = start
    cut.stage = .dev
    cut.repo = "acme-sample-service"
    cut.worktree = "/tmp/acme-ws/.jira-auto-worktrees/ACME-1"
    cut.inflight = PipelineState.Inflight(role: "dev", sessionId: "s-cut", startedAt: Date())
    let resumeScript = PipelineScript([.dev: [nextBlock("pm")], .pm: [nextBlock("stop")]])
    let resumed = await JiraPipeline.run(cut, io: pipelineIO(resumeScript), config: config)
    let resumeRuns = await resumeScript.runs
    let resumePrompts = await resumeScript.prompts
    check(resumed.stage == .done && resumeRuns.first?.0 == "s-cut" && resumePrompts.first?.1.hasPrefix(JiraPipeline.interruptedNote) == true,
          "pipeline: 끊긴 역할 실행은 그 세션을 --resume 해 남은 것만 이어 감")
    let lostScript = PipelineScript([.dev: [nextBlock("pm")], .pm: [nextBlock("stop")]])
    await lostScript.setFailResume()
    let lost = await JiraPipeline.run(cut, io: pipelineIO(lostScript), config: config)
    let lostRuns = await lostScript.runs
    let lostPrompts = await lostScript.prompts
    check(lost.stage == .done && lostRuns.count == 3 && lostRuns[0].0 == "s-cut" && lostRuns[1].0 == nil && lostRuns[1].1 != nil
          && lostPrompts.first?.1.hasPrefix(JiraPipeline.interruptedNote) == true, "pipeline: 끊긴 세션이 없으면 새 세션으로, 이미 한 일 확인 지시와 함께")
    let quitIO = PipelineIO(runRole: { _, _, _, _, _ in PipelineRoleResult(interrupted: true) },
                            helper: { _ in (true, "") }, diff: { _ in "" }, sessionId: { _ in nil }, recordSession: { _, _, _, _ in }, save: { _ in })
    let quit = await JiraPipeline.run(cut, io: quitIO, config: config)
    check(quit.stage == .dev && quit.inflight?.role == "dev" && quit.reason == nil, "pipeline: 앱 종료로 끊기면 멈춤 처리 없이 실행 중 기록을 남김")
    let fresh = ClaudeCommand.arguments(sessionId: nil, mcpConfigPath: "/c.json", systemPrompt: "s", newSessionId: "abc")
    let both = ClaudeCommand.arguments(sessionId: "old", mcpConfigPath: "/c.json", systemPrompt: "s", newSessionId: "abc")
    check(fresh.contains("--session-id") && fresh.contains("abc") && both.contains("--resume") && !both.contains("--session-id"),
          "pipeline: 새 실행은 --session-id, 이어 쓰기는 --resume 만")

    let kmap = DomainMap.parse(Data(#"{"acme": {"repos": ["acme-sample-service"], "knowledge": ["acme-kb", "~/.claude/references/acme-wiki"]}, "beta": {"repos": ["beta-api"], "knowledge": []}, "default": {"knowledge": ["sample-notes"]}}"#.utf8))
    check(kmap.domains["default"] == nil && kmap.knowledgePaths(for: "acme", home: "/u") == ["/u/Workspace/acme-kb", "/u/.claude/references/acme-wiki"]
          && kmap.knowledgePaths(for: "beta", home: "/u").isEmpty && kmap.knowledgePaths(for: "sample-tool", home: "/u") == ["/u/Workspace/sample-notes"]
          && kmap.knowledgeText(for: "acme", home: "/u") == "   - `~/Workspace/acme-kb`\n   - `~/.claude/references/acme-wiki`"
          && kmap.knowledgeText(for: "beta", home: "/u") == "   (정한 지식 저장소 없음)", "knowledge: 도메인별 지식 저장소, 없으면 default")
    var kconfig = PipelineConfig(prompts: [.pm: "지식:\n{{knowledge}}", .dev: "d"], common: "c {{knowledge}}", access: defaults, dir: dir, domains: kmap, home: "/u")
    kconfig.maxSteps = 1
    let kscript = PipelineScript([.pm: [nextBlock("stop")]])
    _ = await JiraPipeline.run(start, io: pipelineIO(kscript), config: kconfig)
    let kprompts = await kscript.prompts
    check(kprompts.first?.1.contains("지식:\n   - `~/Workspace/acme-kb`") == true && kprompts.first?.1.contains("{{knowledge}}") == false
          && kprompts.first?.2.dirs.contains("/u/Workspace/acme-kb") == true, "knowledge: 프롬프트 자리표시 치환, 읽기 폴더에 추가")
    check(kprompts.first?.1.contains("점검표 `/u/.claude/references/notes/acme/checklist.md` (없음)") == true
          && kprompts.first?.1.contains("$H domain-note --domain acme --init") == true
          && kprompts.first?.1.contains("--source \"ACME-1\"") == true
          && kprompts.first?.2.dirs.contains("/u/.claude/references/notes") == true, "notes: 역할 프롬프트에 공유 점검표·노트 안내, 노트 폴더는 읽기 폴더")
    let review = defaults[.reviewer]!
    check(review.tools.contains("Skill(domain-review)") && review.tools.contains("Skill(code-review)") && review.tools.contains("Bash(h domain-note:*)")
          && review.dirs.contains("/refs") && defaults[.dev]!.dirs.contains("/refs") && defaults[.pm]!.tools.contains("Bash(h domain-note:*)")
          && !defaults[.dev]!.tools.contains("Bash(h domain-note:*)") && !defaults[.tester]!.dirs.contains("/refs"),
          "notes: 리뷰어는 domain-review 스킬과 domain-note, 개발은 읽기만, 테스터는 그대로")
    var unsure = start
    unsure.stage = .reviewer
    unsure.testsPassed = true
    let unsureOutcome = unsure.apply(role: .reviewer, next: PipelineNext(kind: .pm, note: "미확인 2건"), summary: "", now: Date())
    check(unsureOutcome == .next && unsure.stage == .pm && unsure.reviewerBacks == 0 && unsure.note == "미확인 2건",
          "pipeline: 리뷰어가 미확인만 있으면 PM 으로 (되돌림 횟수는 안 셈)")

    let bc = PipelineScript([.pm: [nextBlock("stop", #", "note": "프로젝트 단위 개발 건: 자동 처리 안 함""#)]])
    let bcState = await JiraPipeline.run(start, io: pipelineIO(bc), config: config)
    let bcHelpers = await bc.helpers
    let bcNotices = await bc.notices
    let bcPrompts = await bc.prompts
    check(bcState.stage == .done && bcHelpers.isEmpty && bcNotices.isEmpty && bcPrompts.count == 1,
          "pipeline: PM 이 처리 안 하고 stop 하면 정상 종료 (앱은 blocked 알림을 따로 보내지 않음, 알림은 PM 이 한 번)")
    check(PipelineState.arrival(of: nil) == .start && PipelineState.arrival(of: limited) == .resume
          && PipelineState.arrival(of: bcState) == .skip("이미 처리함: ACME-1 · done")
          && PipelineState.arrival(of: cut) == .skip("이미 처리 중: ACME-1 · dev")
          && PipelineState.arrival(of: stopped).isSkip, "pipeline: 끝난 티켓은 다시 잡지 않음, 답 기다리던 티켓만 이어 감")

    var shown = start
    shown.stage = .tester
    shown.testerBacks = 2
    check(shown.statusText == "ACME-1 · tester 2/2", "pipeline: 작업 탭 단계 표시")
    done.save(in: dir)
    let reloaded = PipelineState.load(key: "ACME-1", in: dir)
    check(reloaded?.stage == done.stage && reloaded?.history.count == done.history.count && reloaded?.worktree == done.worktree
          && reloaded?.costs == done.costs && PipelineState.loadAll(in: dir).count == 1, "pipeline: 진행 상태 저장·읽기")
    try? FileManager.default.removeItem(at: dir)
}

// MARK: 홈 스킬 공유
do {
    let fm = FileManager.default
    let root = fm.temporaryDirectory.appendingPathComponent("pm-skills-\(UUID().uuidString)")
    let home = root.appendingPathComponent("home").path
    let skills = HomeSkills.source(home: home)
    for name in ["sample-cli", "acme-jira"] {
        try? fm.createDirectory(at: skills.appendingPathComponent(name), withIntermediateDirectories: true)
        try? "x".write(to: skills.appendingPathComponent(name).appendingPathComponent("SKILL.md"), atomically: true, encoding: .utf8)
    }
    try? fm.createDirectory(at: skills.appendingPathComponent("synced/abc/docs"), withIntermediateDirectories: true)
    try? "x".write(to: skills.appendingPathComponent("synced/abc/docs/SKILL.md"), atomically: true, encoding: .utf8)
    let target = root.appendingPathComponent("work/.claude/skills")
    try? fm.createDirectory(at: target.appendingPathComponent("acme-jira"), withIntermediateDirectories: true)
    let first = HomeSkills.sync(enabled: true, home: home, target: target)
    check(HomeSkills.names(home: home) == ["acme-jira", "sample-cli"] && first.linked == ["sample-cli"]
          && (try? fm.destinationOfSymbolicLink(atPath: target.appendingPathComponent("sample-cli").path)) == skills.path + "/sample-cli"
          && (try? fm.destinationOfSymbolicLink(atPath: target.appendingPathComponent("acme-jira").path)) == nil,
          "skills: 홈 스킬을 링크, 하위 폴더 동기화 사본은 빼고 메이트 쪽 같은 이름 폴더는 그대로 둠")
    try? fm.createDirectory(at: skills.appendingPathComponent("sample-new"), withIntermediateDirectories: true)
    try? "x".write(to: skills.appendingPathComponent("sample-new/SKILL.md"), atomically: true, encoding: .utf8)
    try? fm.removeItem(at: skills.appendingPathComponent("sample-cli"))
    let second = HomeSkills.sync(enabled: true, home: home, target: target)
    check(second.linked == ["sample-new"] && second.removed == ["sample-cli"], "skills: 새 스킬은 링크, 사라진 스킬 링크는 지움")
    let off = HomeSkills.sync(enabled: false, home: home, target: target)
    check(off.removed == ["sample-new"] && fm.fileExists(atPath: target.appendingPathComponent("acme-jira").path), "skills: 끄면 홈 링크만 지움")
    let old = try? JSONDecoder().decode(AppSettings.self, from: Data("{}".utf8))
    check(old?.shareHomeSkills == true, "skills: 기본은 켬")
    try? fm.removeItem(at: root)
}

// MARK: 물결표 하나는 글자로
do {
    func struck(_ raw: String) -> String {
        let escaped = MarkdownText.escapingSingleTildes(raw)
        guard let a = try? AttributedString(markdown: escaped, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace)) else { return "!" }
        return a.runs.filter { $0.inlinePresentationIntent?.contains(.strikethrough) == true }.map { String(a[$0.range].characters) }.joined()
    }
    let plain = "근무시간(9~18시)만 잡아도 … 1. 평일 9~18시"
    check(struck(plain) == "", "markdown: 물결표 하나가 두 번 나와도 취소선 없음")
    check(struck("이건 ~~지움~~ 이야") == "지움", "markdown: 물결표 두 개 쌍은 취소선")
    check(MarkdownText.escapingSingleTildes("`a~b` 와 9~18") == "`a~b` 와 9\\~18" && MarkdownText.escapingSingleTildes("이미 \\~ 됨") == "이미 \\~ 됨",
          "markdown: 코드 안·이미 이스케이프된 물결표는 그대로")
}

// MARK: 설정 층 (shared·local)
do {
    let fm = FileManager.default
    let root = fm.temporaryDirectory.appendingPathComponent("pm-layers-\(UUID().uuidString)")
    try? fm.createDirectory(at: root, withIntermediateDirectories: true)
    let alarmsFile = root.appendingPathComponent("alarms.json")
    var cfg = AppSettings.default
    cfg.reportHour = 9
    check(AlarmStore.load(settings: cfg, file: alarmsFile, ready: false).isEmpty, "layers: local 이 없으면 알람은 빈 목록(기본 알람도 안 만듦)")
    AlarmStore.save([Alarm(title: "t", prompt: "", hour: 9, minute: 0, repeatRule: .daily)], file: alarmsFile, ready: false)
    check(!fm.fileExists(atPath: alarmsFile.path), "layers: local 이 없으면 저장하지 않음")
    AlarmStore.save([Alarm(title: "t", prompt: "", hour: 9, minute: 0, repeatRule: .daily)], file: alarmsFile, ready: true)
    check(AlarmStore.load(settings: cfg, file: alarmsFile, ready: true).map(\.title) == ["t"], "layers: local 이 있으면 저장·읽기")

    let shared = root.appendingPathComponent("shared"), local = root.appendingPathComponent("local")
    try? fm.createDirectory(at: shared, withIntermediateDirectories: true)
    try? fm.createDirectory(at: local, withIntermediateDirectories: true)
    try? "일반 지시".write(to: shared.appendingPathComponent("CLAUDE.md"), atomically: true, encoding: .utf8)
    check(Rules.instructions(shared: shared, local: local).hasSuffix("일반 지시"), "layers: local CLAUDE.md 가 없으면 shared 만")
    try? "회사 지시".write(to: local.appendingPathComponent("CLAUDE.md"), atomically: true, encoding: .utf8)
    let both = Rules.instructions(shared: shared, local: local)
    check(both.contains("일반 지시\n\n회사 지시"), "layers: CLAUDE.md 는 shared 다음 local 순서로 붙임")

    let sj = Rules.File(url: AppPaths.sharedDir.appendingPathComponent("skills/jira.md"))
    let sh = Rules.File(url: AppPaths.sharedDir.appendingPathComponent("skills/hr.md"))
    let lj = Rules.File(url: AppPaths.localDir.appendingPathComponent("skills/jira.md"))
    let lc = Rules.File(url: AppPaths.localDir.appendingPathComponent("CLAUDE.md"))
    let sc = Rules.File(url: AppPaths.sharedDir.appendingPathComponent("CLAUDE.md"))
    let all = [sc, sj, sh, lc, lj]
    check(sj.layer == .shared && lj.layer == .local && Rules.overridden(all) == [sj.id]
          && Rules.effective(all).map(\.id) == [sc.id, sh.id, lc.id, lj.id], "layers: 같은 이름이면 local 이 이김, CLAUDE.md 는 둘 다")

    let perm = root.appendingPathComponent("settings.json")
    try? #"{"permissions": {"deny": ["Bash(rm:*)"], "allow": []}}"#.write(to: perm, atomically: true, encoding: .utf8)
    let merged = ClaudeCommand.installPermissions(at: perm, dataDir: URL(fileURLWithPath: "/d"), localDir: URL(fileURLWithPath: "/r/local"))
    let deny = ((try? JSONSerialization.jsonObject(with: Data(contentsOf: perm))) as? [String: Any]).flatMap { ($0["permissions"] as? [String: Any])?["deny"] as? [String] } ?? []
    check(merged && deny.first == "Bash(rm:*)" && deny.contains("Edit(//r/local/write-tools.json)") && !deny.contains("Edit(//r/local/alarms.json)") && deny.contains("Write(//r/local/domains.json)")
          && !ClaudeCommand.installPermissions(at: perm, dataDir: URL(fileURLWithPath: "/d"), localDir: URL(fileURLWithPath: "/r/local")),
          "layers: 기존 권한 파일에 local 설정 파일 쓰기 금지를 더함 (있던 것은 그대로)")
    try? fm.removeItem(at: root)
}

// MARK: 단가
do {
    func million(_ set: (inout TokenBucket) -> Void) -> TokenBucket { var b = TokenBucket(); set(&b); return b }
    let fable = [million { $0.input = 1_000_000 }, million { $0.output = 1_000_000 }, million { $0.cacheWrite = 1_000_000 }, million { $0.cacheRead = 1_000_000 }]
        .compactMap { ClaudePricing.cost(model: "claude-fable-5-2", bucket: $0) }
    let sonnet = [million { $0.input = 1_000_000 }, million { $0.output = 1_000_000 }, million { $0.cacheWrite = 1_000_000 }, million { $0.cacheRead = 1_000_000 }]
        .compactMap { ClaudePricing.cost(model: "claude-sonnet-5-5", bucket: $0) }
    check(fable.map { ($0 * 100).rounded() / 100 } == [8, 40, 10, 0.2] && sonnet.map { ($0 * 100).rounded() / 100 } == [2, 10, 2.5, 0.1],
          "pricing: Fable 5.2·Sonnet 5.5 단가 (백만 토큰당 입력·출력·캐시 쓰기·캐시 읽기)")
}

// MARK: 자동 처리 모델
do {
    let old = try? JSONDecoder().decode(AppSettings.self, from: Data(#"{"model": "fable", "effort": "medium"}"#.utf8))
    check(old?.automationModel == "fable" && old?.automationEffort == "medium", "model: 예전 설정은 자동 처리 모델로 그대로 이어짐")
    let split = try? JSONDecoder().decode(AppSettings.self, from: Data(#"{"model": "opus", "effort": "high", "automationModel": "sonnet", "automationEffort": "low"}"#.utf8))
    check(split?.model == "opus" && split?.automationModel == "sonnet" && split?.automationEffort == "low", "model: 대화·자동 처리 모델은 따로 저장")
}

// MARK: 도메인 리뷰어
do {
    let target = ReviewTarget.parse(url: "https://git.example.com/acme/sample-web/pull/280")
    check(target?.fullName == "acme/sample-web" && target?.number == 280 && target?.label == "sample-web#280"
          && ReviewTarget.parse(url: "https://git.example.com/acme/sample-web/issues/3") == nil, "review: PR 주소 읽기")
    let map = DomainMap.parse(Data(#"{"acme": {"repos": ["acme-sample-service"], "description": "쿠폰·할인", "keywords": ["쿠폰", "coupon"], "knowledge": ["acme-kb"]}, "beta": {"repos": ["beta-api"]}, "default": {"knowledge": ["sample-notes"]}}"#.utf8))
    check(map.descriptions["acme"] == "쿠폰·할인" && map.keywords["acme"] == ["쿠폰", "coupon"] && map.keywords["beta"] == nil, "review: 도메인 설명·키워드 읽기")
    let inRepo = ReviewTarget(owner: "acme", repo: "acme-sample-service", number: 1, url: "u")
    let explicit = DomainReview.known(inRepo, map: map, book: ReviewDomainBook(repos: ["acme/acme-sample-service": ReviewDomainChoice(domain: "beta", why: "x", at: Date())]))
    check(explicit?.domain == "acme" && explicit?.source == .explicit, "review: domains.json repos 가 예전 기록보다 이김")
    let other = ReviewTarget(owner: "acme", repo: "sample-web", number: 2, url: "u")
    var book = ReviewDomainBook()
    check(DomainReview.known(other, map: map, book: book) == nil, "review: 처음 보는 레포는 도메인 정하기로")
    book.repos[other.fullName] = ReviewDomainChoice(domain: "acme", why: "쿠폰함 화면", at: Date())
    check(DomainReview.known(other, map: map, book: book)?.source == .learned
          && DomainReview.known(other, map: map, book: ReviewDomainBook(repos: [other.fullName: ReviewDomainChoice(domain: "gone", why: "", at: Date())])) == nil,
          "review: 정해 둔 기록을 쓰고, 없어진 도메인 기록은 무시")
    let chooser = DomainReview.choosePrompt(target: other, map: map, helper: "H")
    check(chooser.contains("- acme · 쿠폰·할인 · 키워드: 쿠폰, coupon · 레포: acme-sample-service") && chooser.contains("- default ·")
          && chooser.contains("H gh-get repos/acme/sample-web/pulls/2/files"), "review: 도메인 정하기 안내에 목록·설명·키워드")
    check(DomainReview.extract(from: "답\n```pokemate-domain\n{\"domain\": \"acme\", \"why\": \"쿠폰함\"}\n```", map: map)! == ("acme", "쿠폰함")
          && DomainReview.extract(from: "```pokemate-domain\n{\"domain\": \"nope\"}\n```", map: map) == nil
          && DomainReview.extract(from: "```pokemate-domain\n{\"domain\": \"default\"}\n```", map: map)?.domain == "default",
          "review: 도메인 블록 읽기 (없는 도메인은 거절, default 는 됨)")
    let present: [String: Int] = ["/n/acme/checklist.md": 20, "/n/acme/notes.md": 12]
    let notes = DomainNotes(root: "/n", lines: { present[$0] })
    let empty = DomainNotes(root: "/n", lines: { _ in nil })
    let first = DomainReview.reviewPrompt(target: other, domain: "acme", why: "쿠폰함", source: .chosen, firstRun: true, notes: empty, helper: "H", context: "본문")
    let again = DomainReview.reviewPrompt(target: other, domain: "acme", why: "쿠폰함", source: .learned, firstRun: false, notes: notes, helper: "H", context: "본문")
    let unsure = DomainReview.reviewPrompt(target: other, domain: "default", why: "못 정함", source: .chosen, firstRun: true, notes: notes, helper: "H", context: "본문")
    check(DomainReview.sessionKey("acme") == "acme:reviewer"
          && first.contains("세션 acme:reviewer") && first.contains("첫 실행") && first.contains("H domain-note --domain acme --init") && first.hasSuffix("본문")
          && first.contains("`/n/acme/checklist.md` (없음)")
          && again.contains("새로 필요한 것만") && again.contains("`/n/acme/checklist.md` (20줄)") && again.contains("`/n/acme/notes.md` (12줄)")
          && !again.contains("--init") && again.contains("H gh-file --repo acme/sample-web")
          && again.contains("H domain-note --domain acme --kind {용어집|업무 규칙|장애|함정|결정|동작} --source \"sample-web#2\"") && again.contains("직접 고치지 않는다")
          && unsure.contains("도메인 미정, 점검표 없음") && !unsure.contains("domain-note")
          && !notes.promptLines(domain: "acme", helper: "H", source: "ACME-1", canWrite: false).contains("domain-note")
          && notes.promptLines(domain: "acme", helper: "H", source: "ACME-1", canWrite: false).contains("읽기만"),
          "review: 리뷰어 세션 안내 (첫 실행·이어 쓰기·공유 노트·init·gh-file·domain-note·default)")
    let rows = Data(#"[{"kind":"settings","id":"automation","domainNotesDir":"~/kb/notes"},{"id":"jira"}]"#.utf8)
    check(DomainNotes.root(fromConnections: rows, home: "/u") == "/u/kb/notes" && DomainNotes.root(fromConnections: nil, home: "/u") == "/u/.claude/references/notes"
          && DomainNotes.root(fromConnections: Data(#"[{"kind":"settings","id":"automation","domainNotesDir":" "}]"#.utf8), home: "/u") == "/u/.claude/references/notes",
          "notes: 노트 폴더는 automation 설정 domainNotesDir, 없으면 ~/.claude/references/notes")
    let mapped = DomainNotes(root: "/n", lines: { _ in 5 }, maps: { $0 == "/n/acme/maps" ? (1...22).map { "주제\($0)" } : [] })
    let mapLine = mapped.promptLines(domain: "acme", helper: "H", source: "ACME-1")
    check(mapLine.contains("- 지도: `/n/acme/maps/` 아래 주제1, ") && mapLine.contains(" 외 2개") && !mapLine.contains("주제22,")
          && !DomainNotes(root: "/n", lines: { _ in 5 }, maps: { _ in [] }).promptLines(domain: "acme", helper: "H", source: "ACME-1").contains("지도"),
          "notes: 지도가 있으면 주제 목록(최대 20개)을 안내, 없으면 줄 없음")
    let mapBase = FileManager.default.temporaryDirectory.appendingPathComponent("pokemate-maps-\(UUID().uuidString)")
    try? FileManager.default.createDirectory(at: mapBase, withIntermediateDirectories: true)
    for name in ["b.md", "a.md", ".hidden.md", "x.txt"] { FileManager.default.createFile(atPath: mapBase.appendingPathComponent(name).path, contents: Data()) }
    check(DomainNotes.listMaps(mapBase.path) == ["a", "b"] && DomainNotes.listMaps("/n/none").isEmpty, "notes: 지도 목록은 .md 만, 숨김 파일 제외")
    try? FileManager.default.removeItem(at: mapBase)
    let mapDefaults = PipelineAccess.defaults(helper: "h", workDir: "/ws", references: "/refs", pipelines: "/p")
    check(mapDefaults[.pm]?.tools.contains("Bash(h domain-map:*)") == true && mapDefaults[.reviewer]?.tools.contains("Bash(h domain-map:*)") == false,
          "notes: 지도는 PM 만 남긴다")
    let linkBase = FileManager.default.temporaryDirectory.appendingPathComponent("pokemate-notes-\(UUID().uuidString)")
    try? FileManager.default.createDirectory(at: linkBase.appendingPathComponent("real"), withIntermediateDirectories: true)
    try? FileManager.default.createSymbolicLink(at: linkBase.appendingPathComponent("link"), withDestinationURL: linkBase.appendingPathComponent("real"))
    check(DomainNotes.resolved(linkBase.appendingPathComponent("link").path).hasSuffix("/real") && DomainNotes.resolved("/n/none") == "/n/none",
          "notes: 노트 폴더가 심볼릭 링크면 실제 경로로 (Claude 권한은 실제 경로로 판단)")
    try? FileManager.default.removeItem(at: linkBase)
}

do {
    let raw = Data(#"[{"id": "sample-web-280", "url": "https://git.example.com/acme/sample-web/pull/280", "domain": "acme", "mustFind": []}, {"id": "", "url": "https://git.example.com/acme/x/pull/1"}, {"id": "bad", "url": "nope"}, {"id": "sample-docs-1", "url": "https://git.example.com/acme/sample-docs/pull/1"}]"#.utf8)
    let cases = ReviewEval.loadCases(raw)
    check(cases.map(\.id) == ["sample-web-280", "sample-docs-1"] && cases[0].domain == "acme" && cases[1].domain == nil, "eval: 문제 묶음 읽기 (id·주소 없는 것은 뺌)")
    let access = ReviewEval.access(helper: "H", dirs: ["/w", "/n", "/w"])
    check(access.dirs == ["/w", "/n"] && access.tools.contains("Bash(H gh-diff:*)") && access.tools.contains("Skill(domain-review)") && access.tools.contains("Skill(code-review)")
          && !access.tools.contains { $0.contains("gh-review") || $0.contains("event") || $0.contains("domain-note") || $0.contains("write-note") || $0.contains("review-state") || $0.hasPrefix("Edit(") || $0.hasPrefix("Write(") }
          && ReviewEval.environment(target: ReviewTarget.parse(url: cases[0].url)!) == ["POKEMATE_EVAL": "1", "POKEMATE_REVIEW_REPO": "acme/sample-web"],
          "eval: 읽기 명령과 스킬만 허용, 쓰기 막는 환경")
    let emap = DomainMap.parse(Data(#"{"acme": {"repos": ["sample-docs"]}}"#.utf8))
    let target = ReviewTarget.parse(url: cases[0].url)!
    check(ReviewEval.domain(for: cases[0], target: target, map: emap, book: ReviewDomainBook()).domain == "acme"
          && ReviewEval.domain(for: cases[1], target: ReviewTarget.parse(url: cases[1].url)!, map: emap, book: ReviewDomainBook()).source == .explicit
          && ReviewEval.domain(for: ReviewEvalCase(id: "x", url: cases[0].url, domain: "nope"), target: target, map: emap, book: ReviewDomainBook()).domain == "default",
          "eval: 도메인은 문제에 적은 것 → 기록 → default")
    let ep = ReviewEval.prompt(item: cases[0], target: target, domain: "acme", why: "평가", source: .chosen, notes: DomainNotes(root: "/n", lines: { _ in 3 }),
                               helper: "H", template: "리뷰 {{helper}} gh-diff\n{{knowledge}}", knowledge: "   - `kb`")
    check(ep.contains("리뷰 H gh-diff") && ep.contains("   - `kb`") && !ep.contains("{{") && ep.contains("```pokemate-eval") && ep.contains("링크: \(cases[0].url)")
          && ep.contains("세션 acme:reviewer") && ep.contains("review-state 저장"), "eval: 실제 리뷰와 같은 프롬프트 + 평가 끝맺음")
    let transcript = [#"{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Skill","input":{"skill":"domain-review"}}]}}"#,
                      #"{"type":"user","message":{"content":"x"}}"#,
                      #"{"type":"assistant","message":{"content":[{"type":"text","text":"a"},{"type":"tool_use","name":"Skill","input":{"skill":"code-review","args":"high"}},{"type":"tool_use","name":"Bash","input":{}}]}}"#].joined(separator: "\n")
    check(ReviewEval.skillsUsed(transcript: transcript) == ["domain-review", "code-review"], "eval: 기록에서 부른 스킬 찾기 (스킬 안에서 부른 것 포함)")
}

print("passed: \(passed), failed: \(failures)")
exit(failures == 0 ? 0 : 1)
