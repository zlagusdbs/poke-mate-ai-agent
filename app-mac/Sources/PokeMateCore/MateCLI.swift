import Foundation

public enum MateCLIError: Error, LocalizedError {
    case failed(String)
    public var errorDescription: String? {
        if case .failed(let text) = self { return text }
        return nil
    }
}

/// mcp-server 의 `pokemate` 명령 실행. 아이디·비밀번호는 표준 입력으로만 넘긴다.
public enum MateCLI {
    static let timeout: TimeInterval = 30

    public static func status(settings: AppSettings) async throws -> [SystemStatus] {
        let out = try await run(["status"], stdin: nil, settings: settings)
        return try decode([SystemStatus].self, out)
    }

    public static func connect(target: ConnectTarget, username: String, secret: String,
                               settings: AppSettings) async throws -> [ConnectResult] {
        let out = try await run(target.arguments, stdin: "\(username)\n\(secret)\n", settings: settings)
        return try decode([ConnectResult].self, out)
    }

    public static func githubReviewRequests(settings: AppSettings) async throws -> [PullRequest] {
        let out = try await run(["github", "review-requests"], stdin: nil, settings: settings)
        return try decode([PullRequest].self, out)
    }

    public static func migrateKeychain(settings: AppSettings) async throws -> Bool {
        let out = try await run(["migrate-keychain"], stdin: nil, settings: settings)
        return out.exitCode == 0
    }

    public static func events(_ eventId: String, settings: AppSettings) async throws -> [EventItem] {
        let out = try await run(["events", eventId], stdin: nil, settings: settings)
        return try decode([EventItem].self, out)
    }

    /// PR 의 바뀐 파일 목록 (helper `gh-get … /files --paginate`). 실패하거나 시간이 넘으면 nil, 바뀐 파일이 없으면 빈 목록.
    public static func pullFiles(_ target: ReviewTarget, settings: AppSettings) async -> [String]? {
        let args = ["auto", "gh-get", "repos/\(target.fullName)/pulls/\(target.number)/files", "--paginate", "--jq", ".[].filename"]
        guard let out = try? await run(args, stdin: nil, settings: settings), out.exitCode == 0 else { return nil }
        return parseFileList(String(decoding: out.stdout, as: UTF8.self))
    }

    /// 한 줄에 파일 하나. 앞뒤 공백·빈 줄을 빼고, 순서를 지키며 중복을 없앤다 (여러 쪽이 이어 붙어도 된다).
    /// CRLF 도 줄바꿈으로 본다 (Swift 에서 "\r\n" 은 글자 하나라 `\.isNewline` 으로 나눈다).
    public static func parseFileList(_ stdout: String) -> [String] {
        var seen = Set<String>()
        var files: [String] = []
        for line in stdout.split(whereSeparator: \.isNewline) {
            let file = line.trimmingCharacters(in: .whitespacesAndNewlines)
            if !file.isEmpty && seen.insert(file).inserted { files.append(file) }
        }
        return files
    }

    /// 이슈 등록. 사용자가 확인 카드에서 [Jira에 등록]을 눌렀을 때만 부른다.
    public static func jiraCreate(_ draft: JiraDraft, settings: AppSettings) async throws -> JiraCreated {
        let input = String(decoding: try JSONEncoder().encode(draft), as: UTF8.self)
        let out = try await run(["jira", "create"], stdin: input, settings: settings)
        if let created = try? JSONDecoder().decode(JiraCreated.self, from: out.stdout) { return created }
        struct Failure: Decodable { let error: String; let detail: String? }
        if let failure = try? JSONDecoder().decode(Failure.self, from: out.stdout) {
            throw MateCLIError.failed(JiraFailure.message(code: failure.error, detail: failure.detail ?? ""))
        }
        return try decode(JiraCreated.self, out)
    }

    /// worklog 기록. 사용자가 카드에서 [기록하기]를 눌렀을 때만 부른다.
    public static func jiraWorklog(_ draft: WorklogDraft, settings: AppSettings) async throws {
        let body: [String: Any] = ["issueKey": draft.issueKey, "started": draft.started(),
                                   "timeSpentSeconds": draft.minutes * 60, "comment": draft.comment]
        let input = String(decoding: try JSONSerialization.data(withJSONObject: body), as: UTF8.self)
        let out = try await run(["jira", "worklog"], stdin: input, settings: settings)
        struct Failure: Decodable { let error: String; let detail: String? }
        if let failure = try? JSONDecoder().decode(Failure.self, from: out.stdout) {
            throw MateCLIError.failed(JiraFailure.message(code: failure.error, detail: failure.detail ?? ""))
        }
        guard out.exitCode == 0 else {
            throw MateCLIError.failed(String(decoding: out.stderr, as: UTF8.self))
        }
    }

    private static func run(_ args: [String], stdin: String?, settings: AppSettings) async throws -> ProcessOutput {
        let out = try await ProcessRunner().run(
            executable: settings.uvPath,
            arguments: ["--directory", settings.repoDir + "/mcp-server", "run", "--quiet", "pokemate"] + args,
            stdin: stdin,
            environment: AppPaths.processEnvironment(settings: settings),
            timeout: timeout
        )
        if out.timedOut { throw MateCLIError.failed("연결 확인이 너무 오래 걸려요") }
        return out
    }

    private static func decode<T: Decodable>(_ type: T.Type, _ out: ProcessOutput) throws -> T {
        do {
            return try JSONDecoder().decode(T.self, from: out.stdout)
        } catch {
            let err = String(decoding: out.stderr, as: UTF8.self)
            let text = String(decoding: out.stdout, as: UTF8.self)
            throw MateCLIError.failed((err.isEmpty ? text : err).trimmingCharacters(in: .whitespacesAndNewlines))
        }
    }
}

/// 이슈 등록 실패를 사람이 읽는 말로.
public enum JiraFailure {
    public static func message(code: String, detail: String) -> String {
        switch code {
        case "NOT_CONNECTED:jira": return "Jira 연결이 필요해요. 연결 메뉴에서 연결해 주세요."
        case "AUTH_EXPIRED:jira": return "Jira 연결이 만료됐어요. 연결 메뉴에서 다시 연결해 주세요."
        case "NETWORK_UNREACHABLE:jira": return "사내망 연결을 확인해 주세요."
        case "REJECTED:jira": return "Jira 가 등록을 거절했어요: \(detail)"
        default: return detail.isEmpty ? "등록하지 못했어요 (\(code))" : "등록하지 못했어요: \(detail)"
        }
    }
}
