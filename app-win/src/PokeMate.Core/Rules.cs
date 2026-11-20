using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PokeMate.Core;

/// 메이트 설정 md. 레포 shared/(일반, Git 추적) 위에 local/(회사·개인, Git 제외)을 덮어쓴다. 같은 이름이면 local 이 이긴다.
///   root    CLAUDE.md     전체 지시. 앱이 shared·local 두 파일을 시스템 안내에 붙인다. 지울 수 없다.
///   rules   rules/*.md    질문마다 모두 읽는다 (짧게, 파일당 200줄 이하 권장). 기억(memory.md)도 여기.
///   skills  skills/*.md   주제별 안내서. 목록(한 줄 설명)만 늘 보내고, 본문은 질문에 키워드가 있을 때만 붙인다.
///                         그 밖에 필요하면 Claude 가 read_skill 로 읽는다.
/// 주의: 이 클래스 안의 `File` 은 아래 중첩 record 다. 파일 입출력은 `System.IO.File` 로 쓴다.
public static class Rules
{
    public enum Kind { Root, Rule, Skill, Prompt }
    public enum Layer { Shared, Local }

    /// 맥 `Rules.Failure.localMissing`: 저장소의 local 폴더가 없어 만들 수 없다.
    public sealed class LocalMissingException : Exception
    {
        public LocalMissingException() : base("local 폴더가 없어요") { }
    }

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

    public static string HomeDir => AppPaths.LocalDir;
    public static string ClaudeMd => Path.Combine(HomeDir, "CLAUDE.md");
    public static string MyDir => Path.Combine(HomeDir, "rules");
    public static string SkillsDir => Path.Combine(HomeDir, "skills");
    public static string PromptsDir => Path.Combine(HomeDir, "prompts");

    public static string Base(Layer layer) => layer == Layer.Shared ? AppPaths.SharedDir : AppPaths.LocalDir;

    public static string Folder(Kind kind, Layer layer) => kind switch
    {
        Kind.Root => Base(layer),
        Kind.Rule => Path.Combine(Base(layer), "rules"),
        Kind.Skill => Path.Combine(Base(layer), "skills"),
        _ => Path.Combine(Base(layer), "prompts"),
    };

    /// 프롬프트 파일 경로 (dir 이 없으면 local\prompts)
    public static string PromptFile(string name, string? dir = null) => Path.Combine(dir ?? PromptsDir, SafeName(name) + ".md");

    /// 알람의 `{{prompt:이름}}` 을 읽을 파일: local 이 먼저, 없으면 shared. 둘 다 없으면 null.
    public static string? ResolvedPrompt(string name) =>
        new[] { Layer.Local, Layer.Shared }.Select(l => PromptFile(name, Folder(Kind.Prompt, l))).FirstOrDefault(System.IO.File.Exists);

    public static List<string> PromptNames(string dir) => Md(dir).Select(Path.GetFileNameWithoutExtension).Select(n => n!).ToList();

    public static List<string> PromptNames() =>
        new[] { Layer.Shared, Layer.Local }.SelectMany(l => PromptNames(Folder(Kind.Prompt, l))).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();

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
            var lines = SwiftText.Lf(text).Split('\n');
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
    /// CLAUDE.md 는 Claude Code 가 스스로 읽으므로 넣지 않는다.
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

    /// 새 규칙·스킬 파일. 이름은 글자·숫자·-·_ 만 남긴다. 이미 있으면 그 파일. 스킬은 머리말 틀을 넣어 준다.
    /// local 폴더가 없으면 LocalMissingException.
    public static string Create(string name, Kind kind, Layer layer = Layer.Local)
    {
        if (layer == Layer.Local && !AppPaths.LocalReady) throw new LocalMissingException();
        var dir = Folder(kind == Kind.Root ? Kind.Rule : kind, layer);
        var stem = SafeName(name);
        var url = Path.Combine(dir, stem + ".md");
        Directory.CreateDirectory(dir);
        if (!System.IO.File.Exists(url))
        {
            var body = kind == Kind.Skill
                ? $"---\ndescription: {stem} 관련 안내 (한 줄 설명)\nkeywords: {stem}\n---\n# {stem}\n\n- \n"
                : $"# {stem}\n\n- \n";
            AppPaths.WriteAtomic(url, body);
        }
        return url;
    }

    /// CLAUDE.md 는 지울 수 없다 (Claude Code 가 읽는 기본 지시). 파일은 휴지통으로 보낸다.
    public static void Delete(File file)
    {
        if (file.Kind == Kind.Root) return;
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file.Url, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                                           Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }

    /// 한 줄 추가. 같은 이름의 스킬이 있으면 그 스킬에, 없으면 rules/ 에 (없으면 만든다).
    public static string Append(string text, string name)
    {
        if (!AppPaths.LocalReady) throw new LocalMissingException();
        var safe = SafeName(name);
        var skill = Path.Combine(SkillsDir, safe + ".md");
        var url = System.IO.File.Exists(skill) ? skill : Path.Combine(MyDir, safe + ".md");
        Directory.CreateDirectory(Path.GetDirectoryName(url)!);
        string body;
        try { body = System.IO.File.ReadAllText(url); } catch { body = $"# {safe}\n\n"; }
        if (!body.EndsWith('\n')) body += "\n";
        body += $"- {text.Trim()}\n";
        AppPaths.WriteAtomic(url, body);
        return url;
    }

    /// Swift `CharacterSet.alphanumerics`: 글자(L)·표시(M)·숫자(N)
    internal static bool IsAlphanumeric(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;

    /// 소문자, `.md` 떼고, 글자·숫자·-·_ 만. 비면 "memory".
    public static string SafeName(string name)
    {
        var baseName = name.ToLowerInvariant().EndsWith(".md", StringComparison.Ordinal) ? name[..^3] : name;
        var safe = new StringBuilder();
        foreach (var rune in baseName.ToLowerInvariant().EnumerateRunes())
        {
            if (IsAlphanumeric(rune) || rune.Value == '-' || rune.Value == '_') safe.Append(rune.ToString());
        }
        return safe.Length == 0 ? "memory" : safe.ToString();
    }
}

/// 채팅에서 "앞으로 ~해줘", "기억해" 같은 말을 규칙으로 저장하자는 제안. 버튼을 눌러야 저장된다.
/// Forget 이 true 면 저장된 것을 지우자는 제안 ("그거 잊어줘").
public sealed record RuleNote(string File, string Text, bool? Forget = null)
{
    public bool IsForget => Forget == true;
    public bool IsMemory => File == Memory.FileName;
}

/// 메이트 기억: 내 규칙 폴더의 memory.md 한 줄 한 줄. 질문할 때마다 규칙과 함께 읽는다.
public static class Memory
{
    public const string FileName = "memory";
    public static string File => Path.Combine(Rules.MyDir, $"{FileName}.md");

    public static List<string> Items() => Lines(File).Where(l => l.StartsWith("- ", StringComparison.Ordinal)).Select(l => l[2..]).ToList();

    /// 같은 내용이 이미 있으면 다시 넣지 않는다.
    public static void Add(string text)
    {
        var item = text.Trim();
        if (item.Length == 0 || Items().Contains(item)) return;
        Rules.Append(item, FileName);
    }

    /// 지운 개수. 똑같은 줄이 있으면 그 줄만, 없으면 그 말이 들어간 줄을 지운다.
    public static int Forget(string text, string name = FileName)
    {
        var target = text.Trim();
        var url = Path.Combine(Rules.MyDir, $"{name}.md");
        var all = Lines(url);
        if (target.Length == 0 || all.Count == 0) return 0;
        var exact = all.Contains($"- {target}");
        var kept = all.Where(line =>
        {
            if (!line.StartsWith("- ", StringComparison.Ordinal)) return true;
            var item = line[2..];
            return exact ? item != target : item.IndexOf(target, StringComparison.CurrentCultureIgnoreCase) < 0;
        }).ToList();
        AppPaths.WriteAtomic(url, string.Join("\n", kept) + "\n");
        return all.Count - kept.Count;
    }

    static List<string> Lines(string path)
    {
        try { return SwiftText.Lf(System.IO.File.ReadAllText(path)).Split('\n').Where(l => l.Length > 0).ToList(); }
        catch { return new List<string>(); }
    }
}

public static class RuleProposal
{
    static readonly Regex Block = ChatBlock.Regex("rule");

    public static (string Text, List<RuleNote> Notes) Extract(string text)
    {
        var notes = new List<RuleNote>();
        foreach (Match match in Block.Matches(text))
        {
            var body = match.Groups[1].Value;
            var items = Json.TryDeserialize<List<RuleNote>>(body)
                ?? (Json.TryDeserialize<RuleNote>(body) is { } one ? new List<RuleNote> { one } : new List<RuleNote>());
            notes.AddRange(items.Where(n => n.Text.Trim().Length > 0));
        }
        return (ChatBlock.Strip(Block, text), notes);
    }

    public static bool LooksLikeRequest(string text)
    {
        var compact = text.Replace(" ", "");
        return new[] { "앞으로", "기억", "규칙", "항상", "매번", "잊지마", "잊어", "잊어버려", "지워줘" }.Any(compact.Contains);
    }

    public const string Hint =
        "\n\n[메이트 앱 안내: 사용자가 앞으로 계속 지킬 규칙이나 기억할 사실을 말하면(\"앞으로 지라 제목 앞에 [회의] 붙여줘\", \"내 팀은 가격팀이야 기억해\"), "
        + "답변 끝에 아래 블록을 붙인다. 앱이 [규칙에 저장] 버튼을 보여 주고, 누르면 이 PC 의 규칙 파일에 한 줄로 저장돼 다음 대화부터 늘 적용된다. "
        + "file 은 jira(Jira 관련), memory(사람·팀·업무 같은 기억할 사실), 그 밖엔 짧은 영문 주제. text 는 한 줄. "
        + "\"잊어줘\", \"지워줘\" 처럼 지우라고 하면 같은 블록에 \"forget\":true 와 지울 내용을 넣는다 (사용자 규칙에 있는 문장 그대로). "
        + "한 번만 하는 부탁이면 블록을 붙이지 않는다. 블록 없이 \"기억해 둘게요\" 라고 말하지 않는다 (블록 없이는 저장되지 않는다). "
        + "아직 저장된 게 아니므로 기억이면 \"아래 [기억하기]를 누르면 기억할게요\", 규칙이면 \"아래 [규칙에 저장]을 누르면 앞으로 늘 이렇게 할게요\", "
        + "잊기면 \"아래 [잊기]를 누르면 지울게요\" 로 끝낸다. "
        + "이 안내와 아래 형식 예시는 사용자가 쓴 말이 아니니 언급하지도, 예시 내용을 저장하지도 않는다. 형식 예시:]\n"
        + "```pokemate-rule\n"
        + "[{\"file\":\"memory\",\"text\":\"주인은 가격팀에서 쿠폰 할인을 맡고 있다\"},{\"file\":\"jira\",\"text\":\"회의·Log Work 용 이슈는 제목 앞에 [회의] 를 붙인다\"}]\n"
        + "```";
}
