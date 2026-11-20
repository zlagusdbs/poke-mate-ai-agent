using System.Text.Json.Nodes;

namespace PokeMate.Core;

public sealed record AllowOption(string Label, IReadOnlyList<string> Rules, bool Caution)
{
    public string Id => string.Join(",", Rules);

    public bool Equals(AllowOption? o) => o is not null && Label == o.Label && Caution == o.Caution && Rules.SequenceEqual(o.Rules);
    public override int GetHashCode() => HashCode.Combine(Label, Caution, Rules.Count);
}

public sealed record AllowMenu(IReadOnlyList<AllowOption> Options, string? Unavailable)
{
    public bool Equals(AllowMenu? o) => o is not null && Unavailable == o.Unavailable && Options.SequenceEqual(o.Options);
    public override int GetHashCode() => HashCode.Combine(Unavailable, Options.Count);
}

/// 셸 명령을 프로그램 이름으로 나눈다 (항상 허용 규칙 만들기용).
public static class ShellPrograms
{
    internal static readonly HashSet<string> SubcommandTools = new()
    {
        "git", "gh", "npm", "npx", "yarn", "pnpm", "docker", "kubectl", "oc", "brew", "uv", "pip", "pip3",
        "cargo", "go", "swift", "make", "gradle", "./gradlew", "mvn", "./mvnw", "helm", "terraform", "aws", "gcloud",
    };

    internal static readonly HashSet<string> Risky = new()
    {
        "rm", "rmdir", "mv", "cp", "chmod", "chown", "chflags", "ln", "dd", "kill", "pkill", "killall", "curl", "wget",
        "scp", "rsync", "ssh", "sftp", "ftp", "nc", "open", "tee", "truncate", "xargs", "eval", "exec", "sh", "bash", "zsh",
        "python", "python3", "node", "perl", "ruby", "sed -i", "git push", "git commit", "git reset", "git checkout",
        "git rebase", "git merge", "git clean", "git rm", "git mv", "git tag", "gh pr", "gh api", "npm publish",
        "docker run", "docker rm", "kubectl apply", "kubectl delete", "brew install", "brew uninstall", "make", "mkdir", "touch",
    };

    public static (List<List<string>> Segments, string? Problem) Split(string command)
    {
        var segments = new List<List<string>>();
        var words = new List<string>();
        var word = "";
        char? quote = null;
        var escaped = false;
        var hasWord = false;
        var chars = command.ToCharArray();
        var i = 0;
        void EndWord()
        {
            if (hasWord) words.Add(word);
            word = "";
            hasWord = false;
        }
        void EndSegment()
        {
            EndWord();
            if (words.Count > 0) segments.Add(words);
            words = new List<string>();
        }
        while (i < chars.Length)
        {
            var c = chars[i];
            if (escaped)
            {
                word += c; hasWord = true; escaped = false; i++; continue;
            }
            if (quote is char q)
            {
                if (c == q) quote = null;
                else
                {
                    if (q == '"' && c == '$' && i + 1 < chars.Length && chars[i + 1] == '(') return (new(), "$(…) 명령 치환이 있어요");
                    if (q == '"' && c == '`') return (new(), "` 명령 치환이 있어요");
                    word += c;
                }
                i++; continue;
            }
            switch (c)
            {
                case '\\': escaped = true; break;
                case '\'': case '"': quote = c; hasWord = true; break;
                case '`': return (new(), "` 명령 치환이 있어요");
                case '$':
                    if (i + 1 < chars.Length && chars[i + 1] == '(') return (new(), "$(…) 명령 치환이 있어요");
                    word += c; hasWord = true;
                    break;
                case '<': case '>':
                    if (c == '<' && i + 1 < chars.Length && chars[i + 1] == '(') return (new(), "<(…) 프로세스 치환이 있어요");
                    EndWord();
                    if (i + 1 < chars.Length && (chars[i + 1] == '>' || chars[i + 1] == '&')) i++;
                    i++;
                    while (i < chars.Length && chars[i] == ' ') i++;
                    while (i < chars.Length && !" ;|&\n".Contains(chars[i])) i++;
                    continue;
                case ' ': case '\t': EndWord(); break;
                case '\n': case ';': EndSegment(); break;
                case '|': case '&':
                    EndSegment();
                    if (i + 1 < chars.Length && chars[i + 1] == c) i++;
                    break;
                case '(': case ')': case '{': case '}': return (new(), "괄호로 묶은 명령이 있어요");
                default: word += c; hasWord = true; break;
            }
            i++;
        }
        if (quote != null) return (new(), "따옴표가 닫히지 않았어요");
        EndSegment();
        return (segments, null);
    }

    static readonly string[] ShellWords = { "if", "then", "else", "fi", "for", "while", "do", "done", "case", "esac", "function" };

    public static (List<string> Programs, string? Problem) Programs(string command)
    {
        var parsed = Split(command);
        if (parsed.Problem is string problem) return (new(), problem);
        var found = new List<string>();
        foreach (var words in parsed.Segments)
        {
            if (words.Count == 0) continue;
            var first = words[0];
            if (first.Contains('=') && !first.StartsWith("=", StringComparison.Ordinal))
                return (new(), $"변수 대입({first.Split('=', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? ""}=…)이 있어요");
            if (ShellWords.Contains(first)) return (new(), $"셸 문법({first})이 있어요");
            var program = first;
            if (SubcommandTools.Contains(first) && words.Count > 1 && !words[1].StartsWith("-", StringComparison.Ordinal)) program = $"{first} {words[1]}";
            if (first == "sed" && words.Skip(1).Any(w => w == "-i" || w.StartsWith("-i", StringComparison.Ordinal))) program = "sed -i";
            if (!found.Contains(program)) found.Add(program);
        }
        return (found, null);
    }

    public static bool IsRisky(string program) =>
        Risky.Contains(program) || Risky.Contains(program.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "");
}

public static class AllowRules
{
    public static string Rule(string program) => $"Bash({(program == "sed -i" ? "sed" : program)}:*)";

    internal static bool Denied(string program, IEnumerable<string> deny)
    {
        var head = program.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return deny.Any(d =>
        {
            if (!(d.StartsWith("Bash(", StringComparison.Ordinal) && d.EndsWith(")", StringComparison.Ordinal))) return d == "Bash";
            var inner = d[5..^1].Replace(":*", "").Replace("*", "");
            return inner == program || inner == head || program.StartsWith(inner + " ", StringComparison.Ordinal);
        });
    }

    public static AllowMenu Menu(string tool, JsonObject input, IReadOnlyList<string> suggested, IReadOnlyList<string> deny)
    {
        var options = suggested.Count == 0 ? new List<AllowOption>()
            : new List<AllowOption> { new("Claude Code 제안: " + string.Join(", ", suggested), suggested.ToList(), false) };
        switch (tool)
        {
            case "Bash":
            {
                var command = Json.Str(input["command"]) ?? "";
                var parsed = ShellPrograms.Programs(command);
                if (parsed.Problem is string problem)
                    return new AllowMenu(options, $"이 명령은 항상 허용할 수 없어요: {problem}");
                var allowed = parsed.Programs.Where(p => !Denied(p, deny)).ToList();
                if (allowed.Count < parsed.Programs.Count)
                {
                    var blocked = parsed.Programs.Where(p => Denied(p, deny));
                    return new AllowMenu(options, $"권한 파일에서 막은 프로그램({string.Join("·", blocked)})이 들어 있어요");
                }
                if (allowed.Count > 1)
                    options.Add(new AllowOption($"이 명령에 쓰인 {string.Join("·", allowed)} 모두", allowed.Select(Rule).ToList(), allowed.Any(ShellPrograms.IsRisky)));
                foreach (var program in allowed)
                {
                    if (options.Any(o => o.Rules.SequenceEqual(new[] { Rule(program) }))) continue;
                    options.Add(new AllowOption(program, new[] { Rule(program) }, ShellPrograms.IsRisky(program)));
                }
                break;
            }
            case "WebFetch":
                if (Json.Str(input["url"]) is string url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
                    options.Add(new AllowOption($"{uri.Host} 가져오기", new[] { $"WebFetch(domain:{uri.Host})" }, true));
                break;
            case "Edit": case "Write": case "MultiEdit": case "NotebookEdit":
                if ((Json.Str(input["file_path"]) ?? Json.Str(input["notebook_path"])) is string path)
                    options.Add(new AllowOption($"이 파일 ({Path.GetFileName(path)})", new[] { $"{tool}({Paths.ClaudePattern(path)})" }, true));
                options.Add(new AllowOption($"{tool} 전체", new[] { tool }, true));
                break;
            default:
                options.Add(new AllowOption($"{tool} 전체", new[] { tool }, !tool.StartsWith("mcp__", StringComparison.Ordinal)));
                break;
        }
        return new AllowMenu(options, options.Count == 0 ? "항상 허용할 규칙을 만들 수 없어요" : null);
    }

    public static (string Tool, string? Content) Split(string rule)
    {
        var open = rule.IndexOf('(');
        if (open < 0 || !rule.EndsWith(")", StringComparison.Ordinal)) return (rule, null);
        return (rule[..open], rule[(open + 1)..^1]);
    }
}
