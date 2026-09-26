//! The client: the connect sequence and the running session, as a host drives them. The contract is
//! `libripcord/client/halyard_client.h`'s, so the Mac can move engines by relinking, with these
//! differences:
//!
//! - **An opaque handle** from [`ripcord_client_new`], instead of storage the host sizes and places.
//! - **Key agreement and randomness are the host's**, through [`RipcordEcdhBackend`] (null: the engine's
//!   RustCrypto backend) and [`RipcordRandom`] (required: the platform's CSPRNG).
//! - **No `fds` export.** [`ripcord_client_pump`] waits on the sockets itself for at most the time it is
//!   given, so a host calls it in a loop on its pump thread.
//! - **The pairing record's fields are flattened** into [`RipcordClientConfig`]: the engine does not parse
//!   pairing files.
//!
//! Everything else holds: one host-owned thread calls connect and then pump until pump returns false;
//! every callback runs on that thread from inside those calls; every buffer passed to a callback is
//! borrowed for that call; and the host's commands, the pad and the passcode are pulled, never pushed.

use std::ffi::{CStr, CString, c_char, c_void};
use std::time::Duration;

use ripcord_net::{
    Answer, Client, Commands, Config, Controller, EndReason, Endpoint, Event, Host, Leg, LogLevel, Pairing,
    Peer, Route, Stage,
};
use ripcord_proto::crypto::ecdh::{Curve, Ecdh, RustCryptoEcdh};
use ripcord_proto::input;

use super::{Handle, HostEcdh, RipcordEcdhBackend, RipcordStatus, bytes, bytes_mut, guard, with_handle};

pub const RIPCORD_ROUTE_LOCAL: u32 = 1;
pub const RIPCORD_ROUTE_RENDEZVOUS: u32 = 3;

pub const RIPCORD_LOG_DEBUG: u32 = 0;
pub const RIPCORD_LOG_INFO: u32 = 1;
pub const RIPCORD_LOG_WARN: u32 = 2;
pub const RIPCORD_LOG_ERROR: u32 = 3;

/// Commands a host can ask for, pulled on every turn. Flags, so one pull can carry several.
pub const RIPCORD_CMD_CANCEL: u32 = 1 << 0;
pub const RIPCORD_CMD_KEYFRAME: u32 = 1 << 1;
pub const RIPCORD_CMD_DISCONNECT: u32 = 1 << 2;
/// With DISCONNECT: ask the console to rest first.
pub const RIPCORD_CMD_REST_CONSOLE: u32 = 1 << 3;

/// The furthest point a session reached, `halyard_client_stage`'s values.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RipcordClientStage {
    Idle = 0,
    ControlOpen,
    SignedIn,
    SessionReady,
    SenkushaUp,
    TakionUp,
    StreamKeys,
    StreamReady,
    Streaming,
    Ended,
}

/// Why a session ended, `halyard_client_end_reason`'s values.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RipcordClientEnd {
    None = 0,
    UserDisconnect,
    HostCancel,
    ConsoleClosed,
    ChannelError,
    SigninCancelled,
    SigninRejected,
    SigninNoSession,
    Timeout,
    Refused,
    NoMedia,
}

fn stage_of(s: Stage) -> RipcordClientStage {
    match s {
        Stage::Idle => RipcordClientStage::Idle,
        Stage::ControlOpen => RipcordClientStage::ControlOpen,
        Stage::SignedIn => RipcordClientStage::SignedIn,
        Stage::SessionReady => RipcordClientStage::SessionReady,
        Stage::SenkushaUp => RipcordClientStage::SenkushaUp,
        Stage::TakionUp => RipcordClientStage::TakionUp,
        Stage::StreamKeys => RipcordClientStage::StreamKeys,
        Stage::StreamReady => RipcordClientStage::StreamReady,
        Stage::Streaming => RipcordClientStage::Streaming,
        Stage::Ended => RipcordClientStage::Ended,
    }
}

fn end_of(r: Option<EndReason>) -> RipcordClientEnd {
    match r {
        None => RipcordClientEnd::None,
        Some(EndReason::UserDisconnect) => RipcordClientEnd::UserDisconnect,
        Some(EndReason::HostCancel) => RipcordClientEnd::HostCancel,
        Some(EndReason::ConsoleClosed) => RipcordClientEnd::ConsoleClosed,
        Some(EndReason::ChannelError) => RipcordClientEnd::ChannelError,
        Some(EndReason::SigninCancelled) => RipcordClientEnd::SigninCancelled,
        Some(EndReason::SigninRejected) => RipcordClientEnd::SigninRejected,
        Some(EndReason::SigninNoSession) => RipcordClientEnd::SigninNoSession,
        Some(EndReason::Timeout) => RipcordClientEnd::Timeout,
        Some(EndReason::Refused) => RipcordClientEnd::Refused,
        Some(EndReason::NoMedia) => RipcordClientEnd::NoMedia,
    }
}

/// An IPv4 endpoint: the address in network order, the port in host order.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct RipcordEndpoint {
    pub address: [u8; 4],
    pub port: u16,
}

impl From<RipcordEndpoint> for Endpoint {
    fn from(e: RipcordEndpoint) -> Self {
        Endpoint::new(e.address, e.port)
    }
}

impl From<Endpoint> for RipcordEndpoint {
    fn from(e: Endpoint) -> Self {
        RipcordEndpoint { address: e.address, port: e.port }
    }
}

/// The host's CSPRNG: fills `length` bytes, false on failure (the session then fails rather than use
/// weak keys).
#[repr(C)]
pub struct RipcordRandom {
    pub user: *mut c_void,
    pub fill: Option<extern "C" fn(user: *mut c_void, out: *mut u8, length: usize) -> bool>,
}

/// Everything a connect needs. A zero takes the evidenced default, as in `halyard_client_config`.
#[repr(C)]
pub struct RipcordClientConfig {
    /// `RIPCORD_ROUTE_*`; 0 is LOCAL.
    pub route: u32,
    pub console: [u8; 4],
    pub is_ps5: bool,
    pub registration_key: *const u8,
    pub registration_key_length: usize,
    pub companion: [u8; 16],
    /// The stored passcode, NUL-terminated, or null.
    pub login_pin: *const c_char,
    /// RP-Did.
    pub device_id: *const u8,
    pub device_id_length: usize,
    /// RP-OSType's Win<major>.<minor>; 0.0 sends 10.0.
    pub os_major: i32,
    pub os_minor: i32,

    pub width: i32,
    pub height: i32,
    pub fps: i32,
    pub bitrate_kbps: i32,
    pub allow_hevc: bool,
    pub hdr: bool,
    /// The MTU of the interface toward the console; 0 when unknown.
    pub interface_mtu: u32,

    pub signin_prompt_window_ms: u32,
    pub senkusha_attempts: u32,
    pub stream_attempts: u32,
    pub attempt_interval_ms: u32,
    /// 0 asks for 4 MB; negative keeps the platform's size.
    pub rcvbuf_bytes: i32,
    /// 0 waits for SESSION_ID before Takion on the LAN (the default); negative does not.
    pub require_session_ready: i32,

    /// Rendezvous only. The STUN servers, resolved by the host.
    pub stun_servers: *const RipcordEndpoint,
    pub stun_server_count: usize,
    pub stun_attempts: u32,
    pub stun_timeout_ms: u32,
    pub control_local_port: u16,
    pub media_local_port: u16,
    pub media_offer_timeout_ms: u32,
    pub dgram_stage_timeout_ms: u32,
    pub dgram_receive_timeout_ms: u32,

    /// The console's ports, 0 for the real ones (9295, 9296, 9297). Only a test changes them.
    pub control_port: u16,
    pub stream_port: u16,
    pub senkusha_port: u16,
    /// Skip the arm probe's broadcast, so a loopback test sends nothing off the machine.
    pub no_arm_broadcast: bool,
}

/// The controller, as `poll_input` reports it. `buttons` are `RIPCORD_PAD_*`-compatible bits
/// (`ripcord_proto::input`); sticks are s16 with left and up negative; triggers are levels.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct RipcordInputState {
    pub buttons: u32,
    pub left_x: i16,
    pub left_y: i16,
    pub right_x: i16,
    pub right_y: i16,
    pub left_trigger: u8,
    pub right_trigger: u8,
    /// Gyro x, y, z then accelerometer x, y, z, 0x7fff at rest; only read when `has_motion`.
    pub motion: [u16; 6],
    pub has_motion: bool,
}

/// What STREAM_INFO said. `video_header` is borrowed for the call.
#[repr(C)]
pub struct RipcordStreamInfo {
    pub width: u32,
    pub height: u32,
    pub is_hevc: bool,
    pub video_header: *const u8,
    pub video_header_length: usize,
}

/// One statistics window, every 200 ms while streaming.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct RipcordClientStats {
    pub packets_received: u64,
    pub packets_lost: u64,
    pub video_frames: u64,
    pub audio_frames: u64,
    pub keyframes: u64,
    pub kbps: u32,
    pub ms_since_console_activity: u32,
    pub ms_since_video_frame: u32,
    pub idr_requests: u32,
    pub window_ms: u32,
    pub verify_dropped: u64,
    pub rtt_ms: f64,
}

/// One of our rendezvous legs, for the OFFER.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct RipcordLeg {
    pub local_port: u16,
    pub has_reflexive: bool,
    pub reflexive: RipcordEndpoint,
    /// 1 consistent, 0 per destination (a console elsewhere will not reach this), -1 one answer only.
    pub endpoint_independent: i32,
}

impl From<Leg> for RipcordLeg {
    fn from(l: Leg) -> Self {
        RipcordLeg {
            local_port: l.local_port,
            has_reflexive: l.reflexive.is_some(),
            reflexive: l.reflexive.map(Into::into).unwrap_or_default(),
            endpoint_independent: l.endpoint_independent.map_or(-1, i32::from),
        }
    }
}

/// The console, for one leg, as its OFFER described it.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct RipcordPeer {
    pub endpoint: RipcordEndpoint,
    pub console_hashed_id: [u8; 20],
}

impl From<RipcordPeer> for Peer {
    fn from(p: RipcordPeer) -> Self {
        Peer { endpoint: p.endpoint.into(), console_hashed_id: p.console_hashed_id }
    }
}

/// Protocol facts about how a session went. Text is the host's job.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct RipcordClientResult {
    pub stage: RipcordClientStage,
    pub end_reason: RipcordClientEnd,
    pub login_prompted: bool,
    pub login_attempts: u32,
    /// -1 unknown, 0 refused, 1 accepted.
    pub login_verdict: i32,
    /// The raw verdict byte, -1 if none arrived.
    pub login_verdict_byte: i32,
    pub senkusha_ok: bool,
    /// -1 when not measured.
    pub version_rtt_ms: i32,
    pub stream_version: u32,
    pub stream_info_parsed: bool,
    pub stream_width: u32,
    pub stream_height: u32,
    pub stream_is_hevc: bool,
    pub rcvbuf_granted: usize,
    pub heartbeats_sent: u64,
    pub congestion_sent: u64,
    pub input_history_sent: u64,
    pub input_state_sent: u64,
    pub stream_info_repeats: u64,
    pub disconnect_sent: bool,
    pub rest_requested: bool,
    pub verify_dropped: u64,
    pub stray_dropped: u64,
    /// DISCONNECT's reason when the console hung up, NUL-terminated and truncated.
    pub console_disconnect_reason: [u8; 64],
    pub control_local_port: u16,
    pub media_local_port: u16,
    pub media_prelude_ok: bool,
    /// -1 never.
    pub session_ready_waited_ms: i32,
    pub probe_report_sent: bool,
    pub stream_ready_seen: bool,
}

/// The callbacks, all on the pump thread. Any may be null except `video_frame`.
#[repr(C)]
pub struct RipcordClientCallbacks {
    pub user: *mut c_void,
    /// `RIPCORD_LOG_*`, and a NUL-terminated line borrowed for the call.
    pub log: Option<extern "C" fn(user: *mut c_void, level: u32, line: *const c_char)>,
    /// Before each stage begins; ENDED once, when the session is over.
    pub stage: Option<extern "C" fn(user: *mut c_void, stage: RipcordClientStage)>,
    pub stream_info: Option<extern "C" fn(user: *mut c_void, info: *const RipcordStreamInfo)>,
    /// One Annex-B access unit; keyframes carry the parameter sets.
    pub video_frame: Option<extern "C" fn(user: *mut c_void, data: *const u8, length: usize, keyframe: bool)>,
    /// One Opus packet, 10 ms.
    pub audio_frame: Option<extern "C" fn(user: *mut c_void, data: *const u8, length: usize)>,
    /// The current pad; false when no controller is attached, and then nothing is sent.
    pub poll_input: Option<extern "C" fn(user: *mut c_void, out: *mut RipcordInputState) -> bool>,
    /// The console wants its user's passcode; `retry` counts refusals. Write NUL-terminated digits into
    /// `out` and return 1; 0 if not ready yet (asked again); -1 to give up.
    pub poll_passcode:
        Option<extern "C" fn(user: *mut c_void, retry: u32, out: *mut c_char, out_size: usize) -> i32>,
    /// `RIPCORD_CMD_*` flags, or 0.
    pub poll_commands: Option<extern "C" fn(user: *mut c_void) -> u32>,
    pub stats: Option<extern "C" fn(user: *mut c_void, stats: *const RipcordClientStats)>,
    /// Rendezvous only, and required there: negotiate media for `leg` and fill `out`. 1 ready, 0 not yet,
    /// -1 give up.
    pub poll_media:
        Option<extern "C" fn(user: *mut c_void, leg: *const RipcordLeg, out: *mut RipcordPeer) -> i32>,
}

/// A host key-agreement backend the session owns. The raw user pointer is why it is not `Send` by
/// itself; the handle is used from one thread at a time, which is the contract every export states.
struct OwnedEcdh(RipcordEcdhBackend);

// SAFETY: the backend's user pointer is only dereferenced by the host's own callbacks, on the one
// thread the host drives the handle from.
unsafe impl Send for OwnedEcdh {}

impl Ecdh for OwnedEcdh {
    fn public_key(&self, curve: Curve, private_key: &[u8]) -> Option<Vec<u8>> {
        HostEcdh(&self.0).public_key(curve, private_key)
    }

    fn shared_secret(&self, curve: Curve, private_key: &[u8], peer: &[u8]) -> Option<Vec<u8>> {
        HostEcdh(&self.0).shared_secret(curve, private_key, peer)
    }
}

/// The host's callbacks behind `ripcord_net::Host`.
struct FfiHost<'a>(&'a RipcordClientCallbacks);

impl FfiHost<'_> {
    fn log(&self, level: u32, text: &str) {
        if let Some(f) = self.0.log {
            let line = CString::new(text.replace('\0', " ")).unwrap_or_default();
            f(self.0.user, level, line.as_ptr());
        }
    }
}

impl Host for FfiHost<'_> {
    fn event(&mut self, event: Event) {
        let cb = self.0;
        match event {
            Event::Log(level, text) => self.log(
                match level {
                    LogLevel::Debug => RIPCORD_LOG_DEBUG,
                    LogLevel::Info => RIPCORD_LOG_INFO,
                    LogLevel::Warn => RIPCORD_LOG_WARN,
                    LogLevel::Error => RIPCORD_LOG_ERROR,
                },
                &text,
            ),
            Event::Stage(s) => {
                if let Some(f) = cb.stage {
                    f(cb.user, stage_of(s));
                }
            }
            Event::StreamInfo { width, height, hevc, video_header } => {
                if let Some(f) = cb.stream_info {
                    let info = RipcordStreamInfo {
                        width,
                        height,
                        is_hevc: hevc,
                        video_header: video_header.as_ptr(),
                        video_header_length: video_header.len(),
                    };
                    f(cb.user, &info);
                }
            }
            Event::Video { data, keyframe } => {
                if let Some(f) = cb.video_frame {
                    f(cb.user, data.as_ptr(), data.len(), keyframe);
                }
            }
            Event::Audio(data) => {
                if let Some(f) = cb.audio_frame {
                    f(cb.user, data.as_ptr(), data.len());
                }
            }
            Event::Stats(s) => {
                if let Some(f) = cb.stats {
                    let out = RipcordClientStats {
                        packets_received: s.packets_received,
                        packets_lost: s.packets_lost,
                        video_frames: s.video_frames,
                        audio_frames: s.audio_frames,
                        keyframes: s.keyframes,
                        kbps: s.kbps,
                        ms_since_console_activity: s.ms_since_console_activity,
                        ms_since_video_frame: s.ms_since_video_frame,
                        idr_requests: s.idr_requests,
                        window_ms: s.window_ms,
                        verify_dropped: s.verify_dropped,
                        rtt_ms: s.rtt_ms,
                    };
                    f(cb.user, &out);
                }
            }
            // Answered through the polls, or reported by the calls that wait for them.
            Event::PasscodeWanted { .. }
            | Event::MediaWanted(_)
            | Event::ControlLegReady(_)
            | Event::Ended(_) => {}
        }
    }

    fn controller(&mut self) -> Controller {
        let f = self.0.poll_input?;
        let mut s = RipcordInputState::default();
        if !f(self.0.user, &mut s) {
            return None;
        }
        Some(input::State {
            buttons: s.buttons,
            left_x: s.left_x,
            left_y: s.left_y,
            right_x: s.right_x,
            right_y: s.right_y,
            left_trigger: s.left_trigger,
            right_trigger: s.right_trigger,
            motion: s.has_motion.then_some(s.motion),
        })
    }

    fn passcode(&mut self, retry: u32) -> Answer<String> {
        let Some(f) = self.0.poll_passcode else {
            self.log(RIPCORD_LOG_WARN, "the console wants a passcode and this host cannot ask for one");
            return Answer::GiveUp;
        };
        let mut out = [0 as c_char; 64];
        match f(self.0.user, retry, out.as_mut_ptr(), out.len()) {
            0 => Answer::Pending,
            1 => {
                out[out.len() - 1] = 0;
                // SAFETY: NUL-terminated within the buffer, just ensured.
                let digits = unsafe { CStr::from_ptr(out.as_ptr()) }.to_string_lossy().into_owned();
                // An empty answer is a cancel, as in C.
                if digits.is_empty() { Answer::GiveUp } else { Answer::Ready(digits) }
            }
            _ => Answer::GiveUp,
        }
    }

    fn media(&mut self, leg: &Leg) -> Answer<Peer> {
        let Some(f) = self.0.poll_media else { return Answer::GiveUp };
        let leg = RipcordLeg::from(*leg);
        let mut out = RipcordPeer::default();
        match f(self.0.user, &leg, &mut out) {
            0 => Answer::Pending,
            1 => Answer::Ready(out.into()),
            _ => Answer::GiveUp,
        }
    }

    fn commands(&mut self) -> Commands {
        let flags = self.0.poll_commands.map_or(0, |f| f(self.0.user));
        Commands {
            cancel: flags & RIPCORD_CMD_CANCEL != 0,
            keyframe: flags & RIPCORD_CMD_KEYFRAME != 0,
            disconnect: flags & RIPCORD_CMD_DISCONNECT != 0,
            rest_console: flags & RIPCORD_CMD_REST_CONSOLE != 0,
        }
    }
}

/// One session. Opaque.
pub struct RipcordClient {
    inner: Client,
    callbacks: RipcordClientCallbacks,
    poisoned: bool,
}

impl RipcordClient {
    pub(crate) fn register(
        &mut self,
        is_ps5: bool,
        seed: &[u8; 16],
        account_id: &str,
        client_ip: &str,
    ) -> Result<ripcord_proto::sess::regist::PairingRecord, ripcord_net::RegisterError> {
        let callbacks = &self.callbacks;
        self.inner.rendezvous_register(&mut FfiHost(callbacks), is_ps5, seed, account_id, client_ip)
    }
}

impl Handle for RipcordClient {
    fn poisoned(&mut self) -> &mut bool {
        &mut self.poisoned
    }
}

/// # Safety
/// Every pointer in `c` is null or valid for its stated length.
unsafe fn config_of(c: &RipcordClientConfig) -> Option<Config> {
    let route = match c.route {
        0 | RIPCORD_ROUTE_LOCAL => Route::Local,
        RIPCORD_ROUTE_RENDEZVOUS => Route::Rendezvous,
        _ => return None,
    };
    // SAFETY: forwarded.
    let registration_key = unsafe { bytes(c.registration_key, c.registration_key_length) }?.to_vec();
    // SAFETY: forwarded.
    let device_id = unsafe { bytes(c.device_id, c.device_id_length) }?.to_vec();
    if registration_key.is_empty() || c.console == [0; 4] {
        return None;
    }
    let mut pairing = Pairing::new(c.is_ps5, registration_key, c.companion);
    if !c.login_pin.is_null() {
        // SAFETY: a non-null pin is NUL-terminated per the contract.
        let pin = unsafe { CStr::from_ptr(c.login_pin) }.to_string_lossy().into_owned();
        pairing.login_pin = (!pin.is_empty()).then_some(pin);
    }
    let mut cfg = Config::new(route, c.console, pairing, device_id);
    let us = |ms: u32, default: u64| if ms == 0 { default } else { u64::from(ms) * 1000 };
    let or = |v: u32, default: u32| if v == 0 { default } else { v };
    if (c.os_major, c.os_minor) != (0, 0) {
        cfg.os_version = (c.os_major, c.os_minor);
    }
    (cfg.width, cfg.height) =
        if c.width > 0 && c.height > 0 { (c.width, c.height) } else { (cfg.width, cfg.height) };
    if c.fps != 0 {
        cfg.fps = c.fps;
    }
    if c.bitrate_kbps > 0 {
        cfg.bitrate_kbps = c.bitrate_kbps;
    }
    cfg.allow_hevc = c.allow_hevc;
    cfg.hdr = c.hdr;
    cfg.interface_mtu = (c.interface_mtu != 0).then_some(c.interface_mtu);
    cfg.signin_prompt_window_us = us(c.signin_prompt_window_ms, cfg.signin_prompt_window_us);
    cfg.senkusha_attempts = or(c.senkusha_attempts, cfg.senkusha_attempts);
    cfg.stream_attempts = or(c.stream_attempts, cfg.stream_attempts);
    cfg.attempt_interval_us = us(c.attempt_interval_ms, cfg.attempt_interval_us);
    cfg.rcvbuf_bytes = match c.rcvbuf_bytes {
        0 => cfg.rcvbuf_bytes,
        n if n < 0 => 0,
        n => n as usize,
    };
    cfg.require_session_ready = c.require_session_ready >= 0;
    if c.stun_server_count > 0 {
        if c.stun_servers.is_null() || c.stun_server_count > 16 {
            return None;
        }
        // SAFETY: non-null and valid for `stun_server_count` endpoints per the contract.
        let servers = unsafe { std::slice::from_raw_parts(c.stun_servers, c.stun_server_count) };
        cfg.stun_servers = servers.iter().map(|&e| e.into()).collect();
    }
    cfg.stun_attempts = or(c.stun_attempts, cfg.stun_attempts);
    cfg.stun_timeout_us = us(c.stun_timeout_ms, cfg.stun_timeout_us);
    cfg.control_local_port = c.control_local_port;
    cfg.media_local_port = c.media_local_port;
    cfg.media_offer_timeout_us = us(c.media_offer_timeout_ms, cfg.media_offer_timeout_us);
    cfg.dgram_stage_timeout_us = us(c.dgram_stage_timeout_ms, cfg.dgram_stage_timeout_us);
    cfg.dgram_receive_timeout_us = us(c.dgram_receive_timeout_ms, cfg.dgram_receive_timeout_us);
    if c.control_port != 0 {
        cfg.control_port = c.control_port;
    }
    if c.stream_port != 0 {
        cfg.stream_port = c.stream_port;
    }
    if c.senkusha_port != 0 {
        cfg.senkusha_port = c.senkusha_port;
    }
    cfg.arm_broadcast = !c.no_arm_broadcast;
    Some(cfg)
}

/// A new session from a config (copied; the host may free it after this call) and callbacks (copied).
/// `ecdh` null uses the engine's RustCrypto backend; `random` is required. Opens nothing. Null for a bad
/// argument: no registration key, no console address, an unknown route, a null `video_frame` or `fill`,
/// or a rendezvous config without `poll_media`. Free with [`ripcord_client_free`].
///
/// # Safety
/// Every pointer valid per its field's documentation; the callbacks do not unwind.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_client_new(
    config: *const RipcordClientConfig,
    callbacks: *const RipcordClientCallbacks,
    ecdh: *const RipcordEcdhBackend,
    random: *const RipcordRandom,
) -> *mut RipcordClient {
    guard(std::ptr::null_mut(), || {
        // SAFETY: each pointer null or valid per the contract.
        let (Some(config), Some(callbacks), Some(random)) =
            (unsafe { config.as_ref() }, unsafe { callbacks.as_ref() }, unsafe { random.as_ref() })
        else {
            return std::ptr::null_mut();
        };
        let (Some(fill), Some(_)) = (random.fill, callbacks.video_frame) else { return std::ptr::null_mut() };
        // SAFETY: forwarded.
        let Some(cfg) = (unsafe { config_of(config) }) else { return std::ptr::null_mut() };
        if cfg.route == Route::Rendezvous && callbacks.poll_media.is_none() {
            return std::ptr::null_mut();
        }
        // SAFETY: null or valid, and it outlives the handle per the contract (the struct is copied).
        let backend: Box<dyn Ecdh + Send> = match unsafe { ecdh.as_ref() } {
            Some(b) => Box::new(OwnedEcdh(RipcordEcdhBackend {
                user: b.user,
                public_key: b.public_key,
                shared_secret: b.shared_secret,
            })),
            None => Box::new(RustCryptoEcdh),
        };
        let user = random.user as usize;
        let source = Box::new(move |out: &mut [u8]| {
            // A refused draw must not become predictable key material: poison the draw instead, and the
            // panic ends the session through the export's guard.
            assert!(fill(user as *mut c_void, out.as_mut_ptr(), out.len()), "the host's CSPRNG refused");
        });
        let callbacks = RipcordClientCallbacks { ..*callbacks };
        Box::into_raw(Box::new(RipcordClient {
            inner: Client::new(cfg, backend, source),
            callbacks,
            poisoned: false,
        }))
    })
}

/// # Safety
/// `client` null or from [`ripcord_client_new`], not used after this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_client_free(client: *mut RipcordClient) {
    guard((), || {
        if !client.is_null() {
            // SAFETY: from Box::into_raw in ripcord_client_new, freed once. Dropping closes every socket.
            drop(unsafe { Box::from_raw(client) });
        }
    })
}

/// Runs the sequence to streaming. Blocking, bounded by the session's budgets, cancellable through
/// `poll_commands`. Writes the stage reached; STREAM_READY or later means pump should follow.
///
/// # Safety
/// `client` from [`ripcord_client_new`]; `out_stage` null or valid for a write.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_client_connect(
    client: *mut RipcordClient,
    out_stage: *mut RipcordClientStage,
) -> RipcordStatus {
    with_handle(client, |c| {
        let callbacks = &c.callbacks;
        let stage = c.inner.connect(&mut FfiHost(callbacks));
        if !out_stage.is_null() {
            // SAFETY: non-null and valid per the contract.
            unsafe { out_stage.write(stage_of(stage)) };
        }
        RipcordStatus::Ok
    })
}

/// Services a running session for at most about `max_wait_ms`. Writes whether it is still alive.
///
/// # Safety
/// `client` from [`ripcord_client_new`]; `out_alive` null or valid for a write.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_client_pump(
    client: *mut RipcordClient,
    max_wait_ms: u32,
    out_alive: *mut bool,
) -> RipcordStatus {
    with_handle(client, |c| {
        let callbacks = &c.callbacks;
        let alive = c.inner.pump(&mut FfiHost(callbacks), Duration::from_millis(u64::from(max_wait_ms)));
        if !out_alive.is_null() {
            // SAFETY: as above.
            unsafe { out_alive.write(alive) };
        }
        RipcordStatus::Ok
    })
}

/// Goodbye: REST_MODE first when `rest_console` (only when a person chose it), then the Takion
/// DISCONNECT; the session ends here. Pump-thread only.
///
/// # Safety
/// `client` from [`ripcord_client_new`].
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_client_disconnect(
    client: *mut RipcordClient,
    rest_console: bool,
) -> RipcordStatus {
    with_handle(client, |c| {
        let callbacks = &c.callbacks;
        c.inner.disconnect(&mut FfiHost(callbacks), rest_console);
        RipcordStatus::Ok
    })
}

/// How the session went, at any point.
///
/// # Safety
/// `client` from [`ripcord_client_new`]; `out` valid for a write.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_client_result(
    client: *mut RipcordClient,
    out: *mut RipcordClientResult,
) -> RipcordStatus {
    if out.is_null() {
        return RipcordStatus::InvalidArgument;
    }
    with_handle(client, |c| {
        let o = c.inner.outcome();
        let mut reason = [0u8; 64];
        if let Some(r) = &o.console_disconnect_reason {
            let n = r.len().min(63);
            reason[..n].copy_from_slice(&r.as_bytes()[..n]);
        }
        let result = RipcordClientResult {
            stage: if c.inner.is_ended() && o.stage.is_none() {
                RipcordClientStage::Idle
            } else {
                stage_of(c.inner.stage())
            },
            end_reason: end_of(o.end_reason),
            login_prompted: o.login_prompted,
            login_attempts: o.login_attempts,
            login_verdict: o.login_verdict.map_or(-1, i32::from),
            login_verdict_byte: o.login_verdict_byte.map_or(-1, i32::from),
            senkusha_ok: o.senkusha_ok,
            version_rtt_ms: o.version_rtt_ms.map_or(-1, |v| v as i32),
            stream_version: o.stream_version,
            stream_info_parsed: o.stream_info_parsed,
            stream_width: o.stream_width,
            stream_height: o.stream_height,
            stream_is_hevc: o.stream_is_hevc,
            rcvbuf_granted: o.rcvbuf_granted,
            heartbeats_sent: o.heartbeats_sent,
            congestion_sent: o.congestion_sent,
            input_history_sent: o.input_history_sent,
            input_state_sent: o.input_state_sent,
            stream_info_repeats: o.stream_info_repeats,
            disconnect_sent: o.disconnect_sent,
            rest_requested: o.rest_requested,
            verify_dropped: o.verify_dropped,
            stray_dropped: o.stray_dropped,
            console_disconnect_reason: reason,
            control_local_port: o.control_local_port,
            media_local_port: o.media_local_port,
            media_prelude_ok: o.media_prelude_ok,
            session_ready_waited_ms: o.session_ready_waited_ms.map_or(-1, |v| v as i32),
            probe_report_sent: o.probe_report_sent,
            stream_ready_seen: o.stream_ready_seen,
        };
        // SAFETY: non-null, checked above, and valid per the contract.
        unsafe { out.write(result) };
        RipcordStatus::Ok
    })
}

// ---- the rendezvous route ----

/// Binds the control leg and asks STUN about it; `out` is what our OFFER must advertise. Blocking, at
/// most servers x attempts x timeout. `Rejected` on the LAN route, or once connect has started.
///
/// # Safety
/// `client` from [`ripcord_client_new`]; `out` valid for a write.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_client_rendezvous_prepare(
    client: *mut RipcordClient,
    out: *mut RipcordLeg,
) -> RipcordStatus {
    if out.is_null() {
        return RipcordStatus::InvalidArgument;
    }
    with_handle(client, |c| {
        let callbacks = &c.callbacks;
        match c.inner.rendezvous_prepare(&mut FfiHost(callbacks)) {
            Some(leg) => {
                // SAFETY: non-null, checked above.
                unsafe { out.write(leg.into()) };
                RipcordStatus::Ok
            }
            None => RipcordStatus::Rejected,
        }
    })
}

/// After our OFFER, before our ACCEPT: aims the control leg at the console's chosen candidate and puts
/// our Init on the wire without waiting.
///
/// # Safety
/// `client` from [`ripcord_client_new`]; `local_hashed_id` valid for 20 bytes; `console` valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_client_rendezvous_begin(
    client: *mut RipcordClient,
    local_hashed_id: *const u8,
    console: *const RipcordPeer,
) -> RipcordStatus {
    // SAFETY: forwarded from the contract.
    let (Some(id), Some(console)) = (unsafe { bytes(local_hashed_id, 20) }, unsafe { console.as_ref() })
    else {
        return RipcordStatus::InvalidArgument;
    };
    let id: [u8; 20] = id.try_into().expect("20 bytes");
    let console = *console;
    with_handle(client, |c| {
        if c.inner.rendezvous_begin(id, console.into()) { RipcordStatus::Ok } else { RipcordStatus::Rejected }
    })
}

/// One request and its answer on the control leg (registration on this route), between begin and connect.
/// Writes the answer's length; `Rejected` if it failed or does not fit `response_capacity`.
///
/// # Safety
/// `client` from [`ripcord_client_new`]; `request` valid for `request_length` bytes; `response` valid for
/// writes of `response_capacity` bytes; `out_length` valid for a write.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_client_rendezvous_exchange(
    client: *mut RipcordClient,
    request: *const u8,
    request_length: usize,
    response: *mut u8,
    response_capacity: usize,
    out_length: *mut usize,
) -> RipcordStatus {
    // SAFETY: forwarded from the contract.
    let (Some(request), Some(response)) =
        (unsafe { bytes(request, request_length) }, unsafe { bytes_mut(response, response_capacity) })
    else {
        return RipcordStatus::InvalidArgument;
    };
    if out_length.is_null() {
        return RipcordStatus::InvalidArgument;
    }
    let request = request.to_vec();
    with_handle(client, |c| {
        let callbacks = &c.callbacks;
        match c.inner.rendezvous_exchange(&mut FfiHost(callbacks), request) {
            Some(Ok(answer)) if answer.len() <= response.len() => {
                response[..answer.len()].copy_from_slice(&answer);
                // SAFETY: non-null, checked above.
                unsafe { out_length.write(answer.len()) };
                RipcordStatus::Ok
            }
            _ => RipcordStatus::Rejected,
        }
    })
}

// ---- test support ----

/// The scripted LAN console served on loopback sockets, for host test suites to run a whole session
/// against. Opaque. Test builds only (`test-support`).
#[cfg(feature = "test-support")]
pub struct RipcordLoopbackConsole {
    inner: ripcord_net::testing::LoopbackConsole,
}

/// Starts a console on 127.0.0.1 and writes its (control, senkusha, stream) ports, for
/// [`RipcordClientConfig`]'s port fields. `passcode` non-null makes it ask for that passcode.
///
/// # Safety
/// The out pointers valid for writes; `passcode` null or NUL-terminated.
#[cfg(feature = "test-support")]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_loopback_console_start(
    passcode: *const c_char,
    out_control_port: *mut u16,
    out_senkusha_port: *mut u16,
    out_stream_port: *mut u16,
) -> *mut RipcordLoopbackConsole {
    guard(std::ptr::null_mut(), || {
        if out_control_port.is_null() || out_senkusha_port.is_null() || out_stream_port.is_null() {
            return std::ptr::null_mut();
        }
        let pin = (!passcode.is_null())
            // SAFETY: NUL-terminated per the contract.
            .then(|| unsafe { CStr::from_ptr(passcode) }.to_string_lossy().into_owned());
        let inner = ripcord_net::testing::LoopbackConsole::start(|c| c.passcode = pin);
        let (control, senkusha, stream) = inner.ports;
        // SAFETY: non-null, checked above.
        unsafe {
            out_control_port.write(control);
            out_senkusha_port.write(senkusha);
            out_stream_port.write(stream);
        }
        Box::into_raw(Box::new(RipcordLoopbackConsole { inner }))
    })
}

/// The companion the loopback console's pairing uses: a client config needs it to derive the same keys.
///
/// # Safety
/// `out` valid for 16 bytes.
#[cfg(feature = "test-support")]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_loopback_console_companion(out: *mut u8) -> RipcordStatus {
    // SAFETY: forwarded.
    match unsafe { bytes_mut(out, 16) } {
        Some(o) => {
            o.copy_from_slice(&ripcord_net::testing::console::COMPANION);
            RipcordStatus::Ok
        }
        None => RipcordStatus::InvalidArgument,
    }
}

/// Whether the console has heard the client's Takion DISCONNECT.
///
/// # Safety
/// `console` from [`ripcord_loopback_console_start`].
#[cfg(feature = "test-support")]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_loopback_console_saw_goodbye(
    console: *const RipcordLoopbackConsole,
) -> bool {
    guard(false, || {
        // SAFETY: valid per the contract.
        unsafe { console.as_ref() }
            .is_some_and(|c| c.inner.state.lock().map(|s| s.disconnect_received).unwrap_or(false))
    })
}

/// Stops the console's thread and frees it.
///
/// # Safety
/// `console` null or from [`ripcord_loopback_console_start`], not used after this call.
#[cfg(feature = "test-support")]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_loopback_console_stop(console: *mut RipcordLoopbackConsole) {
    guard((), || {
        if !console.is_null() {
            // SAFETY: from Box::into_raw, freed once.
            drop(unsafe { Box::from_raw(console) });
        }
    })
}
