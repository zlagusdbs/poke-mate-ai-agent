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

func git(_ args: [String]) async -> (ok: Bool, out: String) {
    guard let result = try? await ProcessRunner().run(executable: "/usr/bin/git", arguments: args, timeout: 120) else { return (false, "") }
    return (result.exitCode == 0, String(decoding: result.stdout, as: UTF8.self) + String(decoding: result.stderr, as: UTF8.self))
}

func overlay(_ source: URL, onto target: URL) throws {
    let fm = FileManager.default
    guard let walker = fm.enumerator(at: source, includingPropertiesForKeys: [.isDirectoryKey]) else { return }
    for case let file as URL in walker {
        let relative = String(file.path.dropFirst(source.path.count + 1))
        let dest = target.appendingPathComponent(relative)
        if (try? file.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true {
            try fm.createDirectory(at: dest, withIntermediateDirectories: true)
        } else {
            if fm.fileExists(atPath: dest.path) { try fm.removeItem(at: dest) }
            try fm.copyItem(at: file, to: dest)
        }
    }
}

func prepareFixture(_ item: ReviewEvalCase, fixture: ReviewEvalFixture, dir: URL, work: URL) async -> (worktree: String, diff: String)? {
    let repo = work.appendingPathComponent(fixture.repo, isDirectory: true)
    let tree = work.appendingPathComponent("worktree-\(fixture.key)", isDirectory: true)
    do {
        try FileManager.default.createDirectory(at: repo, withIntermediateDirectories: true)
        try overlay(dir.appendingPathComponent("base"), onto: repo)
    } catch { return nil }
    let r = repo.path
    for args in [["init", "-q", "-b", "main", r], ["-C", r, "config", "user.name", "Sample Bot"], ["-C", r, "config", "user.email", "sample-bot@example.com"],
                 ["-C", r, "add", "-A"], ["-C", r, "commit", "-q", "-m", "feat: base"], ["-C", r, "worktree", "add", "-q", "-b", fixture.branch, tree.path, "main"]] {
        guard await git(args).ok else { return nil }
    }
    guard (try? overlay(dir.appendingPathComponent("head"), onto: tree)) != nil,
          await git(["-C", tree.path, "add", "-A", "-N"]).ok else { return nil }
    let diff = await git(["-C", tree.path, "diff", "HEAD"])
    guard diff.ok else { return nil }
    let file = work.appendingPathComponent("\(fixture.key).diff")
    guard (try? diff.out.write(to: file, atomically: true, encoding: .utf8)) != nil else { return nil }
    return (tree.path, file.path)
}

for item in cases {
    let sessionId = UUID().uuidString.lowercased()
    var prompt = ""
    var access = ClaudeCommand.WriteAccess()
    var environment: [String: String] = ["POKEMATE_EVAL": "1"]
    var domain = DomainReview.fallback
    if let name = item.fixture {
        guard let dir = ReviewEval.fixtureDir(name), let fixture = ReviewEval.loadFixture(dir) else {
            line("\(item.id) 건너뜀 · 문제 파일 \(name) 을 찾지 못했어요")
            continue
        }
        let work = out.appendingPathComponent("\(item.id)-work", isDirectory: true)
        guard let prepared = await prepareFixture(item, fixture: fixture, dir: dir, work: work) else {
            line("\(item.id) 건너뜀 · 가상 레포를 만들지 못했어요")
            continue
        }
        domain = ReviewEval.fixtureDomain(item, fixture: fixture, map: map)
        var config = PipelineConfig.load(promptsDir: Rules.promptsDir, access: [:], dir: work, helper: helper,
                                         fallbackDir: Rules.folder(.prompt, layer: .shared))
        config.domains = map
        config.notes = notes
        let state = ReviewEval.fixtureState(fixture, domain: domain, worktree: prepared.worktree, now: Date())
        prompt = JiraPipeline.prompt(role: .reviewer, state: state, config: config, resumed: false, diffFile: prepared.diff) + "\n\n" + ReviewEval.pipelineTail
        access = ReviewEval.reviewerAccess(helper: helper, worktree: prepared.worktree,
                                           dirs: [home + "/Workspace", home + "/.claude/references", notes.root, work.path] + map.knowledgePaths(for: domain))
        line("\(item.id) 시작 · 가상 레포 \(fixture.repo) · Jira 리뷰어 경로 · 도메인 \(domain) · 세션 \(sessionId)")
    } else {
        guard let target = ReviewTarget.parse(url: item.url) else { continue }
        let chosen = ReviewEval.domain(for: item, target: target, map: map, book: book)
        domain = chosen.domain
        access = ReviewEval.access(helper: helper, dirs: [home + "/Workspace", home + "/.claude/references", notes.root] + map.knowledgePaths(for: chosen.domain))
        prompt = ReviewEval.prompt(item: item, target: target, domain: chosen.domain, why: chosen.why, source: chosen.source, notes: notes,
                                   helper: helper, template: template, knowledge: map.knowledgeText(for: chosen.domain))
        environment = ReviewEval.environment(target: target)
        line("\(item.id) 시작 · 도메인 \(chosen.domain) · 세션 \(sessionId)")
    }
    let started = Date()
    let runner = ClaudeRunner()
    let response = await runner.run(prompt: prompt, sessionId: nil, settings: settings, writeTools: access.tools, writeDirs: access.dirs,
                                     timeout: 2400, newSessionId: sessionId, automation: true, extraEnvironment: environment)
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
    let meta = ReviewEval.meta(costUSD: cost, seconds: seconds, skills: skills, sessionId: sessionId, usage: runner.lastUsage, ok: ok, domain: domain)
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
