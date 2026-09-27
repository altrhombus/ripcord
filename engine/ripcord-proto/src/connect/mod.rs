//! The connect sequence and the running session: from a pairing record and an address to a stream, and
//! back. Ported from `libripcord/client/halyard_client.c` (itself from the PS3 port's `rc_connect.c` and
//! the .NET session), following `HalyardStreamingSession.cs` and `HalyardTakionStream.cs` where the two
//! differ; the choices are in `engine/README.md`.
//!
//! **Sans-IO**, like the rest of the crate: the machine asks for sockets and sends through [`Io`] and is
//! told what arrived; the host (`ripcord-net`, or a test) owns the sockets and the clock. Everything the
//! host wants to say (input, a passcode, a command) is a method call, and everything the session says
//! is an [`Event`].
//!
//! The stages, in order, each located by [`Stage`] when it fails:
//!
//! ```text
//! LAN         arm probe → /sess/init → /sess/ctrl (TCP 9295) → sign-in → senkusha (9297)
//!             → Takion (9296) → key agreement → STREAM_INFO → streaming
//! rendezvous  [host: prepare, signaling, begin, optional exchange] → /sess over the 9303 association
//!             → sign-in → the A/V leg (STUN, the host's media negotiation, prelude, SESSION_ID)
//!             → senkusha on that socket → PROBE_REPORT → STREAM_READY → Takion on that socket
//!             → key agreement → STREAM_INFO → streaming
//! ```

pub mod bandwidth;
mod session;

pub use session::Session;

use crate::input;

/// The console's end of a UDP or TCP flow, or ours.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub struct Endpoint {
    pub address: [u8; 4],
    pub port: u16,
}

impl Endpoint {
    pub const fn new(address: [u8; 4], port: u16) -> Self {
        Self { address, port }
    }
}

/// How the session reaches the console. The value is RP-ConPath.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Route {
    Local = 1,
    Rendezvous = 3,
}

/// The sockets the machine asks the host for. On the rendezvous route the stream runs on the media leg.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum Socket {
    /// The arm probe to 9295, and its reply. Broadcast is enabled on it.
    Arm,
    Senkusha,
    Stream,
    /// The rendezvous route's 9303 association.
    ControlLeg,
    /// The rendezvous route's A/V leg: STUN, the prelude, senkusha and the stream.
    MediaLeg,
}

/// What the machine needs the host to do.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Io {
    /// Open a UDP socket, bound to `local_port` (0: any), asking for `rcvbuf` bytes of receive buffer
    /// (0: the platform's). Answer with [`Session::on_udp_opened`] or [`Session::on_udp_failed`].
    UdpOpen {
        socket: Socket,
        local_port: u16,
        rcvbuf: usize,
    },
    UdpSend {
        socket: Socket,
        to: Endpoint,
        data: Vec<u8>,
    },
    UdpClose(Socket),
    /// Connect the control TCP connection (one at a time; a previous one has been closed). Answer with
    /// [`Session::on_tcp_connected`] or [`Session::on_tcp_closed`]; the machine keeps its own deadline.
    TcpConnect(Endpoint),
    TcpSend(Vec<u8>),
    TcpClose,
}

/// The furthest point a session reached: the C client's ladder, which is how a failure is located.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum Stage {
    Idle,
    /// /sess/init and /sess/ctrl answered.
    ControlOpen,
    /// The console's user is signed in, or was never asked.
    SignedIn,
    /// SESSION_ID seen.
    SessionReady,
    /// Senkusha completed its handshake.
    SenkushaUp,
    /// The stream's own Takion association is established.
    TakionUp,
    /// SESSION_REPLY verified, stream keys derived.
    StreamKeys,
    /// Sealing on, STREAM_INFO received and acknowledged.
    StreamReady,
    /// The first keyframe went to the host.
    Streaming,
    Ended,
}

/// Why a session ended, separately from where it got to.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum EndReason {
    UserDisconnect,
    HostCancel,
    /// The console sent DISCONNECT, or closed the control session.
    ConsoleClosed,
    /// A socket failed, or something could not be built.
    ChannelError,
    SigninCancelled,
    SigninRejected,
    /// Signed in, but SESSION_ID never came.
    SigninNoSession,
    /// A stage ran out of time; the stage says which.
    Timeout,
    /// The console refused the session: SESSION_REPLY, or /sess.
    Refused,
    /// Rendezvous: the host gave up on the media connection, or it never came.
    NoMedia,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum LogLevel {
    Debug,
    Info,
    Warn,
    Error,
}

/// One of our rendezvous legs as the cloud tier needs it for an OFFER.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Leg {
    pub local_port: u16,
    /// What STUN said the NAT maps the leg to, when it answered with IPv4.
    pub reflexive: Option<Endpoint>,
    /// Some(true) consistent mapping, Some(false) per destination, None only one answer.
    pub endpoint_independent: Option<bool>,
}

/// The console, for one leg, as its OFFER described it.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Peer {
    pub endpoint: Endpoint,
    pub console_hashed_id: [u8; 20],
}

/// One statistics window, every 200 ms while streaming: raw facts; the host's policy decides.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Stats {
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
    /// The stream channel's smoothed round trip from its SACKs (Karn's rule), 0 before a sample.
    pub rtt_ms: f64,
    /// Where the adaptive ladder stands: the bitrate it would ask for, and its rung's height. The console
    /// is told only when `Config::report_connection_quality` is on.
    pub target_bitrate_kbps: u32,
    pub target_height: u32,
}

/// What the session says.
#[derive(Clone, Debug, PartialEq)]
pub enum Event {
    Log(LogLevel, String),
    /// The stage being worked towards; [`Stage::Ended`] once, when the session is over.
    Stage(Stage),
    /// Rendezvous: the control leg is bound and STUN has answered (or not). Build the OFFER from it.
    ControlLegReady(Leg),
    /// The console wants its user's passcode. `retry` counts refusals so far. Answer with
    /// [`Session::submit_passcode`]; the session keeps running meanwhile.
    PasscodeWanted {
        retry: u32,
    },
    /// Rendezvous: the A/V leg is bound and STUN asked. Negotiate media with the cloud and answer with
    /// [`Session::media_peer`].
    MediaWanted(Leg),
    /// STREAM_INFO, for the decoder, before the first frame.
    StreamInfo {
        width: u32,
        height: u32,
        hevc: bool,
        video_header: Vec<u8>,
    },
    /// One Annex-B access unit; keyframes carry the parameter sets.
    Video {
        data: Vec<u8>,
        keyframe: bool,
    },
    /// One Opus packet, 10 ms.
    Audio(Vec<u8>),
    Stats(Stats),
    Ended(EndReason),
}

/// The pairing record's part of a connect.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Pairing {
    pub is_ps5: bool,
    pub registration_key: Vec<u8>,
    pub companion: [u8; 16],
    /// The stored passcode, used for the first attempt when present.
    pub login_pin: Option<String>,
}

impl Pairing {
    pub fn new(is_ps5: bool, registration_key: Vec<u8>, companion: [u8; 16]) -> Self {
        Self { is_ps5, registration_key, companion, login_pin: None }
    }
}

/// Everything a connect needs. [`Config::new`] fills the evidenced defaults.
#[derive(Clone, Debug, PartialEq)]
pub struct Config {
    pub route: Route,
    pub console: [u8; 4],
    pub pairing: Pairing,
    /// RP-Did: this client's device id.
    pub device_id: Vec<u8>,
    /// RP-OSType's Win<major>.<minor>. A host that is not Windows sends 10.0.
    pub os_version: (i32, i32),

    pub width: i32,
    pub height: i32,
    /// 30 or 60: anything above 30 is 60.
    pub fps: i32,
    /// The launch spec's bwKbpsSent, and RP-StartBitrate in /sess/ctrl as .NET sends it.
    pub bitrate_kbps: i32,
    /// RP-StreamingType (PS5 only). .NET sends 0.
    pub streaming_type: i32,
    /// The MTU of the interface toward the console, when the host knows it. Declared less 46 bytes of
    /// overhead, clamped to 530..=1454; unknown declares 1454 (.NET's LinkMetrics).
    pub interface_mtu: Option<u32>,
    pub allow_hevc: bool,
    /// Send CONNECTION_QUALITY with the adaptive ladder's target, RTT and loss. Off by default, as in .NET:
    /// the target bitrate's unit is [X], and a wrong guess by 1000x would have the console pick an absurd
    /// rate.
    pub report_connection_quality: bool,
    /// After sign-in, send the control channel's echo probe (0x0910), which the console answers with 0x8910
    /// on both routes: an answer proves our control-field crypto is being read. A diagnostic, off by
    /// default, as .NET's RIPCORD_PROBE_ECHO.
    pub control_echo_probe: bool,
    /// HDR is an HEVC profile, and "HDR" in the launch spec is an inference, never observed [X].
    pub hdr: bool,

    /// Also send the arm probe to 255.255.255.255, which a console that has not been spoken to recently
    /// answers (both references do). A test on loopback turns it off so nothing leaves the machine.
    pub arm_broadcast: bool,
    /// The console's ports. Only a test changes them.
    pub control_port: u16,
    pub stream_port: u16,
    pub senkusha_port: u16,

    pub signin_prompt_window_us: u64,
    pub senkusha_attempts: u32,
    pub stream_attempts: u32,
    pub attempt_interval_us: u64,
    pub rcvbuf_bytes: usize,
    /// Wait for SESSION_ID before Takion on the LAN (the C ports' hardware finding; .NET's claim that a
    /// LAN console streams without it is [X]).
    pub require_session_ready: bool,

    /// Rendezvous only.
    pub stun_servers: Vec<Endpoint>,
    pub stun_attempts: u32,
    pub stun_timeout_us: u64,
    pub control_local_port: u16,
    pub media_local_port: u16,
    /// The address both rendezvous legs bind; `None` binds every interface. A test binds 127.0.0.1. The
    /// driver applies it; the session never names an address of its own.
    pub bind_address: Option<[u8; 4]>,
    pub media_offer_timeout_us: u64,
    pub dgram_stage_timeout_us: u64,
    pub dgram_receive_timeout_us: u64,
}

impl Config {
    /// The defaults, each from the side of the .NET/C comparison it follows (`engine/README.md`).
    pub fn new(route: Route, console: [u8; 4], pairing: Pairing, device_id: Vec<u8>) -> Self {
        Self {
            route,
            console,
            pairing,
            device_id,
            os_version: (10, 0),
            width: 1280,
            height: 720,
            fps: 60,
            bitrate_kbps: 10_000,
            streaming_type: 0,
            interface_mtu: None,
            allow_hevc: false,
            report_connection_quality: false,
            control_echo_probe: false,
            hdr: false,
            arm_broadcast: true,
            control_port: 9295,
            stream_port: 9296,
            senkusha_port: 9297,
            signin_prompt_window_us: match route {
                Route::Local => 20_000_000,
                Route::Rendezvous => 1_000_000,
            },
            senkusha_attempts: 20,
            stream_attempts: 100,
            attempt_interval_us: 300_000,
            rcvbuf_bytes: 4 * 1024 * 1024,
            require_session_ready: true,
            stun_servers: Vec::new(),
            stun_attempts: crate::net::stun::DEFAULT_ATTEMPTS,
            stun_timeout_us: crate::net::stun::DEFAULT_TIMEOUT_US,
            control_local_port: 0,
            media_local_port: 0,
            bind_address: None,
            media_offer_timeout_us: 30_000_000,
            dgram_stage_timeout_us: 30_000_000,
            dgram_receive_timeout_us: 5_000_000,
        }
    }

    /// The C client's normalisation: fps to 30 or 60, a half resolution to the default, a non-positive
    /// bitrate to the default.
    fn normalised(mut self) -> Self {
        if self.width <= 0 || self.height <= 0 {
            (self.width, self.height) = (1280, 720);
        }
        self.fps = if self.fps > 30 { 60 } else { 30 };
        if self.bitrate_kbps <= 0 {
            self.bitrate_kbps = 10_000;
        }
        self.hdr &= self.allow_hevc;
        self
    }
}

/// Protocol facts about how a session went, for diagnosis. Text is the host's job.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Outcome {
    pub stage: Option<Stage>,
    pub end_reason: Option<EndReason>,
    pub login_prompted: bool,
    pub login_attempts: u32,
    /// None unknown, Some(true) accepted, Some(false) refused.
    pub login_verdict: Option<bool>,
    pub login_verdict_byte: Option<u8>,
    pub senkusha_ok: bool,
    pub version_rtt_ms: Option<u32>,
    /// The echo probe's least round trip, when a majority of its pings came back.
    pub echo_rtt_us: Option<u64>,
    /// Whether the MTU probe confirmed the declared MTU in both directions.
    pub mtu_confirmed: bool,
    pub corrupt_frames_sent: u64,
    pub control_echo_sent: bool,
    /// The console answered the control echo probe.
    pub control_echo_answered: bool,
    pub quality_reports_sent: u64,
    pub stream_version: u32,
    /// The curve the stream's key agreement ran on.
    pub curve: Option<crate::crypto::ecdh::Curve>,
    pub reply_reject: Option<crate::takion::negotiator::Reject>,
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
    pub console_disconnect_reason: Option<String>,
    pub disconnect_sent: bool,
    pub rest_requested: bool,
    pub verify_dropped: u64,
    /// Rendezvous only.
    pub control_local_port: u16,
    pub media_local_port: u16,
    pub media_prelude_ok: bool,
    pub session_ready_waited_ms: Option<u32>,
    pub probe_report_sent: bool,
    pub stream_ready_seen: bool,
    pub stray_dropped: u64,
}

/// A controller as the host reports it; `None` sends nothing, since neutral is a position and absence is
/// a different statement.
pub type Controller = Option<input::State>;

#[cfg(test)]
mod tests;
