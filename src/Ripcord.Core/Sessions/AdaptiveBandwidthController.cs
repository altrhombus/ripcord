namespace Ripcord.Core.Sessions;

/// <summary>
/// One rung on the quality ladder. Descending the ladder trades pixels for reliability; the fps drop only
/// happens at the bottom, because a lower frame rate is far more noticeable in play than a softer image.
/// </summary>
public readonly record struct QualityRung(int Width, int Height, int Fps, int BitrateKbps)
{
    public override string ToString() => $"{Height}p{Fps} @ {BitrateKbps / 1000.0:0.#} Mbps";
}

/// <summary>
/// Adaptive bitrate/resolution with power and thermal awareness — the real implementation of
/// <see cref="IBandwidthController"/>, replacing a stub that ignored every sample and echoed the initial
/// config back forever.
///
/// <para>
/// The design goals, in priority order:
/// <list type="number">
///   <item><description><b>React fast to trouble, return slowly to quality.</b> Loss is felt immediately and
///   is unambiguous, so a bad sample steps down at once. Recovery requires a sustained clean streak, because
///   stepping back up into a marginal link produces an oscillation the user sees as repeated glitching — worse
///   than simply sitting one rung low.</description></item>
///   <item><description><b>Never oscillate.</b> A cooldown after any change, plus asymmetric thresholds
///   (hysteresis), so the controller cannot ping-pong between two rungs.</description></item>
///   <item><description><b>Respect the device, not just the link.</b> On battery, and harder under thermal
///   throttling, the ladder is capped — on a handheld, sustained max quality is the difference between a
///   two-hour session and a one-hour one, and throttle stutter gets blamed on the network.</description></item>
/// </list>
/// </para>
///
/// <para>Pure and deterministic (time is injected), so the whole policy is unit-testable off-device.</para>
/// </summary>
public sealed class AdaptiveBandwidthController : IBandwidthController
{
    /// <summary>Loss at or above this is treated as "the link cannot carry the current rate".</summary>
    public const double StepDownLossRatio = 0.02;

    /// <summary>
    /// Fewest observed A/V units before a loss ratio is treated as meaningful.
    ///
    /// <para>
    /// Set above 1/<see cref="StepDownLossRatio"/> (= 50) so that a single lost unit can never on its own exceed
    /// the step-down threshold. Below this the ratio is noise: the first congestion window after connect held
    /// around eight units, where one loss reads as 12% and cost a healthy stream a quality rung immediately on
    /// connect.
    /// </para>
    /// </summary>
    public const int MinimumUnitsForLossDecision = 100;

    /// <summary>Loss must stay below this (lower than the step-down threshold — hysteresis) to earn a step up.</summary>
    public const double StepUpLossRatio = 0.005;

    /// <summary>RTT above this suppresses upward moves: the link is already queueing.</summary>
    public const double StepUpMaxRttMs = 80;

    /// <summary>Minimum spacing between any two changes, so a change can be observed before the next.</summary>
    public static readonly TimeSpan ChangeCooldown = TimeSpan.FromSeconds(4);

    /// <summary>How long the link must stay clean before stepping back up.</summary>
    public static readonly TimeSpan CleanStreakForStepUp = TimeSpan.FromSeconds(12);

    /// <summary>Battery caps the ladder here; thermal throttling caps it one rung lower again.</summary>
    public const int BatteryMaxHeight = 720;

    /// <summary>
    /// Battery charge at or below which the height cap applies. Being on battery is not by itself a reason to
    /// halve resolution: capping at any charge overrode an explicit 1080p request on a 95%-charged laptop with a
    /// flawless link, which reads as the app ignoring you rather than looking after your battery. Energy saver
    /// and critical battery still cap at any charge, because those are explicit or urgent rather than inferred.
    /// </summary>
    public const int BatteryCapAtOrBelowPercent = 30;

    private readonly List<QualityRung> _ladder;
    private readonly Func<DateTimeOffset> _clock;

    private int _index;                    // current rung; 0 = highest quality
    private DateTimeOffset _lastChange;
    private DateTimeOffset _cleanSince;
    private PowerState _power = new(PowerSource.ExternalPower, null, ThermalThrottling: false);
    private bool _haveCleanBaseline;

    /// <param name="initial">The session's requested configuration; becomes the top of the ladder.</param>
    /// <param name="clock">Injected time source, so tests need no delays.</param>
    public AdaptiveBandwidthController(SessionConfig initial, Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _ladder = BuildLadder(initial);
        _lastChange = _clock();
        _cleanSince = _lastChange;
    }

    /// <summary>The rung currently in effect.</summary>
    public QualityRung Current => _ladder[EffectiveIndex()];

    /// <summary>Human-readable reason for the most recent decision, for the diagnostics overlay.</summary>
    public string LastReason { get; private set; } = "starting at the requested quality";

    /// <inheritdoc />
    public string LastDecisionReason => LastReason;

    /// <summary>
    /// Ladder derived from the requested config: full rate, then two bitrate-only reductions (keeping
    /// resolution, which reads as "slightly softer" rather than "blurry"), then a resolution drop, then a
    /// frame-rate drop as the last resort.
    /// </summary>
    private static List<QualityRung> BuildLadder(SessionConfig c)
    {
        int w = c.Width, h = c.Height, fps = c.TargetFps, br = c.InitialBitrateKbps;
        var rungs = new List<QualityRung>
        {
            new(w, h, fps, br),
            new(w, h, fps, (int)(br * 0.7)),
            new(w, h, fps, (int)(br * 0.5)),
        };

        // A 720p step, if the request was taller than that.
        if (h > 720)
        {
            rungs.Add(new QualityRung(1280, 720, fps, Math.Max(4_000, (int)(br * 0.45))));
            rungs.Add(new QualityRung(1280, 720, fps, Math.Max(3_000, (int)(br * 0.3))));
        }

        // Then 540p, then halve the frame rate — last, because judder is worse than softness.
        rungs.Add(new QualityRung(960, 540, fps, 3_000));
        rungs.Add(new QualityRung(960, 540, Math.Max(30, fps / 2), 2_000));
        return rungs;
    }

    public void ReportNetworkSample(NetworkSample sample)
    {
        DateTimeOffset now = _clock();

        // Too small a sample carries no information, so do nothing at all with it — not even restart the clean
        // streak, which would let a meaningless window delay a legitimate step back up.
        if (sample.ObservedUnits < MinimumUnitsForLossDecision)
        {
            return;
        }

        bool bad = sample.PacketLossRatio >= StepDownLossRatio;
        bool clean = sample.PacketLossRatio <= StepUpLossRatio && sample.RoundTripTimeMs <= StepUpMaxRttMs;

        if (!clean)
        {
            // Any non-clean sample restarts the streak, so a single blip defers recovery.
            _cleanSince = now;
            _haveCleanBaseline = true;
        }
        else if (!_haveCleanBaseline)
        {
            _cleanSince = now;
            _haveCleanBaseline = true;
        }

        if (now - _lastChange < ChangeCooldown)
        {
            return; // let the previous change take effect before judging it
        }

        if (bad && _index < _ladder.Count - 1)
        {
            _index++;
            _lastChange = now;
            _cleanSince = now;
            LastReason = $"stepped down to {_ladder[_index]} after {sample.PacketLossRatio:P1} packet loss";
            return;
        }

        if (clean && _index > 0 && now - _cleanSince >= CleanStreakForStepUp)
        {
            _index--;
            _lastChange = now;
            _cleanSince = now;
            LastReason = $"stepped up to {_ladder[_index]} after a sustained clean link";
        }
    }

    public void ReportThermalPowerState(PowerState state)
    {
        bool wasCapped = EffectiveIndex() != _index;
        _power = state;
        bool nowCapped = EffectiveIndex() != _index;

        if (nowCapped && !wasCapped)
        {
            LastReason = DescribeCap(state);
        }
        else if (!nowCapped && wasCapped)
        {
            LastReason = $"device limit lifted — back to {Current}";
        }
    }

    /// <summary>Name the specific device reason for a cap, so the UI and the trace can say which one applies.</summary>
    private string DescribeCap(PowerState state) => state switch
    {
        { ThermalThrottling: true } => $"limited to {Current} because the device is thermally throttling",
        { BatteryCritical: true } => $"limited to {Current} because the battery is critically low",
        { EnergySaverActive: true } => $"limited to {Current} because energy saver is on",
        { BatteryPercent: int p } => $"limited to {Current} because the battery is low ({p}%)",
        _ => $"limited to {Current} to save battery",
    };

    public BitrateDecision RecommendBitrate()
    {
        QualityRung rung = Current;
        return new BitrateDecision(rung.BitrateKbps, rung.Width, rung.Height, rung.Fps);
    }

    /// <summary>
    /// The rung actually in force: the network-chosen rung, floored by any device cap. The network index is
    /// left untouched so that unplugging and replugging restores the previously earned quality rather than
    /// forcing the ladder to be re-climbed.
    /// </summary>
    private int EffectiveIndex()
    {
        int index = _index;

        // On battery, cap the ladder height — but only once the charge is actually low. An unknown percentage
        // reads as "don't know", which is not grounds to cap on a guess (the same principle by which
        // WindowsPowerThermalMonitor treats an unknown AC line as external power).
        if (_power.Source == PowerSource.Battery
            && _power.BatteryPercent is int percent
            && percent <= BatteryCapAtOrBelowPercent)
        {
            index = Math.Max(index, FirstIndexAtOrBelow(BatteryMaxHeight));
        }

        // Any of these means "conserve harder": genuine thermal throttling, the user explicitly turning on
        // energy saver, or a critical battery. Each drops one further rung below the battery cap. Energy saver
        // is treated as strongly as thermal deliberately — it is an explicit instruction rather than an
        // inference, and unlike thermal state Windows reports it precisely.
        if (_power.ThermalThrottling || _power.EnergySaverActive || _power.BatteryCritical)
        {
            index = Math.Min(_ladder.Count - 1, Math.Max(index, FirstIndexAtOrBelow(BatteryMaxHeight)) + 1);
        }

        return Math.Clamp(index, 0, _ladder.Count - 1);
    }

    /// <summary>Index of the first rung no taller than <paramref name="maxHeight"/>, or the top if none is.</summary>
    private int FirstIndexAtOrBelow(int maxHeight)
    {
        for (int i = 0; i < _ladder.Count; i++)
        {
            if (_ladder[i].Height <= maxHeight)
            {
                return i;
            }
        }

        return 0;
    }
}
