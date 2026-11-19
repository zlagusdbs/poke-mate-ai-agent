namespace PokeMate.Core;

// Task 2 는 MateCLI.Events 가 쓰는 EventItem 만 옮겼다. Trigger·TriggerEvent·TriggerDiff 는 나중 작업이 채운다.

/// `pokemate events <id>` 출력 한 줄.
public sealed record EventItem(string Key, string Title, string Summary, string Url);
