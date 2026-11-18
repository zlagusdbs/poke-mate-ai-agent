import Foundation

public struct AllowOption: Equatable, Identifiable, Sendable {
    public let label: String
    public let rules: [String]
    public let caution: Bool
    public var id: String { rules.joined(separator: ",") }

    public init(label: String, rules: [String], caution: Bool) {
        self.label = label
        self.rules = rules
        self.caution = caution
    }
}

public struct AllowMenu: Equatable, Sendable {
    public let options: [AllowOption]
    public let unavailable: String?
}

public enum ShellPrograms {
    static let subcommandTools: Set<String> = ["git", "gh", "npm", "npx", "yarn", "pnpm", "docker", "kubectl", "oc", "brew", "uv", "pip", "pip3",
                                               "cargo", "go", "swift", "make", "gradle", "./gradlew", "mvn", "./mvnw", "helm", "terraform", "aws", "gcloud"]
    static let risky: Set<String> = ["rm", "rmdir", "mv", "cp", "chmod", "chown", "chflags", "ln", "dd", "kill", "pkill", "killall", "curl", "wget",
                                     "scp", "rsync", "ssh", "sftp", "ftp", "nc", "open", "tee", "truncate", "xargs", "eval", "exec", "sh", "bash", "zsh",
                                     "python", "python3", "node", "perl", "ruby", "sed -i", "git push", "git commit", "git reset", "git checkout",
                                     "git rebase", "git merge", "git clean", "git rm", "git mv", "git tag", "gh pr", "gh api", "npm publish",
                                     "docker run", "docker rm", "kubectl apply", "kubectl delete", "brew install", "brew uninstall", "make", "mkdir", "touch"]

    public static func split(_ command: String) -> (segments: [[String]], problem: String?) {
        var segments: [[String]] = []
        var words: [String] = []
        var word = ""
        var quote: Character?
        var escaped = false
        var hasWord = false
        let chars = Array(command)
        var i = 0
        func endWord() {
            if hasWord { words.append(word) }
            word = ""
            hasWord = false
        }
        func endSegment() {
            endWord()
            if !words.isEmpty { segments.append(words) }
            words = []
        }
        while i < chars.count {
            let c = chars[i]
            if escaped {
                word.append(c); hasWord = true; escaped = false; i += 1; continue
            }
            if let q = quote {
                if c == q { quote = nil } else {
                    if q == "\"" && c == "$" && i + 1 < chars.count && chars[i + 1] == "(" { return ([], "$(…) 명령 치환이 있어요") }
                    if q == "\"" && c == "`" { return ([], "` 명령 치환이 있어요") }
                    word.append(c)
                }
                i += 1; continue
            }
            switch c {
            case "\\": escaped = true
            case "'", "\"": quote = c; hasWord = true
            case "`": return ([], "` 명령 치환이 있어요")
            case "$":
                if i + 1 < chars.count && chars[i + 1] == "(" { return ([], "$(…) 명령 치환이 있어요") }
                word.append(c); hasWord = true
            case "<", ">":
                if c == "<" && i + 1 < chars.count && chars[i + 1] == "(" { return ([], "<(…) 프로세스 치환이 있어요") }
                endWord()
                if i + 1 < chars.count && (chars[i + 1] == ">" || chars[i + 1] == "&") { i += 1 }
                i += 1
                while i < chars.count && chars[i] == " " { i += 1 }
                while i < chars.count && !" ;|&\n".contains(chars[i]) { i += 1 }
                continue
            case " ", "\t": endWord()
            case "\n", ";": endSegment()
            case "|", "&":
                endSegment()
                if i + 1 < chars.count && chars[i + 1] == c { i += 1 }
            case "(", ")", "{", "}": return ([], "괄호로 묶은 명령이 있어요")
            default: word.append(c); hasWord = true
            }
            i += 1
        }
        if quote != nil { return ([], "따옴표가 닫히지 않았어요") }
        endSegment()
        return (segments, nil)
    }

    public static func programs(_ command: String) -> (programs: [String], problem: String?) {
        let parsed = split(command)
        if let problem = parsed.problem { return ([], problem) }
        var found: [String] = []
        for words in parsed.segments {
            guard let first = words.first else { continue }
            if first.contains("=") && !first.hasPrefix("=") { return ([], "변수 대입(\(first.split(separator: "=").first ?? "")=…)이 있어요") }
            if ["if", "then", "else", "fi", "for", "while", "do", "done", "case", "esac", "function"].contains(first) {
                return ([], "셸 문법(\(first))이 있어요")
            }
            var program = first
            if subcommandTools.contains(first), words.count > 1, !words[1].hasPrefix("-") { program = "\(first) \(words[1])" }
            if first == "sed", words.dropFirst().contains(where: { $0 == "-i" || $0.hasPrefix("-i") }) { program = "sed -i" }
            if !found.contains(program) { found.append(program) }
        }
        return (found, nil)
    }

    public static func isRisky(_ program: String) -> Bool {
        risky.contains(program) || risky.contains(String(program.split(separator: " ").first ?? ""))
    }
}

public enum AllowRules {
    public static func rule(_ program: String) -> String { "Bash(\(program == "sed -i" ? "sed" : program):*)" }

    static func denied(_ program: String, deny: [String]) -> Bool {
        let head = String(program.split(separator: " ").first ?? "")
        return deny.contains { d in
            guard d.hasPrefix("Bash("), d.hasSuffix(")") else { return d == "Bash" }
            let inner = d.dropFirst(5).dropLast().replacingOccurrences(of: ":*", with: "").replacingOccurrences(of: "*", with: "")
            return inner == program || inner == head || program.hasPrefix(inner + " ")
        }
    }

    public static func menu(tool: String, input: [String: Any], suggested: [String], deny: [String]) -> AllowMenu {
        var options = suggested.isEmpty ? [] : [AllowOption(label: "Claude Code 제안: " + suggested.joined(separator: ", "), rules: suggested, caution: false)]
        switch tool {
        case "Bash":
            let command = (input["command"] as? String) ?? ""
            let parsed = ShellPrograms.programs(command)
            if let problem = parsed.problem {
                return AllowMenu(options: options, unavailable: "이 명령은 항상 허용할 수 없어요: \(problem)")
            }
            let allowed = parsed.programs.filter { !denied($0, deny: deny) }
            if allowed.count < parsed.programs.count {
                let blocked = parsed.programs.filter { denied($0, deny: deny) }
                return AllowMenu(options: options, unavailable: "권한 파일에서 막은 프로그램(\(blocked.joined(separator: "·")))이 들어 있어요")
            }
            if allowed.count > 1 {
                options.append(AllowOption(label: "이 명령에 쓰인 \(allowed.joined(separator: "·")) 모두", rules: allowed.map(rule),
                                           caution: allowed.contains(where: ShellPrograms.isRisky)))
            }
            for program in allowed where !options.contains(where: { $0.rules == [rule(program)] }) {
                options.append(AllowOption(label: program, rules: [rule(program)], caution: ShellPrograms.isRisky(program)))
            }
        case "WebFetch":
            if let url = (input["url"] as? String).flatMap(URL.init(string:)), let host = url.host {
                options.append(AllowOption(label: "\(host) 가져오기", rules: ["WebFetch(domain:\(host))"], caution: true))
            }
        case "Edit", "Write", "MultiEdit", "NotebookEdit":
            if let path = (input["file_path"] as? String) ?? (input["notebook_path"] as? String) {
                options.append(AllowOption(label: "이 파일 (\((path as NSString).lastPathComponent))", rules: ["\(tool)(/\(path))"], caution: true))
            }
            options.append(AllowOption(label: "\(tool) 전체", rules: [tool], caution: true))
        default:
            options.append(AllowOption(label: "\(tool) 전체", rules: [tool], caution: !tool.hasPrefix("mcp__")))
        }
        return AllowMenu(options: options, unavailable: options.isEmpty ? "항상 허용할 규칙을 만들 수 없어요" : nil)
    }

    public static func split(rule: String) -> (tool: String, content: String?) {
        guard let open = rule.firstIndex(of: "("), rule.hasSuffix(")") else { return (rule, nil) }
        return (String(rule[..<open]), String(rule[rule.index(after: open)..<rule.index(before: rule.endIndex)]))
    }
}
