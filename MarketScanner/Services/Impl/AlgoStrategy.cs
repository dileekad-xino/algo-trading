using MarketScanner.Config;
using MarketScanner.Models;
using MarketScanner.Utilities;
using MarketScanner.ViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Globalization;

namespace MarketScanner.Services.Impl;

/// <summary>
/// Central trade decision engine.
///
/// Indicators are pure/stateless.
/// AlgoStrategy is the ONLY place that:
/// - tracks runtime position state
/// - decides BUY / SELL / HOLD
/// - enforces entry and staged exit rules
/// </summary>
public sealed class AlgoStrategy : IAlgoStrategy
{
    private readonly IReadOnlyList<IAlgoStrategy> _strategies;
    private readonly ICciSettingsService _cciSettingsService;
    private readonly IAtrSettingsService _atrSettingsService;
    private readonly ICandlestickStorage _candlestickStorage;
    private readonly CandlestickConfig _candlestickConfig;
    private readonly ILogger<AlgoStrategy> _logger;
    private readonly ConcurrentDictionary<string, SymbolTradeState> _symbolStates = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "Hybrid Momentum + ATR Strategy";
    public string Description => "Closed-bar momentum setup + next-bar live ATR trigger entry + 3-stage ATR protection + bearish confirmation exit.";

    public AlgoStrategy(
        IEnumerable<IAlgoStrategy> strategies,
        ICciSettingsService cciSettingsService,
        IAtrSettingsService atrSettingsService,
        ICandlestickStorage candlestickStorage,
        CandlestickConfig candlestickConfig,
        ILogger<AlgoStrategy> logger)
    {
        _strategies = strategies.ToList();
        _cciSettingsService = cciSettingsService;
        _atrSettingsService = atrSettingsService;
        _candlestickStorage = candlestickStorage;
        _candlestickConfig = candlestickConfig;
        _logger = logger;
    }

    public async Task<AlgoResult> ExecuteAsync(
        ScannerRowViewModel symbol,
        bool hasOpenPosition = false,
        CancellationToken ct = default)
    {
        try
        {
            var cciSettingsTask = _cciSettingsService.GetAsync(symbol.Symbol, ct);
            var atrSettingsTask = _atrSettingsService.GetAsync(symbol.Symbol, ct);
            var strategyTasks = _strategies
                .Select(strategy => Task.Run(() => strategy.ExecuteAsync(symbol, hasOpenPosition, ct), ct))
                .ToArray();

            await Task.WhenAll(strategyTasks).ConfigureAwait(false);
            var results = strategyTasks.Select(t => t.Result).ToArray();
            var cciSettings = await cciSettingsTask.ConfigureAwait(false);
            var atrSettings = await atrSettingsTask.ConfigureAwait(false);

            return CombineResults(results, symbol, hasOpenPosition, cciSettings, atrSettings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AlgoStrategy failed for {Symbol}", symbol.Symbol);
            return Hold(symbol, $"Algo error: {ex.Message}");
        }
    }

    private AlgoResult CombineResults(
        IReadOnlyList<AlgoResult> results,
        ScannerRowViewModel symbol,
        bool hasOpenPosition,
        CciSettings cciSettings,
        AtrSettings atrSettings)
    {
        if (results.Count == 0)
            return Hold(symbol, "No indicator results");

        var cci = results.FirstOrDefault(r => r.CciValue.HasValue || !string.IsNullOrWhiteSpace(r.CciSignal));
        var ema = results.FirstOrDefault(r => r.Ema20Value.HasValue || !string.IsNullOrWhiteSpace(r.Ema20Signal));
        var atr = results.FirstOrDefault(r => r.AtrValue.HasValue || (r.Reason?.Contains("ATR", StringComparison.OrdinalIgnoreCase) ?? false));

        double? livePrice = symbol.LastPrice > 0 ? symbol.LastPrice : null;
        var previousClose = ResolvePreviousClose(symbol, ema);
        var (latestClosedBarTimestamp, latestClosedBarClose, previousClosedBarClose) = ResolveClosedBarSnapshot(symbol);

        var state = _symbolStates.GetOrAdd(symbol.Symbol, _ => new SymbolTradeState());

        if (!hasOpenPosition)
        {
            lock (state.Sync)
            {
                state.InPosition = false;
                return EvaluateFlatEntry(
                    results,
                    symbol,
                    cciSettings,
                    atrSettings,
                    cci,
                    ema,
                    atr,
                    livePrice,
                    previousClose,
                    latestClosedBarTimestamp,
                    latestClosedBarClose,
                    previousClosedBarClose,
                    state);
            }
        }

        lock (state.Sync)
        {
            state.InPosition = true;
            ClearEntrySetupState(state);

            // Recovery path: if we are already in position but runtime state is empty (e.g., restart),
            // initialize state from current price and current ATR snapshot when available.
            if (!state.EntryPrice.HasValue || !state.EntryAtr.HasValue)
            {
                if (!livePrice.HasValue)
                {
                    return BuildResult(
                        symbol,
                        AlgoAction.Hold,
                        "HOLD (in position): waiting for price to initialize 3-stage stops",
                        results,
                        cci,
                        ema,
                        atr,
                        previousClose);
                }

                if (atr?.AtrValue.HasValue != true)
                {
                    return BuildResult(
                        symbol,
                        AlgoAction.Hold,
                        "HOLD (in position): waiting for ATR to initialize 3-stage stops",
                        results,
                        cci,
                        ema,
                        atr,
                        previousClose);
                }

                InitializeStopStateFromEntry(state, livePrice.Value, atr!.AtrValue!.Value, atrSettings);
            }

            if (livePrice.HasValue && (!state.HighestPriceSinceEntry.HasValue || livePrice.Value > state.HighestPriceSinceEntry.Value))
            {
                state.HighestPriceSinceEntry = livePrice.Value;
            }

            return EvaluateExit(results, symbol, cciSettings, atrSettings, cci, ema, atr, livePrice, previousClose, state);
        }
    }

    private AlgoResult EvaluateFlatEntry(
        IReadOnlyList<AlgoResult> results,
        ScannerRowViewModel symbol,
        CciSettings cciSettings,
        AtrSettings atrSettings,
        AlgoResult? cci,
        AlgoResult? ema,
        AlgoResult? atr,
        double? livePrice,
        double? previousClose,
        DateTime? latestClosedBarTimestamp,
        double? latestClosedBarClose,
        double? previousClosedBarClose,
        SymbolTradeState state)
    {
        if (!livePrice.HasValue)
        {
            return BuildResult(symbol, AlgoAction.Hold, "HOLD (flat): close unavailable", results, cci, ema, atr, previousClose);
        }

        // Entry setup is valid only during the bar immediately after the setup bar.
        if (state.IsSetupReady &&
            state.SetupBarTimestamp.HasValue &&
            latestClosedBarTimestamp.HasValue &&
            latestClosedBarTimestamp.Value > state.SetupBarTimestamp.Value)
        {
            var expiredReason =
                $"HOLD: setup expired without live trigger | next bar open {Fmt(state.NextBarOpen)} | trigger {Fmt(state.EntryTriggerPrice)} not reached";
            ClearEntrySetupState(state);
            return BuildResult(symbol, AlgoAction.Hold, expiredReason, results, cci, ema, atr, previousClose);
        }

        if (state.IsSetupReady)
        {
            var setupAtr = state.SetupAtr ?? atr?.AtrValue;
            if (!setupAtr.HasValue)
            {
                return BuildResult(symbol, AlgoAction.Hold, "HOLD (setup ready): waiting for ATR to initialize live trigger", results, cci, ema, atr, previousClose);
            }

            var liveEntryMultiplier = AtrSettings.NormalizeLiveEntryAtrMultiplier(atrSettings.LiveEntryAtrMultiplier);
            if (!state.EntryWindowActive)
            {
                state.EntryWindowActive = true;
                state.NextBarOpen = livePrice.Value;
                state.EntryTriggerPrice = state.NextBarOpen.Value + (setupAtr.Value * liveEntryMultiplier);

                var armedReason =
                    $"HOLD (setup ready): entry window active | next bar open {Fmt(state.NextBarOpen)} | live trigger {Fmt(state.EntryTriggerPrice)} | waiting for live price confirmation";
                return BuildResult(symbol, AlgoAction.Hold, armedReason, results, cci, ema, atr, previousClose);
            }

            if (state.EntryTriggerPrice.HasValue && livePrice.Value >= state.EntryTriggerPrice.Value)
            {
                InitializeStopStateFromEntry(state, livePrice.Value, setupAtr.Value, atrSettings);
                var buyReason =
                    $"BUY: live entry triggered | next bar open {Fmt(state.NextBarOpen)} | setup ATR {Fmt(setupAtr)} | trigger {Fmt(state.EntryTriggerPrice)} | fill {Fmt(livePrice)}";
                ClearEntrySetupState(state);
                return BuildResult(symbol, AlgoAction.Buy, buyReason, results, cci, ema, atr, previousClose);
            }

            var waitingReason =
                $"HOLD (setup ready): entry window active | next bar open {Fmt(state.NextBarOpen)} | live trigger {Fmt(state.EntryTriggerPrice)} | waiting for live price confirmation";
            return BuildResult(symbol, AlgoAction.Hold, waitingReason, results, cci, ema, atr, previousClose);
        }

        var cciNow = cci?.CciValue;
        var d1 = cci?.CciDelta;
        var acc = cci?.CciAcceleration;
        var emaNow = ema?.Ema20Value;
        var emaPrev = ema?.PreviousEma20Value;
        var atrValue = atr?.AtrValue;
        var setupClose = latestClosedBarClose;
        var setupPrevClose = previousClosedBarClose ?? previousClose;

        var entryThreshold = CciSettings.NormalizeEntryThreshold(cciSettings.EntryThreshold);
        var entryMinDelta = CciSettings.NormalizeEntryMinDelta(cciSettings.EntryMinDelta);
        var impulseMultiplier = AtrSettings.NormalizeImpulseAtrMultiplier(atrSettings.ImpulseAtrMultiplier);
        var requireRisingEma = cciSettings.RequireRisingEma20;

        var hasCore = cciNow.HasValue && d1.HasValue && acc.HasValue && emaNow.HasValue && setupClose.HasValue && setupPrevClose.HasValue;

        bool cciOk = hasCore && cciNow!.Value > entryThreshold;
        bool d1Ok = hasCore && d1!.Value >= entryMinDelta;
        bool accOk = hasCore && acc!.Value >= 0;
        bool closeAboveEma = hasCore && setupClose.Value > emaNow!.Value;
        bool closeAbovePrev = hasCore && setupClose.Value > setupPrevClose!.Value;

        bool impulseOk = false;
        double? impulse = null;
        double? impulseThreshold = null;
        if (hasCore && atrValue.HasValue)
        {
            impulse = setupClose!.Value - setupPrevClose!.Value;
            impulseThreshold = atrValue.Value * impulseMultiplier;
            impulseOk = impulse.Value >= impulseThreshold.Value;
        }

        bool emaRisingOk = !requireRisingEma || (emaNow.HasValue && emaPrev.HasValue && emaNow.Value > emaPrev.Value);

        bool canEnter = hasCore && atrValue.HasValue && impulse.HasValue && impulseThreshold.HasValue
            && cciOk && d1Ok && accOk && closeAboveEma && closeAbovePrev && impulseOk && emaRisingOk;

        var closedBarReadyForEvaluation =
            latestClosedBarTimestamp.HasValue &&
            (!state.LastSetupEvaluationBarTimestamp.HasValue || latestClosedBarTimestamp.Value > state.LastSetupEvaluationBarTimestamp.Value);

        if (closedBarReadyForEvaluation)
        {
            state.LastSetupEvaluationBarTimestamp = latestClosedBarTimestamp;

            if (canEnter)
            {
                state.IsSetupReady = true;
                state.SetupConfirmedAt = DateTime.UtcNow;
                state.SetupBarTimestamp = latestClosedBarTimestamp;
                state.SetupAtr = atrValue;
                state.SetupClose = setupClose;
                state.EntryWindowActive = false;
                state.NextBarOpen = null;
                state.EntryTriggerPrice = null;

                var summary =
                    $"HOLD (setup ready): {symbol.Symbol} passed entry conditions on closed bar | awaiting live trigger next bar | setup close {Fmt(setupClose)} | setup ATR {Fmt(atrValue)}";
                return BuildResult(symbol, AlgoAction.Hold, summary, results, cci, ema, atr, previousClose);
            }
        }

        var holdSummary =
            "HOLD (flat): entry conditions not met: " +
            $"CCI>{entryThreshold:F0}={YesNo(cciOk)}, " +
            $"d1>={entryMinDelta:F0}={YesNo(d1Ok)}, " +
            $"acc>=0={YesNo(accOk)}, " +
            $"close>EMA20={YesNo(closeAboveEma)}, " +
            $"close>prevClose={YesNo(closeAbovePrev)}, " +
            $"impulse>=ATR*{impulseMultiplier:F2}={YesNo(impulseOk)}, " +
            $"EMA20 rising={YesNo(emaRisingOk)}";

        return BuildResult(symbol, AlgoAction.Hold, holdSummary, results, cci, ema, atr, previousClose);
    }

    private AlgoResult EvaluateExit(
        IReadOnlyList<AlgoResult> results,
        ScannerRowViewModel symbol,
        CciSettings cciSettings,
        AtrSettings atrSettings,
        AlgoResult? cci,
        AlgoResult? ema,
        AlgoResult? atr,
        double? close,
        double? previousClose,
        SymbolTradeState state)
    {
        if (!close.HasValue || !state.EntryPrice.HasValue || !state.EntryAtr.HasValue || !state.HighestPriceSinceEntry.HasValue ||
            !state.InitialStop.HasValue || !state.ProfitLockArmPrice.HasValue || !state.ProfitLockStop.HasValue || !state.TrailingArmPrice.HasValue)
        {
            return BuildResult(symbol, AlgoAction.Hold, "HOLD (in position): waiting for stop state initialization", results, cci, ema, atr, previousClose);
        }

        // Stage progression
        if (!state.IsProfitLockArmed && close.Value >= state.ProfitLockArmPrice.Value)
        {
            state.IsProfitLockArmed = true;
        }

        if (!state.IsTrailingArmed && state.HighestPriceSinceEntry.Value >= state.TrailingArmPrice.Value)
        {
            state.IsTrailingArmed = true;
        }

        if (state.IsTrailingArmed)
        {
            var trailingMultiplier = AtrSettings.NormalizeTrailingAtrMultiplier(atrSettings.TrailingAtrMultiplier);
            state.TrailingStop = state.HighestPriceSinceEntry.Value - (state.EntryAtr.Value * trailingMultiplier);
        }

        // Priority A: Full trailing stop
        if (state.IsTrailingArmed && state.TrailingStop.HasValue && close.Value <= state.TrailingStop.Value)
        {
            _symbolStates.TryRemove(symbol.Symbol, out _);
            var summary =
                $"SELL: trailing stop hit | entry {Fmt(state.EntryPrice)} | entry ATR {Fmt(state.EntryAtr)} | high {Fmt(state.HighestPriceSinceEntry)} | trail {Fmt(state.TrailingStop)} | close {Fmt(close)}";
            return BuildResult(symbol, AlgoAction.Sell, summary, results, cci, ema, atr, previousClose);
        }

        // Priority B: Profit-lock stop (while trailing is not armed yet)
        if (state.IsProfitLockArmed && !state.IsTrailingArmed && close.Value <= state.ProfitLockStop.Value)
        {
            _symbolStates.TryRemove(symbol.Symbol, out _);
            var summary =
                $"SELL: profit lock stop hit | entry {Fmt(state.EntryPrice)} | entry ATR {Fmt(state.EntryAtr)} | stop {Fmt(state.ProfitLockStop)} | close {Fmt(close)}";
            return BuildResult(symbol, AlgoAction.Sell, summary, results, cci, ema, atr, previousClose);
        }

        // Priority C: Initial stop (before profit-lock/trailing)
        if (!state.IsProfitLockArmed && !state.IsTrailingArmed && close.Value <= state.InitialStop.Value)
        {
            _symbolStates.TryRemove(symbol.Symbol, out _);
            var summary =
                $"SELL: initial stop hit | entry {Fmt(state.EntryPrice)} | entry ATR {Fmt(state.EntryAtr)} | stop {Fmt(state.InitialStop)} | close {Fmt(close)}";
            return BuildResult(symbol, AlgoAction.Sell, summary, results, cci, ema, atr, previousClose);
        }

        // Priority D: hard bearish confirmation
        var d1 = cci?.CciDelta;
        var acc = cci?.CciAcceleration;
        var emaNow = ema?.Ema20Value;

        bool bearish = d1.HasValue && acc.HasValue && emaNow.HasValue && previousClose.HasValue
                       && d1.Value < 0
                       && acc.Value < 0
                       && close.Value < emaNow.Value
                       && close.Value < previousClose.Value;

        if (bearish)
        {
            _symbolStates.TryRemove(symbol.Symbol, out _);
            var summary =
                $"SELL: momentum reversal confirmed | d1 {Fmt(d1)} < 0, acc {Fmt(acc)} < 0, close {Fmt(close)} < EMA20 {Fmt(emaNow)}, close < prevClose";
            return BuildResult(symbol, AlgoAction.Sell, summary, results, cci, ema, atr, previousClose);
        }

        // Stage-specific HOLD reason text
        if (state.IsTrailingArmed)
        {
            var summary =
                $"HOLD (in position): trailing active | entry {Fmt(state.EntryPrice)} | entry ATR {Fmt(state.EntryAtr)} | high {Fmt(state.HighestPriceSinceEntry)} | trail {Fmt(state.TrailingStop)} | close {Fmt(close)}";
            return BuildResult(symbol, AlgoAction.Hold, summary, results, cci, ema, atr, previousClose);
        }

        if (state.IsProfitLockArmed)
        {
            var trailingArmReachedByHigh = state.HighestPriceSinceEntry.Value >= state.TrailingArmPrice.Value;
            var summary =
                $"HOLD (in position): profit lock active | entry {Fmt(state.EntryPrice)} | entry ATR {Fmt(state.EntryAtr)} | high {Fmt(state.HighestPriceSinceEntry)} | stop {Fmt(state.ProfitLockStop)} | trailing arm {Fmt(state.TrailingArmPrice)} {(trailingArmReachedByHigh ? "reached by high" : "not yet reached by high")} | close {Fmt(close)}";
            return BuildResult(symbol, AlgoAction.Hold, summary, results, cci, ema, atr, previousClose);
        }

        var initialSummary =
            $"HOLD (in position): initial stop active | entry {Fmt(state.EntryPrice)} | entry ATR {Fmt(state.EntryAtr)} | initial stop {Fmt(state.InitialStop)} | profit-lock arm {Fmt(state.ProfitLockArmPrice)} | profit-lock stop {Fmt(state.ProfitLockStop)} | trailing arm {Fmt(state.TrailingArmPrice)} | high {Fmt(state.HighestPriceSinceEntry)} | close {Fmt(close)}";
        return BuildResult(symbol, AlgoAction.Hold, initialSummary, results, cci, ema, atr, previousClose);
    }

    private static void InitializeStopStateFromEntry(SymbolTradeState state, double entryPrice, double entryAtr, AtrSettings atrSettings)
    {
        var initialStopMultiplier = AtrSettings.NormalizeInitialStopAtrMultiplier(atrSettings.InitialStopAtrMultiplier);
        var profitLockArmMultiplier = AtrSettings.NormalizeProfitLockArmAtrMultiplier(atrSettings.ProfitLockArmAtrMultiplier);
        var profitLockStopMultiplier = AtrSettings.NormalizeProfitLockStopAtrMultiplier(atrSettings.ProfitLockStopAtrMultiplier);
        var trailingArmMultiplier = AtrSettings.NormalizeTrailingArmAtrMultiplier(atrSettings.TrailingArmAtrMultiplier);

        state.InPosition = true;
        state.EntryPrice = entryPrice;
        state.EntryAtr = entryAtr;
        state.HighestPriceSinceEntry = entryPrice;

        state.InitialStop = entryPrice - (entryAtr * initialStopMultiplier);
        state.ProfitLockArmPrice = entryPrice + (entryAtr * profitLockArmMultiplier);
        state.IsProfitLockArmed = false;
        state.ProfitLockStop = entryPrice + (entryAtr * profitLockStopMultiplier);

        state.TrailingArmPrice = entryPrice + (entryAtr * trailingArmMultiplier);
        state.IsTrailingArmed = false;
        state.TrailingStop = null;
    }

    private static void ClearEntrySetupState(SymbolTradeState state)
    {
        state.IsSetupReady = false;
        state.SetupConfirmedAt = null;
        state.SetupBarTimestamp = null;
        state.SetupAtr = null;
        state.SetupClose = null;
        state.NextBarOpen = null;
        state.EntryTriggerPrice = null;
        state.EntryWindowActive = false;
    }

    private AlgoResult BuildResult(
        ScannerRowViewModel symbol,
        AlgoAction action,
        string summary,
        IReadOnlyList<AlgoResult> allResults,
        AlgoResult? cci,
        AlgoResult? ema,
        AlgoResult? atr,
        double? previousClose)
    {
        var indicatorReasons = string.Join(" | ", allResults.Select(r => $"[{r.Action}] {r.Reason}"));

        return new AlgoResult(
            Symbol: symbol.Symbol,
            Action: action,
            Price: symbol.LastPrice,
            Reason: $"{summary} | {indicatorReasons}",
            Timestamp: DateTime.UtcNow,
            CciValue: cci?.CciValue,
            CciSignal: cci?.CciSignal,
            PreviousCciValue: cci?.PreviousCciValue,
            PreviousCciValue2: cci?.PreviousCciValue2,
            CciDelta: cci?.CciDelta,
            CciDeltaPrevious: cci?.CciDeltaPrevious,
            CciAcceleration: cci?.CciAcceleration,
            Ema20Value: ema?.Ema20Value,
            PreviousEma20Value: ema?.PreviousEma20Value,
            Ema20Signal: ema?.Ema20Signal,
            AtrValue: atr?.AtrValue,
            PreviousClose: previousClose,
            Macd: allResults.FirstOrDefault(r => r.Macd != null)?.Macd,
            MacdSignal: allResults.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.MacdSignal))?.MacdSignal
        );
    }

    private double? ResolvePreviousClose(ScannerRowViewModel symbol, AlgoResult? ema)
    {
        if (ema?.PreviousClose.HasValue == true)
            return ema.PreviousClose.Value;

        var interval = TimeframeMap.ToIntervalKey(_candlestickConfig.IntervalSeconds);
        var candles = _candlestickStorage
            .GetCandlesticks(symbol.Symbol, interval, 2)
            .OrderBy(c => c.Timestamp)
            .ToList();

        if (candles.Count >= 2)
            return (double)candles[^2].Close;

        return symbol.PrevClose > 0 ? symbol.PrevClose : null;
    }

    private (DateTime? LatestTimestamp, double? LatestClose, double? PreviousClose) ResolveClosedBarSnapshot(ScannerRowViewModel symbol)
    {
        var interval = TimeframeMap.ToIntervalKey(_candlestickConfig.IntervalSeconds);
        var candles = _candlestickStorage
            .GetCandlesticks(symbol.Symbol, interval, 2)
            .OrderBy(c => c.Timestamp)
            .ToList();

        if (candles.Count == 0)
            return (null, null, null);

        var latest = candles[^1];
        var previous = candles.Count >= 2 ? candles[^2] : (Candlestick?)null;
        return (latest.Timestamp, (double)latest.Close, previous is null ? null : (double)previous.Close);
    }

    private static AlgoResult Hold(ScannerRowViewModel symbol, string reason) =>
        new(
            Symbol: symbol.Symbol,
            Action: AlgoAction.Hold,
            Price: symbol.LastPrice,
            Reason: reason,
            Timestamp: DateTime.UtcNow
        );

    private static string YesNo(bool value) => value ? "Yes" : "No";

    private static string Fmt(double? value) => value.HasValue
        ? value.Value.ToString("0.00", CultureInfo.InvariantCulture)
        : "n/a";

    private sealed class SymbolTradeState
    {
        public object Sync { get; } = new();

        public bool InPosition { get; set; }
        public double? EntryPrice { get; set; }
        public double? EntryAtr { get; set; }
        public double? HighestPriceSinceEntry { get; set; }

        public double? InitialStop { get; set; }

        public double? ProfitLockArmPrice { get; set; }
        public bool IsProfitLockArmed { get; set; }
        public double? ProfitLockStop { get; set; }

        public double? TrailingArmPrice { get; set; }
        public bool IsTrailingArmed { get; set; }
        public double? TrailingStop { get; set; }

        public bool IsSetupReady { get; set; }
        public DateTime? SetupConfirmedAt { get; set; }
        public DateTime? SetupBarTimestamp { get; set; }
        public DateTime? LastSetupEvaluationBarTimestamp { get; set; }
        public double? SetupAtr { get; set; }
        public double? SetupClose { get; set; }
        public double? NextBarOpen { get; set; }
        public double? EntryTriggerPrice { get; set; }
        public bool EntryWindowActive { get; set; }
    }
}
