using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: 단가
public static class PricingChecks
{
    public static void Run()
    {
        var buckets = new[]
        {
            new TokenBucket { Input = 1_000_000 }, new TokenBucket { Output = 1_000_000 },
            new TokenBucket { CacheWrite = 1_000_000 }, new TokenBucket { CacheRead = 1_000_000 },
        };
        var fable = buckets.Select(b => ClaudePricing.Cost("claude-fable-5-2", b)).Where(c => c != null).Select(c => Math.Round(c!.Value * 100) / 100);
        var sonnet = buckets.Select(b => ClaudePricing.Cost("claude-sonnet-5-5", b)).Where(c => c != null).Select(c => Math.Round(c!.Value * 100) / 100);
        Check.That(fable.SequenceEqual(new[] { 8, 40, 10, 0.2 }) && sonnet.SequenceEqual(new[] { 2, 10, 2.5, 0.1 }),
                   "pricing: Fable 5.2·Sonnet 5.5 단가 (백만 토큰당 입력·출력·캐시 쓰기·캐시 읽기)");
        Check.That(ClaudePricing.Key(" Anthropic/Claude-Sonnet-4 ") == "claude-sonnet-4-20250514" && ClaudePricing.Cost("gpt", new TokenBucket { Input = 1 }) == null,
                   "pricing: 별칭·모르는 모델");
    }
}
