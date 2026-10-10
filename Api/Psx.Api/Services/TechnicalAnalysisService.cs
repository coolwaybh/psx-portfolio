using Psx.Api.Entities;

namespace Psx.Api.Services;

public record TechnicalIndicatorsDto(
    decimal LastClose,
    string LastDate,
    decimal? Sma20,
    decimal? Sma50,
    decimal? Sma200,
    decimal? Rsi14,
    decimal? MacdLine,
    decimal? MacdSignal,
    decimal? MacdHistogram,
    decimal? Support,
    decimal? Resistance
);

public record TechnicalAnalysisResult(
    string Signal,       // "bullish" | "neutral" | "bearish"
    int Score,            // 0-100, 50 = neutral baseline
    List<string> Reasons,
    TechnicalIndicatorsDto Indicators
);

// Deterministic technical-analysis engine computed from PSX end-of-day closing prices
// already cached by PsxHistoricalPriceService (no AI call, no extra data source - every
// input here is a plain arithmetic function of EodPrice history that's already being
// collected for the Cumulative P&L chart). Pure math -> fast and reproducible, unlike
// FundamentalAnalysisService's AI research call, so this is never rate-limited and is
// safe to recompute on every request (callers that want to avoid repeat PSX history
// fetches should still lean on PsxHistoricalPriceService's own EodPrice cache).
//
// Every indicator here is close-price-only (no OHLC/volume) - PsxHistoricalPriceService
// only captures (Date, Close) from PSX's timeseries/eod endpoint today, so intrabar
// true-range, volume-based signals, and classic candlestick patterns are deliberately
// out of scope until that fetcher is extended to capture more columns.
public static class TechnicalAnalysisService
{
    public static TechnicalAnalysisResult? Analyze(List<EodPrice> history)
    {
        if (history.Count == 0) return null;

        var closes = history.OrderBy(h => h.Date).Select(h => h.Close).ToList();
        var last = history.OrderBy(h => h.Date).Last();

        var sma20 = Sma(closes, 20);
        var sma50 = Sma(closes, 50);
        var sma200 = Sma(closes, 200);
        var rsi14 = Rsi(closes, 14);
        var (macdLine, macdSignal, macdHistogram) = Macd(closes);
        var (support, resistance) = SupportResistance(closes);

        var score = 50;
        var reasons = new List<string>();

        // ── Trend: price vs the moving averages, and their own stacking order ──
        if (sma50 is decimal s50 && sma200 is decimal s200)
        {
            if (last.Close > s50 && s50 > s200)
            {
                score += 15;
                reasons.Add("Price is above both the 50-day and 200-day moving averages, with the 50-day above the 200-day - a confirmed uptrend structure.");
            }
            else if (last.Close < s50 && s50 < s200)
            {
                score -= 15;
                reasons.Add("Price is below both the 50-day and 200-day moving averages, with the 50-day below the 200-day - a confirmed downtrend structure.");
            }
            else if (last.Close > s200)
            {
                score += 5;
                reasons.Add("Price is above its 200-day moving average, but the shorter-term trend is mixed.");
            }
            else
            {
                score -= 5;
                reasons.Add("Price is below its 200-day moving average, but the shorter-term trend is mixed.");
            }
        }
        else if (sma50 is decimal s50Only)
        {
            // Enough history for a 50-day read but not yet 200 days (symbol with
            // 50-199 cached trading days) - still worth a partial trend signal rather
            // than falling all the way back to the 20-day average.
            if (last.Close > s50Only) { score += 8; reasons.Add("Price is above its 50-day moving average (limited history - 200-day trend not yet available)."); }
            else { score -= 8; reasons.Add("Price is below its 50-day moving average (limited history - 200-day trend not yet available)."); }
        }
        else if (sma20 is decimal s20)
        {
            // Not enough history for the longer averages yet - fall back to the
            // shortest one rather than skipping trend entirely.
            if (last.Close > s20) { score += 5; reasons.Add("Price is above its 20-day moving average (limited history - longer-term trend not yet available)."); }
            else { score -= 5; reasons.Add("Price is below its 20-day moving average (limited history - longer-term trend not yet available)."); }
        }

        // ── Momentum: RSI(14) ──
        if (rsi14 is decimal rsi)
        {
            if (rsi >= 70)
            {
                score -= 10;
                reasons.Add($"RSI(14) is {rsi:0.#} - overbought territory, raising the risk of a near-term pullback.");
            }
            else if (rsi <= 30)
            {
                score += 8;
                reasons.Add($"RSI(14) is {rsi:0.#} - oversold, which has historically preceded a bounce (but can also mean a falling knife - check the trend above).");
            }
            else if (rsi is >= 45 and <= 65)
            {
                score += 8;
                reasons.Add($"RSI(14) is {rsi:0.#} - a healthy momentum zone, neither overbought nor oversold.");
            }
        }

        // ── Momentum: MACD crossover ──
        if (macdLine is decimal ml && macdSignal is decimal msig)
        {
            if (ml > msig)
            {
                score += 10;
                reasons.Add("MACD line is above its signal line - positive momentum.");
            }
            else
            {
                score -= 10;
                reasons.Add("MACD line is below its signal line - negative momentum.");
            }
        }

        // ── Position within the recent trading range ──
        // Support/resistance are computed from the window BEFORE today's close (see
        // SupportResistance below), so today's close can legitimately sit outside that
        // prior range - that's a breakout/breakdown, scored opposite to merely
        // approaching a level the price hasn't actually crossed yet.
        if (support is decimal sup && resistance is decimal res && res > sup)
        {
            if (last.Close > res)
            {
                score += 8;
                reasons.Add($"Price has broken out above its prior resistance level ({res:0.##}) - a bullish signal, not a ceiling.");
            }
            else if (last.Close < sup)
            {
                score -= 8;
                reasons.Add($"Price has broken down below its prior support level ({sup:0.##}) - a bearish signal.");
            }
            else
            {
                var pctFromSupport = (last.Close - sup) / (res - sup);
                if (pctFromSupport <= 0.15m)
                {
                    score += 5;
                    reasons.Add($"Price is trading near its recent support level ({sup:0.##}) - limited downside before that level is tested.");
                }
                else if (pctFromSupport >= 0.85m)
                {
                    score -= 5;
                    reasons.Add($"Price is trading near its recent resistance level ({res:0.##}) - limited upside before that level is tested.");
                }
            }
        }

        score = Math.Clamp(score, 0, 100);
        var signal = score switch
        {
            >= 65 => "bullish",
            <= 35 => "bearish",
            _ => "neutral"
        };

        var indicators = new TechnicalIndicatorsDto(
            last.Close, last.Date.ToString("yyyy-MM-dd"),
            Round(sma20), Round(sma50), Round(sma200),
            rsi14 is decimal r ? Math.Round(r, 1) : null,
            Round(macdLine), Round(macdSignal), Round(macdHistogram),
            Round(support), Round(resistance));

        return new TechnicalAnalysisResult(signal, score, reasons, indicators);
    }

    static decimal? Round(decimal? v) => v is decimal d ? Math.Round(d, 2) : null;

    static decimal? Sma(List<decimal> closes, int period)
    {
        if (closes.Count < period) return null;
        return closes.TakeLast(period).Average();
    }

    // Wilder's smoothing, the standard RSI formula (not a plain SMA of gains/losses) -
    // seeded by a simple average over the first `period` changes, then smoothed forward.
    static decimal? Rsi(List<decimal> closes, int period)
    {
        if (closes.Count < period + 1) return null;

        var changes = new List<decimal>();
        for (var i = 1; i < closes.Count; i++) changes.Add(closes[i] - closes[i - 1]);

        var gains = changes.Take(period).Select(c => c > 0 ? c : 0).Average();
        var losses = changes.Take(period).Select(c => c < 0 ? -c : 0).Average();

        for (var i = period; i < changes.Count; i++)
        {
            var change = changes[i];
            var gain = change > 0 ? change : 0;
            var loss = change < 0 ? -change : 0;
            gains = (gains * (period - 1) + gain) / period;
            losses = (losses * (period - 1) + loss) / period;
        }

        // Zero movement at all (a perfectly flat run) is a genuinely neutral market,
        // not "maximally overbought" - must be checked before the gains==0 case below,
        // which only means "overbought" when there WAS movement, all of it upward.
        if (gains == 0 && losses == 0) return 50;
        if (losses == 0) return 100;
        var rs = gains / losses;
        return 100 - 100 / (1 + rs);
    }

    static (decimal? Line, decimal? Signal, decimal? Histogram) Macd(List<decimal> closes)
    {
        if (closes.Count < 35) return (null, null, null); // 26 (slow EMA) + 9 (signal EMA) warm-up

        var ema12 = EmaSeries(closes, 12);
        var ema26 = EmaSeries(closes, 26);
        var macdSeries = ema12.Zip(ema26, (fast, slow) => fast - slow).ToList();
        // MACD line is only meaningful once both EMAs have warmed up (from index 25 on,
        // 0-based) - skip the noisy head before computing the signal EMA over it.
        var warmedMacd = macdSeries.Skip(25).ToList();
        var signalSeries = EmaSeries(warmedMacd, 9);

        var macdLine = macdSeries[^1];
        var signalLine = signalSeries[^1];
        return (macdLine, signalLine, macdLine - signalLine);
    }

    static List<decimal> EmaSeries(List<decimal> values, int period)
    {
        var k = 2m / (period + 1);
        var result = new List<decimal>(values.Count) { values[0] };
        for (var i = 1; i < values.Count; i++)
            result.Add(values[i] * k + result[i - 1] * (1 - k));
        return result;
    }

    // Simplest workable definition: the lowest/highest close over the ~3 months (60
    // trading days) BEFORE today, or the full prior history if shorter. Deliberately
    // excludes today's own close - including it would make "resistance" always equal
    // today's price on any new high, which the caller would then score as "at
    // resistance" instead of "broke out", exactly backwards. A real swing-point
    // detector (local minima/maxima) would be more precise but needs OHLC highs/lows,
    // which aren't captured yet (see the class-level comment).
    static (decimal? Support, decimal? Resistance) SupportResistance(List<decimal> closes)
    {
        if (closes.Count < 2) return (null, null);
        var priorCloses = closes.SkipLast(1).ToList();
        var window = priorCloses.TakeLast(Math.Min(60, priorCloses.Count)).ToList();
        return (window.Min(), window.Max());
    }
}
