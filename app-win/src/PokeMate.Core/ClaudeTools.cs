using System.Text.Json.Nodes;

namespace PokeMate.Core;

/// Claude Code 가 묻는 도구 사용 권한 한 건. Input·Suggestions 는 JSON 글.
public sealed record PermissionRequest(string Id, string Tool, string Summary, string Detail, IReadOnlyList<string> Rules, string Input, string Suggestions)
{
    public JsonObject InputObject => Json.ParseObject(Input) ?? new JsonObject();

    public bool Equals(PermissionRequest? o) =>
        o is not null && Id == o.Id && Tool == o.Tool && Summary == o.Summary && Detail == o.Detail
        && Rules.SequenceEqual(o.Rules) && Input == o.Input && Suggestions == o.Suggestions;

    public override int GetHashCode() => HashCode.Combine(Id, Tool, Summary);
}

/// 승인 카드가 연달아 뜰 때 알림은 5초에 하나만.
public sealed class PermissionAlertGate
{
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(5);
    public const string NotificationId = "pokemate-permission";
    DateTimeOffset? last;

    public bool ShouldAlert(DateTimeOffset now)
    {
        if (last is DateTimeOffset l && now - l < Quiet) return false;
        last = now;
        return true;
    }

    public void Reset() => last = null;

    public static string Body(PermissionRequest request, int limit = 100)
    {
        var line = $"{request.Tool}: {request.Summary}".Replace("\n", " ");
        return SwiftText.Count(line) > limit ? SwiftText.Prefix(line, limit) + "…" : line;
    }
}

public abstract record PermissionDecision
{
    public sealed record Allow : PermissionDecision;
    public sealed record AllowAlways : PermissionDecision;
    public sealed record AllowRules(IReadOnlyList<string> Rules) : PermissionDecision
    {
        public bool Equals(AllowRules? o) => o is not null && Rules.SequenceEqual(o.Rules);
        public override int GetHashCode() => Rules.Count;
    }
    public sealed record Deny(string Message) : PermissionDecision;
}

/// `claude -p --output-format stream-json` 줄 해석과 권한 응답.
public static class ClaudeStream
{
    /// 한 줄을 JSON 객체로 (CRLF 의 CR 이 남아 있어도 된다). 객체가 아니면 null.
    public static JsonObject? Object(string line) => Json.ParseObject(line);

    static string Text(JsonObject input, string key) => Json.Str(input[key])?.Trim() ?? "";

    static string Short(string s, int limit = 120)
    {
        var one = s.Replace("\n", " ⏎ ");
        return SwiftText.Count(one) > limit ? SwiftText.Prefix(one, limit) + "…" : one;
    }

    static int LineCount(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

    public static string Target(string tool, JsonObject input)
    {
        foreach (var key in new[] { "command", "file_path", "notebook_path", "url", "query", "pattern", "path", "description" })
        {
            var value = Text(input, key);
            if (value.Length > 0) return Short(value);
        }
        return "";
    }

    public static string Activity(string tool, JsonObject input)
    {
        var verb = tool switch
        {
            "Bash" => "실행 중",
            "Read" or "NotebookRead" => "읽는 중",
            "Edit" or "MultiEdit" or "NotebookEdit" => "수정 중",
            "Write" => "쓰는 중",
            "Grep" or "Glob" => "찾는 중",
            "WebFetch" => "가져오는 중",
            "WebSearch" => "검색 중",
            _ => tool.StartsWith("mcp__", StringComparison.Ordinal) ? "조회 중" : "실행 중",
        };
        var name = tool.StartsWith("mcp__", StringComparison.Ordinal)
            ? tool.Split("__", StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? tool
            : tool;
        var target = Target(tool, input);
        return target.Length == 0 ? $"{name} {verb}" : $"{name} {verb}: {target}";
    }

    public static string Detail(string tool, JsonObject input)
    {
        switch (tool)
        {
            case "Edit":
                var old = Text(input, "old_string");
                var @new = Text(input, "new_string");
                return $"바꿀 부분 {LineCount(old)}줄 → {LineCount(@new)}줄\n- {Short(old, 160)}\n+ {Short(@new, 160)}";
            case "MultiEdit":
                return $"수정 {(input["edits"] as JsonArray)?.Count ?? 0}곳";
            case "Write":
                var content = Text(input, "content");
                return $"{LineCount(content)}줄을 씀\n{Short(content, 200)}";
            case "Bash":
                return Text(input, "description");
            case "PowerShell":
            {
                // 요약(Target)은 120자에서 자르므로, 승인 카드에서 명령 전체를 볼 수 있게 자르지 않고 보여 준다.
                var command = Text(input, "command");
                var description = Text(input, "description");
                return description.Length == 0 ? command : command.Length == 0 ? description : command + "\n— " + description;
            }
            default:
                return Short(Json.Sorted(input)?.ToJsonString(Json.Compact) ?? "", 240);
        }
    }

    public static PermissionRequest? PermissionRequest(JsonObject obj)
    {
        if (Json.Str(obj["type"]) != "control_request" || Json.Str(obj["request_id"]) is not string id
            || obj["request"] is not JsonObject request || Json.Str(request["subtype"]) != "can_use_tool"
            || Json.Str(request["tool_name"]) is not string tool) return null;
        var input = request["input"] as JsonObject ?? new JsonObject();
        var suggestions = (request["permission_suggestions"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Where(s => Json.Str(s["type"]) == "addRules" && Json.Str(s["behavior"]) == "allow").ToList();
        var rules = suggestions.SelectMany(s => (s["rules"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            .Select(rule =>
            {
                if (Json.Str(rule["toolName"]) is not string name) return null;
                var content = Json.Str(rule["ruleContent"]) ?? "";
                return content.Length == 0 ? name : $"{name}({content})";
            })
            .Where(r => r != null).Select(r => r!).ToList();
        var summary = Target(tool, input);
        var suggestionArray = new JsonArray(suggestions.Select(s => Json.Clone(s)).ToArray());
        return new PermissionRequest(id, tool, summary.Length == 0 ? tool : summary, Detail(tool, input), rules,
                                     input.ToJsonString(Json.Compact), suggestionArray.ToJsonString(Json.Compact));
    }

    /// 권한 요청에 대한 control_response 한 줄 (끝에 줄바꿈).
    public static string Response(PermissionRequest request, PermissionDecision decision)
    {
        JsonObject answer;
        switch (decision)
        {
            case PermissionDecision.Deny deny:
                answer = new JsonObject { ["behavior"] = "deny", ["message"] = deny.Message };
                break;
            case PermissionDecision.AllowRules allowRules:
                answer = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = Json.ParseObject(request.Input) ?? new JsonObject() };
                var parsed = allowRules.Rules.Select(rule =>
                {
                    var (toolName, content) = AllowRules.Split(rule);
                    var item = new JsonObject { ["toolName"] = toolName };
                    if (content != null) item["ruleContent"] = content;
                    return (JsonNode?)item;
                }).ToArray();
                if (parsed.Length > 0)
                    answer["updatedPermissions"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "addRules", ["rules"] = new JsonArray(parsed), ["behavior"] = "allow", ["destination"] = "session",
                    });
                break;
            default:   // Allow, AllowAlways
                answer = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = Json.ParseObject(request.Input) ?? new JsonObject() };
                if (decision is PermissionDecision.AllowAlways && Json.ParseArray(request.Suggestions) is { Count: > 0 } suggestions)
                {
                    var copies = suggestions.OfType<JsonObject>().Select(s =>
                    {
                        var copy = (JsonObject)Json.Clone(s)!;
                        copy["destination"] = "session";
                        return (JsonNode?)copy;
                    }).ToArray();
                    answer["updatedPermissions"] = new JsonArray(copies);
                }
                break;
        }
        var message = new JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = request.Id, ["response"] = answer },
        };
        return message.ToJsonString(Json.Compact) + "\n";
    }

    public static List<(string Name, JsonObject Input)> ToolUses(JsonObject obj)
    {
        var uses = new List<(string, JsonObject)>();
        if (Json.Str(obj["type"]) != "assistant" || obj["message"]?["content"] is not JsonArray content) return uses;
        foreach (var c in content.OfType<JsonObject>())
        {
            if (Json.Str(c["type"]) != "tool_use" || Json.Str(c["name"]) is not string name) continue;
            uses.Add((name, Json.Clone(c["input"]) as JsonObject ?? new JsonObject()));
        }
        return uses;
    }
}
