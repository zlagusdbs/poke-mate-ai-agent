namespace PokeMate.Core;

// Task 2 는 ClaudeRunner 가 쓰는 부분(Instructions, Prompt 와 그것이 쓰는 Kind·Layer·File·Info·Folder·Files·Overridden·Effective·Render)만 옮겼다.
// Create·Delete·Append·SafeName·PromptFile·RuleNote·Memory·RuleProposal 은 나중 작업이 이 파일에 채운다.

/// 메이트 설정 md. 레포 shared/(일반, Git 추적) 위에 local/(회사·개인, Git 제외)을 덮어쓴다. 같은 이름이면 local 이 이긴다.
public static class Rules
{
    public enum Kind { Root, Rule, Skill, Prompt }
    public enum Layer { Shared, Local }

    public sealed record File(string Url)
    {
        public string Id => Url;

        public Kind Kind => Path.GetFileName(Path.GetDirectoryName(Url) ?? "") switch
        {
            "rules" => Kind.Rule,
            "skills" => Kind.Skill,
            "prompts" => Kind.Prompt,
            _ => Kind.Root,
        };

        /// 메이트 폴더 기준 경로. 예: CLAUDE.md, rules/memory.md, skills/jira.md
        public string Name => Kind == Kind.Root ? Path.GetFileName(Url) : $"{Path.GetFileName(Path.GetDirectoryName(Url))}/{Path.GetFileName(Url)}";

        public Layer Layer => Url.StartsWith(AppPaths.SharedDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? Layer.Shared : Layer.Local;

        public int LineCount
        {
            get
            {
                try { return System.IO.File.ReadAllText(Url).Split('\n').Length; } catch { return 1; }
            }
        }
    }

    /// rules 파일 권장 최대 줄 수 (질문마다 모두 읽어서)
    public const int RuleLineLimit = 200;

    public static string Base(Layer layer) => layer == Layer.Shared ? AppPaths.SharedDir : AppPaths.LocalDir;

    public static string Folder(Kind kind, Layer layer) => kind switch
    {
        Kind.Root => Base(layer),
        Kind.Rule => Path.Combine(Base(layer), "rules"),
        Kind.Skill => Path.Combine(Base(layer), "skills"),
        _ => Path.Combine(Base(layer), "prompts"),
    };

    public static string Instructions(string? shared = null, string? local = null)
    {
        var parts = new[] { shared ?? AppPaths.SharedDir, local ?? AppPaths.LocalDir }.Select(dir =>
        {
            try { return System.IO.File.ReadAllText(Path.Combine(dir, "CLAUDE.md")).Trim(); } catch { return ""; }
        }).Where(t => t.Length > 0).ToList();
        if (parts.Count == 0) return "";
        return "\n\n# 메이트 지시 (shared/CLAUDE.md 다음 local/CLAUDE.md, 겹치면 뒤의 것을 따른다)\n\n" + string.Join("\n\n", parts);
    }

    static List<string> Md(string dir)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(dir).Where(p => Path.GetExtension(p) == ".md")
                .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal).ToList();
        }
        catch { return new List<string>(); }
    }

    /// CLAUDE.md (있으면) + rules/*.md + skills/*.md + prompts/*.md
    public static List<File> Files() =>
        new[] { Layer.Shared, Layer.Local }.SelectMany(layer =>
        {
            var top = Path.Combine(Folder(Kind.Root, layer), "CLAUDE.md");
            var root = System.IO.File.Exists(top) ? new List<string> { top } : new List<string>();
            return root.Concat(Md(Folder(Kind.Rule, layer))).Concat(Md(Folder(Kind.Skill, layer))).Concat(Md(Folder(Kind.Prompt, layer)));
        }).Select(p => new File(p)).ToList();

    public static HashSet<string> Overridden(IReadOnlyList<File> files)
    {
        var local = files.Where(f => f.Layer == Layer.Local && f.Kind != Kind.Root).Select(f => f.Name).ToHashSet();
        return files.Where(f => f.Layer == Layer.Shared && local.Contains(f.Name)).Select(f => f.Id).ToHashSet();
    }

    public static List<File> Effective(IReadOnlyList<File> files)
    {
        var hidden = Overridden(files);
        return files.Where(f => !hidden.Contains(f.Id)).ToList();
    }

    /// 파일 맨 위 머리말. 없으면 제목 줄이 설명, 파일 이름이 키워드.
    public sealed record Info(string Name, string Description, IReadOnlyList<string> Keywords, string Body)
    {
        public bool Equals(Info? o) => o is not null && Name == o.Name && Description == o.Description && Body == o.Body && Keywords.SequenceEqual(o.Keywords);
        public override int GetHashCode() => HashCode.Combine(Name, Description, Body);

        public static Info Parse(string name, string text)
        {
            var meta = new Dictionary<string, string>();
            var body = text;
            var lines = text.Split('\n');
            if (lines.Length > 0 && SwiftText.TrimSpaces(lines[0]) == "---")
            {
                var end = Array.FindIndex(lines, 1, l => SwiftText.TrimSpaces(l) == "---");
                if (end > 0)
                {
                    for (var i = 1; i < end; i++)
                    {
                        var parts = lines[i].Split(':', 2).Select(SwiftText.TrimSpaces).ToArray();
                        if (parts.Length == 2) meta[parts[0].ToLowerInvariant()] = parts[1];
                    }
                    body = string.Join("\n", lines.Skip(end + 1));
                }
            }
            body = body.Trim();
            var stem = Path.GetFileName(name).Replace(".md", "");
            var heading = body.Split('\n').FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))?[2..];
            var keywords = (meta.TryGetValue("keywords", out var k) ? k.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => SwiftText.TrimSpaces(x).ToLowerInvariant()) : Enumerable.Empty<string>())
                .Where(x => x.Length > 0).ToList();
            return new Info(name, meta.TryGetValue("description", out var d) ? d : heading ?? stem,
                            keywords.Count == 0 ? new[] { stem.ToLowerInvariant() } : keywords, body);
        }

        /// 질문에 키워드가 들어 있으면 이 스킬이 필요하다고 본다 (띄어쓰기·대소문자 무시).
        public bool Matches(string message)
        {
            var compact = message.Replace(" ", "").ToLowerInvariant();
            return Keywords.Any(k => compact.Contains(k.Replace(" ", "")));
        }
    }

    static List<Info> Infos(Kind kind) =>
        Effective(Files()).Where(f => f.Kind == kind).Select(f =>
        {
            try { return Info.Parse(f.Name, System.IO.File.ReadAllText(f.Url)); } catch { return null; }
        }).Where(i => i != null).Select(i => i!).ToList();

    /// 시스템 안내에 붙일 글: rules 본문 전부 + skills 목록 + 질문에 맞는 skills 본문.
    public static string Prompt(string message) => Render(Infos(Kind.Rule), Infos(Kind.Skill), message);

    public static string Render(IReadOnlyList<Info> rules, IReadOnlyList<Info> skills, string message)
    {
        var text = "";
        var ruleBodies = rules.Where(r => r.Body.Length > 0).ToList();
        if (ruleBodies.Count > 0)
        {
            text += "\n\n# 사용자 규칙 (메이트 폴더의 rules/*.md, 늘 지킨다)\n";
            foreach (var info in ruleBodies) text += $"\n## {info.Name}\n{info.Body}\n";
        }
        if (skills.Count > 0)
        {
            text += "\n\n# 사용자 스킬 (메이트 폴더의 skills/*.md)\n\n스킬 목록:\n";
            text += string.Join("\n", skills.Select(s => $"- {s.Name}: {s.Description}"));
            text += "\n아래에 본문이 없는 스킬이 이번 질문에 필요하면 read_skill 도구로 읽고 따른다.\n";
            foreach (var info in skills.Where(i => i.Matches(message) && i.Body.Length > 0)) text += $"\n## {info.Name}\n{info.Body}\n";
        }
        return text;
    }
}
