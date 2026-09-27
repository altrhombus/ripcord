//! Adaptive bitrate and when to tell the console about it. Ported from `AdaptiveBandwidthController.cs`
//! and `ConnectionQualityReporter.cs` (`Ripcord.Core.Sessions`); the C core has neither. Pure, with time
//! passed in, so the policy is testable without a console.
//!
//! The ladder decides what the session would like the console to encode at. On the wire that is only ever
//! CONNECTION_QUALITY's target bitrate, whose unit is [X] (kbps, as the launch spec's `bwKbpsSent`; bps is as
//! likely), which is why sending it is opt-in, as in .NET. The resolution and frame rate a rung names are not
//! renegotiated mid-stream; they are reported for a host to show.

/// One rung. Descending trades pixels for reliability; the frame rate drops only at the bottom, because
/// judder is far more noticeable in play than a softer image.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Rung {
    pub width: i32,
    pub height: i32,
    pub fps: i32,
    pub bitrate_kbps: i32,
}

/// One congestion window's measurements.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Sample {
    pub rtt_ms: f64,
    /// Lost over received plus lost, 0..1.
    pub loss_ratio: f64,
    /// Received plus lost, so a real loss rate can be told from one lost unit in a tiny window.
    pub observed_units: u64,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum PowerSource {
    #[default]
    External,
    Battery,
}

/// What the host knows about its power and thermal state.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct PowerState {
    pub source: PowerSource,
    pub battery_percent: Option<u8>,
    pub thermal_throttling: bool,
    pub energy_saver: bool,
    pub battery_critical: bool,
}

/// Loss at or above this is "the link cannot carry the current rate".
pub const STEP_DOWN_LOSS: f64 = 0.02;
/// Loss must stay below this (hysteresis) to earn a step up.
pub const STEP_UP_LOSS: f64 = 0.005;
/// RTT above this suppresses upward moves: the link is already queueing.
pub const STEP_UP_MAX_RTT_MS: f64 = 80.0;
/// Above 1/STEP_DOWN_LOSS, so one lost unit can never by itself step down. The first window after connect
/// holds about eight units, where one loss reads as 12%.
pub const MIN_UNITS_FOR_DECISION: u64 = 100;
pub const CHANGE_COOLDOWN_US: u64 = 4_000_000;
pub const CLEAN_STREAK_US: u64 = 12_000_000;
pub const BATTERY_MAX_HEIGHT: i32 = 720;
/// Being on battery is not by itself a reason to halve resolution; a low charge is.
pub const BATTERY_CAP_AT_OR_BELOW_PERCENT: u8 = 30;

pub struct Controller {
    ladder: Vec<Rung>,
    index: usize,
    last_change_us: u64,
    clean_since_us: u64,
    have_clean_baseline: bool,
    power: PowerState,
}

impl Controller {
    /// The ladder from the requested configuration: full rate, two bitrate-only cuts, a 720p step if the
    /// request was taller, then 540p, then half the frame rate.
    pub fn new(width: i32, height: i32, fps: i32, bitrate_kbps: i32, now_us: u64) -> Self {
        let rung = |w, h, f, b| Rung { width: w, height: h, fps: f, bitrate_kbps: b };
        let br = f64::from(bitrate_kbps);
        let mut ladder = vec![
            rung(width, height, fps, bitrate_kbps),
            rung(width, height, fps, (br * 0.7) as i32),
            rung(width, height, fps, (br * 0.5) as i32),
        ];
        if height > 720 {
            ladder.push(rung(1280, 720, fps, 4_000.max((br * 0.45) as i32)));
            ladder.push(rung(1280, 720, fps, 3_000.max((br * 0.3) as i32)));
        }
        ladder.push(rung(960, 540, fps, 3_000));
        ladder.push(rung(960, 540, 30.max(fps / 2), 2_000));
        Self {
            ladder,
            index: 0,
            last_change_us: now_us,
            clean_since_us: now_us,
            have_clean_baseline: false,
            power: PowerState::default(),
        }
    }

    pub fn ladder(&self) -> &[Rung] {
        &self.ladder
    }

    /// The rung in force: the network's choice, floored by any device cap.
    pub fn current(&self) -> Rung {
        self.ladder[self.effective_index()]
    }

    /// React fast to trouble and return slowly to quality: a bad window steps down at once (after the
    /// cooldown), and only a sustained clean streak steps back up.
    pub fn report(&mut self, now_us: u64, s: Sample) {
        if s.observed_units < MIN_UNITS_FOR_DECISION {
            return; // no information, and not even a reason to restart the clean streak
        }
        let bad = s.loss_ratio >= STEP_DOWN_LOSS;
        let clean = s.loss_ratio <= STEP_UP_LOSS && s.rtt_ms <= STEP_UP_MAX_RTT_MS;
        if !clean || !self.have_clean_baseline {
            self.clean_since_us = now_us;
            self.have_clean_baseline = true;
        }
        if now_us.saturating_sub(self.last_change_us) < CHANGE_COOLDOWN_US {
            return;
        }
        if bad && self.index < self.ladder.len() - 1 {
            self.index += 1;
            self.last_change_us = now_us;
            self.clean_since_us = now_us;
        } else if clean && self.index > 0 && now_us.saturating_sub(self.clean_since_us) >= CLEAN_STREAK_US {
            self.index -= 1;
            self.last_change_us = now_us;
            self.clean_since_us = now_us;
        }
    }

    /// The network index is left alone, so unplugging and replugging restores the earned quality.
    pub fn set_power(&mut self, power: PowerState) {
        self.power = power;
    }

    fn effective_index(&self) -> usize {
        let mut index = self.index;
        let capped = self.first_at_or_below(BATTERY_MAX_HEIGHT);
        if self.power.source == PowerSource::Battery
            && self.power.battery_percent.is_some_and(|p| p <= BATTERY_CAP_AT_OR_BELOW_PERCENT)
        {
            index = index.max(capped);
        }
        if self.power.thermal_throttling || self.power.energy_saver || self.power.battery_critical {
            index = (index.max(capped) + 1).min(self.ladder.len() - 1);
        }
        index.min(self.ladder.len() - 1)
    }

    fn first_at_or_below(&self, max_height: i32) -> usize {
        self.ladder.iter().position(|r| r.height <= max_height).unwrap_or(0)
    }
}

/// What a CONNECTION_QUALITY report says.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Report {
    pub target_bitrate_kbps: u32,
    pub rtt_ms: f64,
    /// 0..100.
    pub loss_percent: f64,
}

/// Resend unchanged state at least this often.
pub const REFRESH_US: u64 = 2_000_000;
/// Floor between any two reports, so an oscillating ladder cannot burst control messages at the moment the
/// uplink can least carry them.
pub const MIN_SPACING_US: u64 = 500_000;
/// A relative change in target below this is ladder noise.
pub const SIGNIFICANT_CHANGE: f64 = 0.05;

/// When to report: the first at once, a significant change promptly (past the spacing floor), and
/// unchanged state on the refresh interval.
#[derive(Default)]
pub struct Reporter {
    last_sent_us: u64,
    last_target_kbps: i32,
    have_sent: bool,
    pub reports_sent: u64,
}

impl Reporter {
    pub fn next(&mut self, now_us: u64, target_kbps: i32, rtt_ms: f64, loss_percent: f64) -> Option<Report> {
        if target_kbps <= 0 {
            return None;
        }
        if self.have_sent {
            let since = now_us.saturating_sub(self.last_sent_us);
            if since < MIN_SPACING_US {
                return None;
            }
            if !significant(self.last_target_kbps, target_kbps) && since < REFRESH_US {
                return None;
            }
        }
        self.last_sent_us = now_us;
        self.last_target_kbps = target_kbps;
        self.have_sent = true;
        self.reports_sent += 1;
        Some(Report {
            target_bitrate_kbps: target_kbps as u32,
            rtt_ms: rtt_ms.max(0.0),
            loss_percent: loss_percent.clamp(0.0, 100.0),
        })
    }
}

pub fn significant(previous_kbps: i32, current_kbps: i32) -> bool {
    previous_kbps <= 0
        || f64::from((current_kbps - previous_kbps).abs()) / f64::from(previous_kbps) >= SIGNIFICANT_CHANGE
}

#[cfg(test)]
mod tests {
    use super::*;

    const S: u64 = 1_000_000;

    fn window(loss: f64, rtt: f64) -> Sample {
        Sample { rtt_ms: rtt, loss_ratio: loss, observed_units: 500 }
    }

    #[test]
    fn the_ladder_from_a_1080p_request() {
        let c = Controller::new(1920, 1080, 60, 25_000, 0);
        let heights: Vec<i32> = c.ladder().iter().map(|r| r.height).collect();
        assert_eq!(heights, [1080, 1080, 1080, 720, 720, 540, 540]);
        assert_eq!(c.ladder()[1].bitrate_kbps, 17_500);
        assert_eq!(c.ladder()[3].bitrate_kbps, 11_250);
        assert_eq!(c.ladder()[6].fps, 30);
        assert_eq!(Controller::new(1280, 720, 60, 10_000, 0).ladder().len(), 5, "no 720p step below 1080p");
    }

    #[test]
    fn loss_steps_down_after_the_cooldown_and_a_clean_streak_steps_back_up() {
        let mut c = Controller::new(1920, 1080, 60, 25_000, 0);
        c.report(S, window(0.05, 10.0));
        assert_eq!(c.current().bitrate_kbps, 25_000, "inside the first cooldown");
        c.report(5 * S, window(0.05, 10.0));
        assert_eq!(c.current().bitrate_kbps, 17_500);
        c.report(6 * S, window(0.05, 10.0));
        assert_eq!(c.current().bitrate_kbps, 17_500, "a change is observed before the next");
        // A tiny window says nothing, even at 100% loss.
        c.report(20 * S, Sample { rtt_ms: 10.0, loss_ratio: 1.0, observed_units: 8 });
        assert_eq!(c.current().bitrate_kbps, 17_500);
        // Not clean but not bad at 21 s restarts the streak; clean from 22 s earns a rung 12 s after 21 s.
        c.report(21 * S, window(0.01, 10.0));
        assert_eq!(c.current().bitrate_kbps, 17_500, "1% is not bad enough to step down");
        for t in 22..33 {
            c.report(t * S, window(0.0, 10.0));
            assert_eq!(c.current().bitrate_kbps, 17_500, "still earning it at {t} s");
        }
        c.report(33 * S, window(0.0, 10.0));
        assert_eq!(c.current().bitrate_kbps, 25_000);
        // A slow link is not clean, however little it loses.
        c.report(40 * S, window(0.05, 10.0));
        for t in 41..70 {
            c.report(t * S, window(0.0, 120.0));
        }
        assert_eq!(c.current().bitrate_kbps, 17_500);
    }

    #[test]
    fn a_low_battery_caps_at_720p_and_throttling_one_rung_lower() {
        let mut c = Controller::new(1920, 1080, 60, 25_000, 0);
        c.set_power(PowerState {
            source: PowerSource::Battery,
            battery_percent: Some(95),
            ..Default::default()
        });
        assert_eq!(c.current().height, 1080, "a full battery is not a reason to halve resolution");
        c.set_power(PowerState {
            source: PowerSource::Battery,
            battery_percent: Some(20),
            ..Default::default()
        });
        assert_eq!(c.current().height, 720);
        assert_eq!(c.current().bitrate_kbps, 11_250);
        c.set_power(PowerState { thermal_throttling: true, ..Default::default() });
        assert_eq!(c.current().bitrate_kbps, 7_500);
        c.set_power(PowerState::default());
        assert_eq!(c.current().bitrate_kbps, 25_000, "the earned rung comes back");
    }

    #[test]
    fn reports_go_out_first_on_change_and_on_refresh() {
        let mut r = Reporter::default();
        assert!(r.next(0, 0, 5.0, 0.0).is_none(), "nothing to ask for yet");
        assert!(r.next(0, 25_000, 5.0, 150.0).is_some_and(|x| x.loss_percent == 100.0));
        assert!(r.next(400_000, 10_000, 5.0, 0.0).is_none(), "the spacing floor holds even for a change");
        assert!(r.next(600_000, 24_500, 5.0, 0.0).is_none(), "2% is noise");
        assert!(r.next(700_000, 17_500, 5.0, 0.0).is_some(), "30% is a decision");
        assert!(r.next(2_600_000, 17_500, 5.0, 0.0).is_none());
        assert!(r.next(2_700_000, 17_500, -3.0, 0.0).is_some_and(|x| x.rtt_ms == 0.0), "the refresh");
        assert_eq!(r.reports_sent, 3);
    }
}
