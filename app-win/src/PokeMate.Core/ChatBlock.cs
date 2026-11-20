using System.Text.RegularExpressions;

namespace PokeMate.Core;

/// 채팅 답변 속 ```pokemate-<이름> 블록 (제안 카드가 되는 부분).
public static class ChatBlock
{
    public const string Tag = "pokemate";
    public const string LegacyTag = "pocketmate";

    public static string Pattern(string name) => $"```(?:{Tag}|{LegacyTag})-{name}[ \t]*\n(.*?)```";

    /// 블록 찾기 (Swift `.dotMatchesLineSeparators`)
    public static Regex Regex(string name) => new(Pattern(name), RegexOptions.Singleline);

    /// 답변에서 블록을 모두 떼어 낸 글 (앞뒤 공백 정리)
    internal static string Strip(Regex block, string text) => block.Replace(text, "").Trim();
}
