namespace PokeMate.Core;

// Task 2 는 AppSettings 가 쓰는 SessionReuse 의 값만 옮겼다. FreshReason(WorkSession …)·WorkSession·WorkSessionBook 등은 Task 4 에서 채운다.
// (RunUsage 는 Task 2 계획대로 TokenUsage.cs 에 있다.)

public sealed record SessionReuse
{
    public const int DefaultMinutes = 55;
    public const int DefaultTokens = 120_000;

    public const string ReviewerReason = "리뷰어는 늘 새로";

    public int IdleMinutes { get; set; }
    public int MaxTokens { get; set; }
    public bool FreshReviewer { get; set; }

    public SessionReuse(int idleMinutes = DefaultMinutes, int maxTokens = DefaultTokens, bool freshReviewer = true)
    {
        IdleMinutes = idleMinutes;
        MaxTokens = maxTokens;
        FreshReviewer = freshReviewer;
    }

    public SessionReuse(AppSettings settings)
        : this(settings.SessionReuseMinutes, settings.SessionReuseMaxTokens, settings.ReviewerAlwaysFresh) { }

    public static SessionReuse Always => new(0, 0, false);
}
