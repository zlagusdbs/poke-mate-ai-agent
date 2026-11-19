import Foundation
import PokeMateCore

let settings = AppPaths.loadSettings()
var rest = Array(CommandLine.arguments.dropFirst())
var only: [String] = []
var outDir: URL?
var casesFile = URL(fileURLWithPath: settings.repoDir).appendingPathComponent("local/eval/reviews.json")
while !rest.isEmpty {
    let arg = rest.removeFirst()
    switch arg {
    case "--case": if !rest.isEmpty { only.append(rest.removeFirst()) }
    case "--out": if !rest.isEmpty { outDir = URL(fileURLWithPath: rest.removeFirst()) }
    case "-h", "--help":
        print("usage: PokeMateEval [cases.json] [--case id]... [--out 결과 폴더]")
        exit(0)
    default: casesFile = URL(fileURLWithPath: arg)
    }
}

func line(_ text: String) {
    let f = DateFormatter()
    f.dateFormat = "HH:mm:ss"
    print("[\(f.string(from: Date()))] \(text)")
    fflush(stdout)
}

guard let data = try? Data(contentsOf: casesFile) else {
    print("문제 묶음을 읽지 못했어요: \(casesFile.path)")
    exit(2)
}
let cases = ReviewEval.loadCases(data).filter { only.isEmpty || only.contains($0.id) }
guard !cases.isEmpty else {
    print("돌릴 문제가 없어요: \(casesFile.path)")
    exit(2)
}
guard let templateFile = Rules.resolvedPrompt("github-review"), let template = try? String(contentsOf: templateFile, encoding: .utf8) else {
    print("github-review 프롬프트를 찾지 못했어요")
    exit(2)
}
let stamp: String = {
    let f = DateFormatter()
    f.dateFormat = "yyyyMMdd-HHmmss"
    return f.string(from: Date())
}()
let out = outDir ?? URL(fileURLWithPath: settings.repoDir).appendingPathComponent("local/eval/runs/\(stamp)", isDirectory: true)
try? FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)
let helper = PipelineAccess.helper(settings: settings)
let map = DomainMap.load()
let book = ReviewDomainBook.load()
let notes = DomainNotes.load()
let home = NSHomeDirectory()
line("문제 \(cases.count)개 · 프롬프트 \(templateFile.path) · 결과 \(out.path)")

for item in cases {
    guard let target = ReviewTarget.parse(url: item.url) else { continue }
    let chosen = ReviewEval.domain(for: item, target: target, map: map, book: book)
    let access = ReviewEval.access(helper: helper, dirs: [home + "/Workspace", home + "/.claude/references", notes.root] + map.knowledgePaths(for: chosen.domain))
    let prompt = ReviewEval.prompt(item: item, target: target, domain: chosen.domain, why: chosen.why, source: chosen.source, notes: notes,
                                   helper: helper, template: template, knowledge: map.knowledgeText(for: chosen.domain))
    let sessionId = UUID().uuidString.lowercased()
    line("\(item.id) 시작 · 도메인 \(chosen.domain) · 세션 \(sessionId)")
    let started = Date()
    let runner = ClaudeRunner()
    let response = await runner.run(prompt: prompt, sessionId: nil, settings: settings, writeTools: access.tools, writeDirs: access.dirs,
                                     timeout: 2400, newSessionId: sessionId, automation: true,
                                     extraEnvironment: ReviewEval.environment(target: target))
    let seconds = Date().timeIntervalSince(started)
    var text = ""
    var ok = false
    switch response {
    case .success(let answer, _):
        text = answer
        ok = true
    case .failure(let failure):
        text = "실행 실패: \(failure.message)"
    }
    let transcript = ReviewEval.transcriptFile(configDir: AppPaths.claudeConfigDir, sessionId: sessionId)
        .flatMap { try? String(contentsOf: $0, encoding: .utf8) } ?? ""
    let skills = ReviewEval.skillsUsed(transcript: transcript)
    let cost = runner.lastUsage?.costUSD ?? 0
    try? text.write(to: out.appendingPathComponent("\(item.id).md"), atomically: true, encoding: .utf8)
    let meta = ReviewEval.meta(costUSD: cost, seconds: seconds, skills: skills, sessionId: sessionId, usage: runner.lastUsage, ok: ok, domain: chosen.domain)
    if let json = try? JSONSerialization.data(withJSONObject: meta, options: [.prettyPrinted, .sortedKeys]) {
        try? json.write(to: out.appendingPathComponent("\(item.id).json"))
    }
    line("\(item.id) 끝 · \(ok ? "성공" : "실패") · \(TokenFormat.usd(cost)) · \(Int(seconds / 60))분 · 스킬 \(skills.isEmpty ? "-" : skills.joined(separator: ","))")
}

let score = try? await ProcessRunner().run(executable: settings.uvPath,
                                           arguments: ["--directory", settings.repoDir + "/mcp-server", "run", "--quiet", "pokemate", "auto", "eval-score",
                                                       "--cases", casesFile.path, "--results", out.path] + only.flatMap { ["--case", $0] },
                                           environment: AppPaths.processEnvironment(settings: settings), timeout: 300)
print(String(decoding: score?.stdout ?? Data(), as: UTF8.self))
print(String(decoding: score?.stderr ?? Data(), as: UTF8.self))
exit(score?.exitCode == 0 ? 0 : 1)
