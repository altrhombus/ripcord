//! The machine behind [`Session`]. Each step of the sequence is a [`Step`]; every input ends in
//! [`Session::advance`], which services the control session, applies deadlines and moves the step on.

use std::collections::{HashMap, VecDeque};
use std::sync::{Arc, Mutex};

use super::bandwidth::Rung;
use super::*;
use crate::RandomSource;
use crate::crypto::ecdh::Ecdh;
use crate::dgram::assoc::Association;
use crate::dgram::channel::{self, Channel, StageResult};
use crate::dgram::rendezvous::{self, ControlPlane, CtrlValues, Exchange};
use crate::discovery;
use crate::halyard::control::ControlField;
use crate::net::stun::{self, Gatherer, Outcome as StunOutcome};
use crate::sess::http::Response;
use crate::sess::requests::{self, Addressing, ConnectionPath, CtrlFields};
use crate::sess::session::{self as ctrl_session, ControlSession};
use crate::sess::{ctrl, launch_spec};
use crate::stream::demux::{DemuxSink, StreamDemux};
use crate::stream::packet_crypto::PacketCrypto;
use crate::takion::chunks::{
    CHANNEL_BANDWIDTH, CHANNEL_PROTOCOL_VERSION, CHANNEL_SESSION, CHANNEL_STREAM_INFO,
};
use crate::takion::connection::{self, Connection, Message};
use crate::takion::control::{self as tc, SessionRequest};
use crate::takion::negotiator::{self, Negotiator, SessionKeys};
use crate::takion::sealer;

/// Senkusha's whole budget, handshake, legs and probes together: .NET's 8 s box.
const SENKUSHA_BUDGET_US: u64 = 8_000_000;
/// The echo probe (HalyardSenkusha.RunEchoProbeAsync): 3 s in all, 80 ms per ping, and a majority of the
/// ten answered before its figure replaces the handshake round trips.
const ECHO_BUDGET_US: u64 = 3_000_000;
const ECHO_REPLY_US: u64 = 80_000;
/// The MTU probe (RunMtuProbeAsync): 3 s for both directions, 600 ms per console reply, and the close of
/// the upstream test on its own budget, since it must go out however the test ended.
const MTU_BUDGET_US: u64 = 3_000_000;
const MTU_REPLY_US: u64 = 600_000;
/// The MTU probe's upstream packet is filled with this, not zeros, so a compressing link cannot pass a size
/// the path cannot carry.
const MTU_PADDING: u8 = 0x47;
/// The console's downstream MTU datagrams: base type 2 on the senkusha socket. Only their arrival counts.
const BASE_TYPE_SENKUSHA_MTU: u8 = 0x02;
/// One control reply on senkusha, and the stream's PROTOCOL_VERSION_ACK (non-fatal, as in C).
const TAKION_REPLY_US: u64 = 5_000_000;
/// The whole stream bring-up, handshake to STREAM_INFO's ack: .NET's box.
const STREAM_BOX_US: u64 = 35_000_000;
/// The control plane (arm, both TCP connects, /sess): .NET's deadline, per route.
const CONTROL_DEADLINE_LAN_US: u64 = 20_000_000;
const CONTROL_DEADLINE_RENDEZVOUS_US: u64 = 60_000_000;
/// The sign-in gate: five attempts, eight seconds each (.NET's MaxSignInAttempts/SignInAttemptTimeout).
const SIGNIN_ATTEMPTS: u32 = 5;
const SIGNIN_WAIT_US: u64 = 8_000_000;
/// After an accepted passcode on the LAN, how long SESSION_ID may take. An awake console sends it promptly;
/// one woken from rest is `[X]` here, since no LAN run from rest has timed it. The bound comes from the
/// rendezvous route, where a woken console sent it 8.5 s after accepting (journal, 2026-09-25).
/// The wait ends the moment SESSION_ID arrives.
const SESSION_AFTER_LOGIN_US: u64 = 30_000_000;
/// The arm probe's reply window, then the settle before the console is spoken to.
const ARM_REPLY_WINDOW_US: u64 = 2_000_000;
const ARM_SETTLE_US: u64 = 200_000;
/// The rendezvous route's two non-fatal waits: SESSION_ID after the A/V prelude, STREAM_READY after
/// PROBE_REPORT.
const SESSION_READY_WINDOW_US: u64 = 8_000_000;
const STREAM_READY_WINDOW_US: u64 = 10_000_000;
/// The stream channel's cadences; the congestion window is also the stats window.
const HEARTBEAT_US: u64 = 1_000_000;
const CONGESTION_US: u64 = 200_000;
const INPUT_POLL_US: u64 = 4_000;
/// One IDR repairs the whole chain; asking while the answer is in flight wastes upstream.
const IDR_MIN_US: u64 = 200_000;
/// The declared MTU when the interface's is unknown, and the most ever declared; the least is 530. An
/// interface MTU is declared less the 46 bytes of IP, UDP and Takion overhead (.NET's LinkMetrics).
const DECLARED_MTU: u32 = 1454;
const DECLARED_MTU_MIN: u32 = 530;
const MTU_OVERHEAD: u32 = 46;
/// The rendezvous route's control port.
const RENDEZVOUS_CONTROL_PORT: u16 = crate::net::candidates::CONTROL_PORT;

type SharedRandom = Arc<Mutex<RandomSource>>;

fn share(random: &SharedRandom) -> RandomSource {
    let r = random.clone();
    Box::new(move |b: &mut [u8]| (r.lock().unwrap_or_else(|p| p.into_inner()))(b))
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum SigninResume {
    /// The ordinary gate after /sess/ctrl.
    Gate,
    /// A prompt that arrived during the rendezvous media wait ([X], C's addition).
    LateMedia,
}

enum Step {
    Idle,
    Arm { deadline: u64, settle: Option<u64> },
    TcpInit { connected: bool, deadline: u64 },
    TcpCtrl { connected: bool, deadline: u64, field: Option<ControlField> },
    DgramControl,
    SigninPrompt { deadline: u64 },
    SigninAsk { retry: u32, attempt: u32, refusals: u32, resume: SigninResume },
    SigninVerdict { attempt: u32, refusals: u32, pin: String, deadline: u64, resume: SigninResume },
    SigninAccepted { deadline: u64, resume: SigninResume },
    MediaOpen,
    MediaStun,
    MediaAwaitPeer { deadline: u64, asked: u64 },
    MediaPrelude,
    MediaSessionReady { started: u64, deadline: u64 },
    SenkushaOpen { deadline: u64 },
    SenkushaHandshake { deadline: u64 },
    SenkushaVersion { deadline: u64, asked: u64, box_end: u64 },
    SenkushaSession { deadline: u64 },
    SenkushaEcho { seq: u8, sent_at: u64, deadline: u64, budget_end: u64 },
    SenkushaMtuDown { mtu: u32, deadline: u64, budget_end: u64 },
    SenkushaMtuUpReply { mtu: u32, deadline: u64, budget_end: u64 },
    SenkushaMtuUpEcho { mtu: u32, deadline: u64 },
    StreamReadyWait { deadline: u64 },
    TakionOpen,
    TakionHandshake,
    KeysVersion { deadline: u64 },
    KeysReply { deadline: u64 },
    StreamInfoWait { deadline: u64 },
    Streaming,
    Ended,
}

/// The control session, whichever route carries it.
enum Control {
    None,
    Tcp { buf: Vec<u8>, session: Option<ControlSession> },
    Dgram(Box<ControlPlane>),
}

impl Control {
    fn session(&mut self) -> Option<&mut ControlSession> {
        match self {
            Control::Tcp { session, .. } => session.as_mut(),
            Control::Dgram(plane) => plane.session(),
            Control::None => None,
        }
    }
}

#[derive(Default)]
struct Opened {
    local_port: u16,
    rcvbuf: usize,
}

struct Streaming {
    writer: input::Writer,
    next_input: u64,
    next_congestion: u64,
    next_heartbeat: u64,
    window_start: u64,
    window_bytes: u64,
    last_activity: u64,
    last_video: u64,
    idr_awaiting: bool,
    idr_last: Option<u64>,
    idr_requests: u32,
    packets_received: u64,
    packets_lost: u64,
    video_frames: u64,
    audio_frames: u64,
    keyframes: u64,
}

/// The demuxer's sink for one ingest: frames become events, counts are kept.
struct Sink<'a> {
    events: &'a mut VecDeque<Event>,
    video: u64,
    audio: u64,
    keyframes: u64,
    losses: Vec<(u16, u16)>,
}

impl DemuxSink for Sink<'_> {
    fn video_frame(&mut self, data: &[u8], keyframe: bool) {
        self.video += 1;
        self.keyframes += u64::from(keyframe);
        self.events.push_back(Event::Video { data: data.to_vec(), keyframe });
    }

    fn audio_frame(&mut self, data: &[u8]) {
        self.audio += 1;
        self.events.push_back(Event::Audio(data.to_vec()));
    }

    fn video_loss(&mut self, first: u16, last: u16) {
        self.losses.push((first, last));
    }
}

/// One session, LAN or rendezvous.
pub struct Session {
    cfg: Config,
    ecdh: Box<dyn Ecdh + Send>,
    random: SharedRandom,
    io: VecDeque<Io>,
    events: VecDeque<Event>,
    outcome: Outcome,
    reached: Stage,
    end: Option<EndReason>,
    step: Step,
    started: bool,

    opened: HashMap<Socket, Opened>,
    open_failed: Vec<Socket>,
    control: Control,
    session_ready: bool,
    login_prompt: bool,
    verdict: Option<u8>,
    stream_ready: bool,
    passcode: Option<Option<String>>,
    rest_wanted: bool,

    // The rendezvous legs.
    control_peer: Option<Endpoint>,
    local_hashed_id: [u8; 20],
    stun: Option<(Socket, Gatherer<Endpoint>)>,
    control_leg: Leg,
    media_leg: Leg,
    channel: Option<Channel>,
    exchange: Option<Exchange>,
    media_peer: Option<Endpoint>,
    media_answer: Option<Option<Peer>>,
    media_channel: Option<Channel>,
    prepared: bool,
    arm_probe_sent: bool,
    /// A TCP connection is up and ours; what arrives for one we closed is stale.
    tcp_live: bool,
    control_deadline: u64,
    stream_box: u64,
    senkusha_session_asked: u64,
    /// The least round trip senkusha measured, in microseconds.
    measured_rtt_us: Option<u64>,
    senkusha_box: u64,
    /// Echoed ping sequences and downstream MTU datagrams seen on the senkusha socket.
    probe_echoes: VecDeque<u8>,
    mtu_probes_received: u64,
    echo_samples: Vec<u64>,
    confirmed_mtu: Option<u32>,

    // Takion.
    senkusha: Option<Connection>,
    senkusha_messages: VecDeque<Message>,
    stream: Option<Connection>,
    stream_messages: VecDeque<Message>,
    negotiator: Option<Negotiator>,
    keys: Option<SessionKeys>,
    demux: Option<StreamDemux<PacketCrypto>>,
    live: Option<Streaming>,
    controller: Controller,
    bandwidth: Option<bandwidth::Controller>,
    reporter: bandwidth::Reporter,
    power: bandwidth::PowerState,
}

fn log(events: &mut VecDeque<Event>, level: LogLevel, text: impl Into<String>) {
    events.push_back(Event::Log(level, text.into()));
}

impl Session {
    /// `random` must be the host's CSPRNG; `ecdh` the platform's key agreement.
    pub fn new(config: Config, ecdh: Box<dyn Ecdh + Send>, random: RandomSource) -> Self {
        Self {
            cfg: config.normalised(),
            ecdh,
            random: Arc::new(Mutex::new(random)),
            io: VecDeque::new(),
            events: VecDeque::new(),
            outcome: Outcome::default(),
            reached: Stage::Idle,
            end: None,
            step: Step::Idle,
            started: false,
            opened: HashMap::new(),
            open_failed: Vec::new(),
            control: Control::None,
            session_ready: false,
            login_prompt: false,
            verdict: None,
            stream_ready: false,
            passcode: None,
            rest_wanted: false,
            control_peer: None,
            local_hashed_id: [0; 20],
            stun: None,
            control_leg: Leg::default(),
            media_leg: Leg::default(),
            channel: None,
            exchange: None,
            media_peer: None,
            media_answer: None,
            media_channel: None,
            prepared: false,
            arm_probe_sent: false,
            tcp_live: false,
            control_deadline: 0,
            stream_box: 0,
            senkusha_session_asked: 0,
            measured_rtt_us: None,
            senkusha_box: 0,
            probe_echoes: VecDeque::new(),
            mtu_probes_received: 0,
            echo_samples: Vec::new(),
            confirmed_mtu: None,
            senkusha: None,
            senkusha_messages: VecDeque::new(),
            stream: None,
            stream_messages: VecDeque::new(),
            negotiator: None,
            keys: None,
            demux: None,
            live: None,
            controller: None,
            bandwidth: None,
            reporter: bandwidth::Reporter::default(),
            power: bandwidth::PowerState::default(),
        }
    }

    /// Bytes from the host's CSPRNG, for what a host builds around the session (a registration request).
    pub fn fill_random(&self, out: &mut [u8]) {
        self.fill(out)
    }

    fn fill(&self, out: &mut [u8]) {
        (self.random.lock().unwrap_or_else(|p| p.into_inner()))(out)
    }

    fn random_u32(&self) -> u32 {
        let mut b = [0u8; 4];
        self.fill(&mut b);
        u32::from_be_bytes(b)
    }

    fn log(&mut self, level: LogLevel, text: impl Into<String>) {
        log(&mut self.events, level, text);
    }

    // ---- What the host reads --------------------------------------------------------------------------

    pub fn poll_io(&mut self) -> Option<Io> {
        self.io.pop_front()
    }

    pub fn poll_event(&mut self) -> Option<Event> {
        self.events.pop_front()
    }

    pub fn outcome(&self) -> &Outcome {
        &self.outcome
    }

    /// The furthest stage reached.
    pub fn stage(&self) -> Stage {
        self.reached
    }

    pub fn is_streaming(&self) -> bool {
        matches!(self.step, Step::Streaming)
    }

    pub fn is_ended(&self) -> bool {
        matches!(self.step, Step::Ended)
    }

    /// The earliest time the machine wants [`Session::handle_timeout`], if any.
    pub fn next_timeout(&self) -> Option<u64> {
        let mut t: Option<u64> = None;
        let mut at = |v: Option<u64>| {
            if let Some(v) = v {
                t = Some(t.map_or(v, |t| t.min(v)));
            }
        };
        at(self.step_deadline());
        at(self.senkusha.as_ref().and_then(Connection::next_timeout));
        at(self.stream.as_ref().and_then(Connection::next_timeout));
        at(self.channel.as_ref().and_then(Channel::next_timeout));
        at(self.media_channel.as_ref().and_then(Channel::next_timeout));
        at(self.exchange.as_ref().and_then(Exchange::next_timeout));
        if let Control::Dgram(p) = &self.control {
            at(p.next_timeout());
        }
        at(self.stun.as_ref().and_then(|(_, g)| g.next_timeout()));
        if let Some(l) = &self.live {
            at(Some(l.next_heartbeat));
            at(Some(l.next_congestion));
            if self.controller.is_some() {
                at(Some(l.next_input));
            }
            if l.idr_awaiting {
                at(Some(l.idr_last.map_or(0, |t| t + IDR_MIN_US)));
            }
        }
        t
    }

    fn step_deadline(&self) -> Option<u64> {
        match &self.step {
            Step::Arm { deadline, settle } => Some(settle.unwrap_or(*deadline)),
            Step::TcpInit { deadline, .. }
            | Step::TcpCtrl { deadline, .. }
            | Step::SigninPrompt { deadline }
            | Step::SigninVerdict { deadline, .. }
            | Step::SigninAccepted { deadline, .. }
            | Step::MediaAwaitPeer { deadline, .. }
            | Step::MediaSessionReady { deadline, .. }
            | Step::SenkushaOpen { deadline }
            | Step::SenkushaHandshake { deadline }
            | Step::SenkushaVersion { deadline, .. }
            | Step::SenkushaSession { deadline }
            | Step::SenkushaMtuUpEcho { deadline, .. }
            | Step::StreamReadyWait { deadline }
            | Step::KeysVersion { deadline }
            | Step::KeysReply { deadline }
            | Step::StreamInfoWait { deadline } => Some(*deadline),
            Step::DgramControl => Some(self.control_deadline),
            Step::SenkushaEcho { deadline, budget_end, .. }
            | Step::SenkushaMtuDown { deadline, budget_end, .. }
            | Step::SenkushaMtuUpReply { deadline, budget_end, .. } => Some((*deadline).min(*budget_end)),
            Step::TakionOpen | Step::TakionHandshake => Some(self.stream_box),
            _ => None,
        }
    }

    // ---- The rendezvous route's pre-connect calls ----------------------------------------------------

    /// Binds the control leg and asks STUN about it; [`Event::ControlLegReady`] follows.
    pub fn rendezvous_prepare(&mut self, now_us: u64) -> bool {
        if self.cfg.route != Route::Rendezvous || self.prepared || self.started {
            return false;
        }
        self.prepared = true;
        self.io.push_back(Io::UdpOpen {
            socket: Socket::ControlLeg,
            local_port: self.cfg.control_local_port,
            rcvbuf: 0,
        });
        self.advance(now_us);
        true
    }

    /// After our OFFER, before our ACCEPT: aims the control leg at the console's chosen candidate and puts
    /// our Init on the wire without waiting.
    pub fn rendezvous_begin(&mut self, now_us: u64, local_hashed_id: [u8; 20], console: Peer) -> bool {
        if self.cfg.route != Route::Rendezvous
            || self.channel.is_some()
            || self.started
            || !self.opened.contains_key(&Socket::ControlLeg)
        {
            return false;
        }
        self.local_hashed_id = local_hashed_id;
        self.control_peer = Some(console.endpoint);
        let assoc = Association::new(
            share(&self.random),
            local_hashed_id,
            console.console_hashed_id,
            console.endpoint.address,
            console.endpoint.port,
        );
        let mut channel = Channel::new(assoc, self.dgram_options());
        channel.begin();
        self.channel = Some(channel);
        self.flush();
        let _ = now_us;
        true
    }

    /// One request and its answer on the control leg (registration on this route): between begin and
    /// connect. The answer arrives as [`Session::poll_exchange`].
    pub fn rendezvous_exchange(&mut self, now_us: u64, request: Vec<u8>) -> bool {
        let Some(channel) = self.channel.take() else { return false };
        if self.started {
            self.channel = Some(channel);
            return false;
        }
        self.exchange = Some(Exchange::new(channel, request, now_us));
        self.flush();
        true
    }

    /// The exchange's answer once it has one.
    pub fn poll_exchange(&mut self) -> Option<Result<Vec<u8>, rendezvous::Error>> {
        let result = self.exchange.as_mut()?.poll_result()?;
        let exchange = self.exchange.take()?;
        self.channel = Some(exchange.into_channel());
        Some(result)
    }

    fn dgram_options(&self) -> channel::Options {
        channel::Options {
            receive_timeout_us: self.cfg.dgram_receive_timeout_us,
            stage_timeout_us: self.cfg.dgram_stage_timeout_us,
            hello_addressing: crate::dgram::assoc::Addressing::PortPair,
        }
    }

    // ---- What the host tells the session -------------------------------------------------------------

    /// Starts the sequence. On the rendezvous route, after prepare and begin.
    pub fn connect(&mut self, now_us: u64) {
        if self.started {
            return;
        }
        self.started = true;
        if self.cfg.pairing.registration_key.is_empty() {
            // A client made only to register (the account route's pairing) has no key to connect with.
            self.log(LogLevel::Error, "there is no registration key to connect with: pair first");
            self.request_end(EndReason::ChannelError);
        } else if !crate::halyard::family_bundled(self.cfg.pairing.is_ps5) {
            // Stopping here, before the console, rather than at /sess/init, where it would read as a refusal.
            self.log(LogLevel::Error, "this engine was built without the interop constants for this console");
            self.request_end(EndReason::ChannelError);
        }
        if self.end.is_none() {
            self.start_control(now_us);
        }
        self.advance(now_us);
    }

    /// The host's power and thermal state, which caps the adaptive ladder (a low battery at 720p, throttling
    /// one rung lower). Takes effect at the next statistics window.
    pub fn set_power_state(&mut self, power: bandwidth::PowerState) {
        self.power = power;
        if let Some(b) = self.bandwidth.as_mut() {
            b.set_power(power);
        }
    }

    /// The current controller, polled by the session on its own input cadence.
    pub fn set_controller(&mut self, controller: Controller) {
        self.controller = controller;
    }

    /// The answer to [`Event::PasscodeWanted`]: the digits, or `None` to give up.
    pub fn submit_passcode(&mut self, now_us: u64, digits: Option<String>) {
        self.passcode = Some(digits);
        self.advance(now_us);
    }

    /// The answer to [`Event::MediaWanted`]: the console's media candidate, or `None` to give up.
    pub fn media_peer(&mut self, now_us: u64, peer: Option<Peer>) {
        self.media_answer = Some(peer);
        self.advance(now_us);
    }

    /// The host's decoder lost the chain.
    pub fn request_keyframe(&mut self, now_us: u64) {
        if let Some(l) = self.live.as_mut() {
            l.idr_awaiting = true;
        }
        self.advance(now_us);
    }

    /// Goodbye: REST_MODE first when `rest_console` (only when a person chose it), then the Takion
    /// DISCONNECT, both best-effort, then the session ends.
    pub fn disconnect(&mut self, now_us: u64, rest_console: bool) {
        self.rest_wanted |= rest_console;
        self.request_end(EndReason::UserDisconnect);
        self.advance(now_us);
    }

    /// Abandons a connect, or ends a running session without the goodbye's rest.
    pub fn cancel(&mut self, now_us: u64) {
        self.request_end(EndReason::HostCancel);
        self.advance(now_us);
    }

    pub fn on_udp_opened(&mut self, now_us: u64, socket: Socket, local_port: u16, rcvbuf_granted: usize) {
        self.opened.insert(socket, Opened { local_port, rcvbuf: rcvbuf_granted });
        match socket {
            Socket::ControlLeg | Socket::MediaLeg => self.start_stun(now_us, socket, local_port),
            _ => {}
        }
        self.advance(now_us);
    }

    pub fn on_udp_failed(&mut self, now_us: u64, socket: Socket) {
        self.open_failed.push(socket);
        self.advance(now_us);
    }

    pub fn on_tcp_connected(&mut self, now_us: u64) {
        self.tcp_live = true;
        match &mut self.step {
            Step::TcpInit { connected, .. } | Step::TcpCtrl { connected, .. } if !*connected => {
                *connected = true;
                self.send_sess_request(now_us);
            }
            _ => {}
        }
        self.advance(now_us);
    }

    pub fn on_tcp_data(&mut self, now_us: u64, data: &[u8]) {
        if !self.tcp_live {
            return;
        }
        if let Control::Tcp { buf, session } = &mut self.control {
            match session {
                Some(s) => s.on_bytes(data),
                None => buf.extend_from_slice(data),
            }
        }
        self.advance(now_us);
    }

    /// The connection closed or failed; `error` when it did not close cleanly.
    pub fn on_tcp_closed(&mut self, now_us: u64, error: bool) {
        if !std::mem::replace(&mut self.tcp_live, false) {
            return;
        }
        match &self.step {
            Step::TcpInit { .. } | Step::TcpCtrl { .. } => {
                // A close before the response parsed is a failure; a close with it buffered is how
                // /sess/init ends anyway, and the step reads the buffer first.
                if !self.response_ready() {
                    self.log(LogLevel::Warn, "the control connection closed before /sess answered");
                    self.request_end(EndReason::ChannelError);
                }
            }
            _ if matches!(self.control, Control::Tcp { session: Some(_), .. }) => {
                self.log(
                    LogLevel::Warn,
                    if error {
                        "the control session failed"
                    } else {
                        "the console closed the control session"
                    },
                );
                self.request_end(if error { EndReason::ChannelError } else { EndReason::ConsoleClosed });
            }
            _ => {}
        }
        self.advance(now_us);
    }

    pub fn on_datagram(&mut self, now_us: u64, socket: Socket, from: Endpoint, data: &[u8]) {
        match socket {
            Socket::Arm => {
                if discovery::is_arm_reply(self.cfg.pairing.is_ps5, data)
                    && let Step::Arm { settle, .. } = &mut self.step
                    && settle.is_none()
                {
                    *settle = Some(now_us + ARM_SETTLE_US);
                    log(&mut self.events, LogLevel::Info, "the control listener is armed");
                }
            }
            Socket::ControlLeg => {
                if let Some((s, g)) = self.stun.as_mut()
                    && *s == socket
                    && stun::parse(data).is_some()
                {
                    g.on_datagram(now_us, data);
                } else if let Some(e) = self.exchange.as_mut() {
                    e.on_datagram(now_us, data);
                } else if let Control::Dgram(p) = &mut self.control {
                    p.on_datagram(now_us, data);
                } else if let Some(c) = self.channel.as_mut() {
                    c.on_datagram(now_us, data);
                }
            }
            Socket::MediaLeg => {
                if let Some((s, g)) = self.stun.as_mut()
                    && *s == socket
                    && stun::parse(data).is_some()
                {
                    g.on_datagram(now_us, data);
                } else if self.media_peer.is_some_and(|p| p != from) {
                    // Only the console's endpoint is the console (HalyardTakionStream's RemoteEndPoint
                    // check). Not activity either.
                    self.outcome.stray_dropped += 1;
                } else if self.senkusha.is_some() {
                    self.on_senkusha_datagram(now_us, data);
                } else if self.stream.is_some() {
                    self.on_stream_datagram(now_us, data);
                } else if let Some(c) = self.media_channel.as_mut() {
                    c.on_datagram(now_us, data);
                }
            }
            // The LAN sockets read only the console's endpoint, as every .NET Takion reader does.
            Socket::Senkusha if from != Endpoint::new(self.cfg.console, self.cfg.senkusha_port) => {
                self.outcome.stray_dropped += 1;
            }
            Socket::Stream if from != Endpoint::new(self.cfg.console, self.cfg.stream_port) => {
                self.outcome.stray_dropped += 1;
            }
            Socket::Senkusha => self.on_senkusha_datagram(now_us, data),
            Socket::Stream => self.on_stream_datagram(now_us, data),
        }
        self.advance(now_us);
    }

    /// Senkusha's socket carries its Takion association, the echoes of our pings, and the console's
    /// downstream MTU datagrams.
    fn on_senkusha_datagram(&mut self, now_us: u64, data: &[u8]) {
        if connection::is_control(data) {
            if let Some(s) = self.senkusha.as_mut() {
                self.senkusha_messages.extend(s.on_datagram(now_us, data));
            }
        } else if let Some(seq) = crate::takion::senkusha::echo_sequence(data) {
            self.probe_echoes.push_back(seq);
        } else if data.first().is_some_and(|b| b & 0x0f == BASE_TYPE_SENKUSHA_MTU) {
            self.mtu_probes_received += 1;
        }
    }

    fn on_stream_datagram(&mut self, now_us: u64, data: &[u8]) {
        if let Some(l) = self.live.as_mut() {
            l.last_activity = now_us;
        }
        if connection::is_control(data) {
            if let Some(s) = self.stream.as_mut() {
                self.stream_messages.extend(s.on_datagram(now_us, data));
            }
            return;
        }
        let (Some(demux), Some(live)) = (self.demux.as_mut(), self.live.as_mut()) else { return };
        live.window_bytes += data.len() as u64;
        let mut sink =
            Sink { events: &mut self.events, video: 0, audio: 0, keyframes: 0, losses: Vec::new() };
        demux.ingest(data, &mut sink);
        let (video, audio, keyframes) = (sink.video, sink.audio, sink.keyframes);
        let losses = std::mem::take(&mut sink.losses);
        live.video_frames += video;
        live.audio_frames += audio;
        live.keyframes += keyframes;
        if video > 0 {
            live.last_video = now_us;
        }
        if keyframes > 0 {
            // Cleared on arrival, not when the request went out: a request that produced nothing has not
            // fixed anything.
            live.idr_awaiting = false;
        }
        if !losses.is_empty() {
            live.idr_awaiting = true;
        }
        // CORRUPT_FRAME for each range the demuxer lost, as .NET reports it; the IDR latch asks for the repair.
        for (first, last) in losses {
            if let Some(s) = self.stream.as_mut()
                && s.send(
                    now_us,
                    CHANNEL_SESSION,
                    &tc::build_corrupt_frame(u32::from(first), u32::from(last)),
                )
                .is_ok()
            {
                self.outcome.corrupt_frames_sent += 1;
            }
        }
        if keyframes > 0 && self.reached < Stage::Streaming {
            self.reach(Stage::Streaming);
            self.announce(Stage::Streaming);
        }
    }

    pub fn handle_timeout(&mut self, now_us: u64) {
        if let Some(s) = self.senkusha.as_mut() {
            s.handle_timeout(now_us);
        }
        if let Some(s) = self.stream.as_mut() {
            s.handle_timeout(now_us);
        }
        if let Some(c) = self.channel.as_mut() {
            c.handle_timeout(now_us);
        }
        if let Some(c) = self.media_channel.as_mut() {
            c.handle_timeout(now_us);
        }
        if let Some(e) = self.exchange.as_mut() {
            e.handle_timeout(now_us);
        }
        if let Control::Dgram(p) = &mut self.control {
            p.handle_timeout(now_us);
        }
        if let Some((_, g)) = self.stun.as_mut() {
            g.handle_timeout(now_us);
        }
        self.advance(now_us);
    }

    // ---- Bookkeeping ---------------------------------------------------------------------------------

    fn request_end(&mut self, reason: EndReason) {
        if self.end.is_none() && !matches!(self.step, Step::Ended) {
            self.end = Some(reason);
        }
    }

    fn announce(&mut self, stage: Stage) {
        self.events.push_back(Event::Stage(stage));
    }

    fn reach(&mut self, stage: Stage) {
        if stage > self.reached {
            self.reached = stage;
        }
        self.outcome.stage = Some(self.reached);
    }

    /// Moves everything the components queued onto the host's I/O queue.
    fn flush(&mut self) {
        let console = self.cfg.console;
        if let Some(s) = self.senkusha.as_mut() {
            let (socket, to) = match self.cfg.route {
                Route::Local => (Socket::Senkusha, Endpoint::new(console, self.cfg.senkusha_port)),
                Route::Rendezvous => (Socket::MediaLeg, self.media_peer.unwrap_or_default()),
            };
            while let Some(d) = s.poll_transmit() {
                self.io.push_back(Io::UdpSend { socket, to, data: d });
            }
        }
        let (socket, to) = self.stream_target();
        if let Some(s) = self.stream.as_mut() {
            while let Some(d) = s.poll_transmit() {
                self.io.push_back(Io::UdpSend { socket, to, data: d });
            }
        }
        let control_to = self.control_peer.unwrap_or_default();
        let mut control_out = Vec::new();
        if let Some(c) = self.channel.as_mut() {
            control_out.extend(std::iter::from_fn(|| c.poll_transmit()));
        }
        if let Some(e) = self.exchange.as_mut() {
            control_out.extend(std::iter::from_fn(|| e.poll_transmit()));
        }
        match &mut self.control {
            Control::Dgram(p) => control_out.extend(std::iter::from_fn(|| p.poll_transmit())),
            Control::Tcp { session: Some(s), .. } => {
                while let Some(frame) = s.poll_transmit() {
                    self.io.push_back(Io::TcpSend(frame));
                }
            }
            _ => {}
        }
        for data in control_out {
            self.io.push_back(Io::UdpSend { socket: Socket::ControlLeg, to: control_to, data });
        }
        if let Some(c) = self.media_channel.as_mut() {
            let to = self.media_peer.unwrap_or_default();
            while let Some(d) = c.poll_transmit() {
                self.io.push_back(Io::UdpSend { socket: Socket::MediaLeg, to, data: d });
            }
        }
        if let Some((socket, g)) = self.stun.as_mut() {
            while let Some((to, data)) = g.poll_transmit() {
                self.io.push_back(Io::UdpSend { socket: *socket, to, data });
            }
        }
    }

    fn stream_target(&self) -> (Socket, Endpoint) {
        match self.cfg.route {
            Route::Local => (Socket::Stream, Endpoint::new(self.cfg.console, self.cfg.stream_port)),
            Route::Rendezvous => (Socket::MediaLeg, self.media_peer.unwrap_or_default()),
        }
    }

    /// Everything the control session said since the last call: SESSION_ID, the sign-in gate, the verdict,
    /// STREAM_READY. Heartbeats are answered inside the session.
    fn service_control(&mut self) {
        let mut said = Vec::new();
        let mut plane_events = Vec::new();
        match &mut self.control {
            Control::Tcp { session: Some(s), .. } => said.extend(std::iter::from_fn(|| s.poll_event())),
            Control::Dgram(p) => plane_events.extend(std::iter::from_fn(|| p.poll_event())),
            _ => {}
        }
        for e in plane_events {
            match e {
                rendezvous::Event::Session(e) => said.push(e),
                rendezvous::Event::Connected => {}
                rendezvous::Event::Closed => {
                    self.log(LogLevel::Warn, "the console closed the control session");
                    self.request_end(EndReason::ConsoleClosed);
                }
                rendezvous::Event::Failed(error) => {
                    self.log(LogLevel::Warn, format!("the control plane failed: {error:?}"));
                    self.request_end(match error {
                        rendezvous::Error::Timeout(_) => EndReason::Timeout,
                        rendezvous::Error::Init(_) | rendezvous::Error::CtrlRefused { .. } => {
                            EndReason::Refused
                        }
                        _ => EndReason::ChannelError,
                    });
                }
            }
        }
        for e in said {
            match e {
                ctrl_session::Event::SessionReady => self.session_ready = true,
                ctrl_session::Event::StreamReady => self.stream_ready = true,
                ctrl_session::Event::LoginPrompt => {
                    self.login_prompt = true;
                    self.outcome.login_prompted = true;
                }
                ctrl_session::Event::Frame { kind: ctrl::LOGIN, plaintext: Some(p), .. } if !p.is_empty() => {
                    self.record_verdict(p[0])
                }
                ctrl_session::Event::Frame { kind: ctrl::ECHO_PROBE_ACK, .. } => {
                    self.outcome.control_echo_answered = true;
                    self.log(LogLevel::Info, "the console answered the control echo probe (0x8910)");
                }
                ctrl_session::Event::LoginResult { accepted } => {
                    self.record_verdict(if accepted { ctrl::LOGIN_ACCEPTED } else { ctrl::LOGIN_REJECTED })
                }
                _ => {}
            }
        }
    }

    fn record_verdict(&mut self, byte: u8) {
        self.outcome.login_verdict_byte = Some(byte);
        self.outcome.login_verdict = match byte {
            ctrl::LOGIN_ACCEPTED => Some(true),
            ctrl::LOGIN_REJECTED => Some(false),
            _ => None,
        };
        self.verdict = Some(byte);
        self.log(LogLevel::Info, format!("the console answered the passcode (verdict 0x{byte:02x})"));
    }

    // ---- The control session -------------------------------------------------------------------------

    fn start_control(&mut self, now_us: u64) {
        self.announce(Stage::ControlOpen);
        self.control_deadline = now_us
            + match self.cfg.route {
                Route::Local => CONTROL_DEADLINE_LAN_US,
                Route::Rendezvous => CONTROL_DEADLINE_RENDEZVOUS_US,
            };
        match self.cfg.route {
            Route::Local => {
                self.log(LogLevel::Info, "opening the control session (arm, /sess/init, /sess/ctrl)");
                self.control = Control::Tcp { buf: Vec::new(), session: None };
                self.io.push_back(Io::UdpOpen { socket: Socket::Arm, local_port: 0, rcvbuf: 0 });
                self.step = Step::Arm { deadline: now_us + ARM_REPLY_WINDOW_US, settle: None };
            }
            Route::Rendezvous => {
                let Some(channel) = self.channel.take() else {
                    self.log(LogLevel::Error, "the rendezvous route needs prepare and begin before connect");
                    self.request_end(EndReason::ChannelError);
                    return;
                };
                let addressing =
                    Addressing::new(self.cfg.console, RENDEZVOUS_CONTROL_PORT, ConnectionPath::Rendezvous);
                let p = &self.cfg.pairing;
                let values = CtrlValues {
                    registration_key: p.registration_key.clone(),
                    device_id: self.cfg.device_id.clone(),
                    os_major: self.cfg.os_version.0,
                    os_minor: self.cfg.os_version.1,
                    start_bitrate_kbps: self.cfg.bitrate_kbps,
                    streaming_type: self.cfg.streaming_type,
                };
                let plane = ControlPlane::new(channel, p.is_ps5, addressing, p.companion, values, now_us);
                self.control = Control::Dgram(Box::new(plane));
                self.step = Step::DgramControl;
            }
        }
    }

    fn addressing(&self) -> Addressing {
        Addressing::new(self.cfg.console, self.cfg.control_port, ConnectionPath::Local)
    }

    fn send_sess_request(&mut self, _now_us: u64) {
        let is_ps5 = self.cfg.pairing.is_ps5;
        let addressing = self.addressing();
        let request = match &mut self.step {
            Step::TcpInit { .. } => requests::init(is_ps5, &addressing, &self.cfg.pairing.registration_key),
            Step::TcpCtrl { field: Some(field), .. } => {
                let p = &self.cfg.pairing;
                requests::ctrl(
                    is_ps5,
                    &addressing,
                    field,
                    &CtrlFields {
                        registration_key: &p.registration_key,
                        device_id: &self.cfg.device_id,
                        os_major: self.cfg.os_version.0,
                        os_minor: self.cfg.os_version.1,
                        start_bitrate_kbps: self.cfg.bitrate_kbps,
                        streaming_type: self.cfg.streaming_type,
                    },
                )
            }
            _ => return,
        };
        if let Control::Tcp { buf, .. } = &mut self.control {
            buf.clear();
        }
        self.io.push_back(Io::TcpSend(request));
    }

    fn response_ready(&self) -> bool {
        matches!(&self.control, Control::Tcp { buf, session: None } if Response::parse(buf).is_some())
    }

    fn tcp_close(&mut self) {
        self.tcp_live = false;
        self.io.push_back(Io::TcpClose);
    }

    fn tcp_connect(&mut self, now_us: u64, init: bool) {
        if let Control::Tcp { buf, .. } = &mut self.control {
            buf.clear();
        }
        let to = Endpoint::new(self.cfg.console, self.cfg.control_port);
        self.io.push_back(Io::TcpConnect(to));
        let _ = now_us;
        let deadline = self.control_deadline;
        if init {
            self.step = Step::TcpInit { connected: false, deadline };
        } else if let Step::TcpCtrl { field, .. } = std::mem::replace(&mut self.step, Step::Idle) {
            self.step = Step::TcpCtrl { connected: false, deadline, field };
        }
    }

    // ---- The machine ---------------------------------------------------------------------------------

    fn advance(&mut self, now_us: u64) {
        loop {
            self.service_control();
            self.settle_stun();
            self.flush();
            if matches!(self.step, Step::Ended) {
                return;
            }
            if let Some(reason) = self.end {
                self.finish(reason, now_us);
                return;
            }
            if !self.step_once(now_us) {
                self.flush();
                if self.end.is_some() {
                    continue;
                }
                return;
            }
        }
    }

    /// One transition, if the current step has one due. `true` if it moved.
    fn step_once(&mut self, now: u64) -> bool {
        match std::mem::replace(&mut self.step, Step::Idle) {
            Step::Idle => false,

            // ---- LAN control ----
            Step::Arm { deadline, settle } => {
                if self.opened.contains_key(&Socket::Arm) && !self.arm_sent() {
                    self.send_arm_probe();
                }
                // Best-effort: a failed arm socket or a silent console only skips the wait for a reply.
                let proceed = self.open_failed.contains(&Socket::Arm) || settle.is_some_and(|s| now >= s);
                if !proceed {
                    let settle = settle.or((now >= deadline).then_some(deadline + ARM_SETTLE_US));
                    self.step = Step::Arm { deadline, settle };
                    return false;
                }
                self.io.push_back(Io::UdpClose(Socket::Arm));
                self.opened.remove(&Socket::Arm);
                self.tcp_connect(now, true);
                true
            }
            Step::TcpInit { connected, deadline } => {
                if let Control::Tcp { buf, session: None } = &self.control
                    && let Some(reply) = Response::parse(buf)
                {
                    let result =
                        requests::open_init(self.cfg.pairing.is_ps5, &reply, &self.cfg.pairing.companion);
                    // /sess/init is served Connection: close.
                    self.tcp_close();
                    match result {
                        Ok(field) => {
                            self.step = Step::TcpCtrl { connected: false, deadline: 0, field: Some(field) };
                            self.tcp_connect(now, false);
                        }
                        Err(e) => {
                            self.log(LogLevel::Warn, format!("/sess/init: {e:?}"));
                            self.request_end(EndReason::Refused);
                        }
                    }
                    return true;
                }
                if now >= deadline {
                    self.log(
                        LogLevel::Warn,
                        if connected { "/sess/init: no response" } else { "the control port did not accept" },
                    );
                    self.tcp_close();
                    self.request_end(if connected { EndReason::Timeout } else { EndReason::ChannelError });
                    return true;
                }
                self.step = Step::TcpInit { connected, deadline };
                false
            }
            Step::TcpCtrl { connected, deadline, field } => {
                if let Control::Tcp { buf, session } = &mut self.control
                    && session.is_none()
                    && let Some(reply) = Response::parse(buf)
                {
                    if !reply.is_success() {
                        let status = reply.status;
                        log(&mut self.events, LogLevel::Warn, format!("/sess/ctrl refused ({status})"));
                        self.request_end(EndReason::Refused);
                        return true;
                    }
                    let leftover = buf[reply.consumed..].to_vec();
                    let Some(field) = field else { return true };
                    *session = Some(ControlSession::new(field, self.cfg.pairing.is_ps5, &leftover));
                    buf.clear();
                    self.reach(Stage::ControlOpen);
                    self.start_signin(now);
                    return true;
                }
                if now >= deadline {
                    self.log(
                        LogLevel::Warn,
                        if connected { "/sess/ctrl: no response" } else { "the control port did not accept" },
                    );
                    self.tcp_close();
                    self.request_end(if connected { EndReason::Timeout } else { EndReason::ChannelError });
                    return true;
                }
                self.step = Step::TcpCtrl { connected, deadline, field };
                false
            }

            // ---- Rendezvous control ----
            Step::DgramControl => {
                if matches!(self.control, Control::Dgram(_)) && self.control.session().is_some() {
                    self.reach(Stage::ControlOpen);
                    self.start_signin(now);
                    return true;
                }
                if now >= self.control_deadline {
                    self.log(LogLevel::Warn, "the control plane timed out");
                    self.request_end(EndReason::Timeout);
                    return true;
                }
                self.step = Step::DgramControl;
                false
            }

            // ---- Sign-in ----
            Step::SigninPrompt { deadline } => {
                if self.session_ready || self.login_prompt || now >= deadline {
                    if self.login_prompt && !self.session_ready {
                        self.start_passcode(now, SigninResume::Gate);
                    } else {
                        self.finish_signin(now);
                    }
                    return true;
                }
                self.step = Step::SigninPrompt { deadline };
                false
            }
            Step::SigninAsk { retry, attempt, refusals, resume } => match self.passcode.take() {
                None => {
                    self.step = Step::SigninAsk { retry, attempt, refusals, resume };
                    false
                }
                Some(Some(pin)) if !pin.is_empty() => {
                    self.submit(now, attempt, refusals, pin, resume);
                    true
                }
                Some(_) => {
                    self.request_end(EndReason::SigninCancelled);
                    true
                }
            },
            Step::SigninVerdict { attempt, refusals, pin, deadline, resume } => {
                if self.session_ready {
                    self.signin_done(now, resume);
                    return true;
                }
                if let Some(byte) = self.verdict.take() {
                    match byte {
                        ctrl::LOGIN_REJECTED => {
                            let refusals = refusals + 1;
                            self.log(LogLevel::Warn, "the console refused that passcode");
                            if attempt >= SIGNIN_ATTEMPTS {
                                self.request_end(EndReason::SigninRejected);
                            } else {
                                self.ask(refusals, attempt + 1, refusals, resume);
                            }
                        }
                        ctrl::LOGIN_ACCEPTED => {
                            // SESSION_ID does not always follow at once.
                            self.reach(Stage::SignedIn);
                            self.announce(Stage::SessionReady);
                            let wait = if self.cfg.route == Route::Rendezvous {
                                SIGNIN_WAIT_US
                            } else {
                                SESSION_AFTER_LOGIN_US
                            };
                            self.step = Step::SigninAccepted { deadline: now + wait, resume };
                        }
                        _ => {
                            self.log(LogLevel::Warn, "a verdict byte nobody has seen - not retrying");
                            self.request_end(EndReason::SigninRejected);
                        }
                    }
                    return true;
                }
                if now >= deadline {
                    if attempt >= SIGNIN_ATTEMPTS {
                        // Refused every time, or never answered at all: the second is a timeout.
                        self.request_end(if refusals > 0 {
                            EndReason::SigninRejected
                        } else {
                            EndReason::Timeout
                        });
                    } else {
                        self.log(
                            LogLevel::Warn,
                            "the console did not answer the passcode - sending it again",
                        );
                        self.submit(now, attempt + 1, refusals, pin, resume);
                    }
                    return true;
                }
                self.step = Step::SigninVerdict { attempt, refusals, pin, deadline, resume };
                false
            }
            Step::SigninAccepted { deadline, resume } => {
                if self.session_ready || now >= deadline {
                    self.signin_done(now, resume);
                    return true;
                }
                self.step = Step::SigninAccepted { deadline, resume };
                false
            }

            // ---- The rendezvous A/V leg ----
            Step::MediaOpen => {
                if self.open_failed.contains(&Socket::MediaLeg) {
                    self.request_end(EndReason::ChannelError);
                    return true;
                }
                if let Some(o) = self.opened.get(&Socket::MediaLeg) {
                    self.outcome.media_local_port = o.local_port;
                    self.outcome.rcvbuf_granted = o.rcvbuf;
                    self.step = Step::MediaStun;
                    return true;
                }
                self.step = Step::MediaOpen;
                false
            }
            Step::MediaStun => {
                if self.stun.as_ref().is_some_and(|(s, g)| *s == Socket::MediaLeg && g.outcome().is_none()) {
                    self.step = Step::MediaStun;
                    return false;
                }
                self.stun = None;
                self.events.push_back(Event::MediaWanted(self.media_leg));
                self.step =
                    Step::MediaAwaitPeer { deadline: now + self.cfg.media_offer_timeout_us, asked: now };
                true
            }
            Step::MediaAwaitPeer { deadline, asked } => {
                if self.login_prompt && !self.session_ready && self.outcome.login_attempts == 0 {
                    self.log(
                        LogLevel::Warn,
                        format!(
                            "the console asked for its passcode late ({} ms into the media wait)",
                            (now - asked) / 1000
                        ),
                    );
                    self.start_passcode(now, SigninResume::LateMedia);
                    return true;
                }
                match self.media_answer.take() {
                    Some(Some(peer)) => {
                        self.start_media_prelude(now, peer);
                        true
                    }
                    Some(None) => {
                        self.log(
                            LogLevel::Warn,
                            "the host gave up on the media connection - there is no A/V path",
                        );
                        self.request_end(EndReason::NoMedia);
                        true
                    }
                    None if now >= deadline => {
                        self.log(
                            LogLevel::Warn,
                            "the console never offered a media connection - there is no A/V path",
                        );
                        self.request_end(EndReason::NoMedia);
                        true
                    }
                    None => {
                        self.step = Step::MediaAwaitPeer { deadline, asked };
                        false
                    }
                }
            }
            Step::MediaPrelude => match self.media_channel.as_mut().and_then(Channel::poll_stage) {
                None => {
                    self.step = Step::MediaPrelude;
                    false
                }
                Some(StageResult::Done) => {
                    self.outcome.media_prelude_ok = true;
                    self.step =
                        Step::MediaSessionReady { started: now, deadline: now + SESSION_READY_WINDOW_US };
                    true
                }
                Some(other) => {
                    self.log(LogLevel::Warn, format!("the A/V leg's prelude: {other:?}"));
                    self.request_end(match other {
                        StageResult::Timeout(_) => EndReason::Timeout,
                        _ => EndReason::ChannelError,
                    });
                    true
                }
            },
            Step::MediaSessionReady { started, deadline } => {
                if self.session_ready || now >= deadline {
                    if self.session_ready {
                        self.outcome.session_ready_waited_ms = Some(((now - started) / 1000) as u32);
                        self.reach(Stage::SessionReady);
                    } else {
                        self.log(
                            LogLevel::Warn,
                            "no SESSION_ID after the A/V prelude - continuing, as .NET does",
                        );
                    }
                    self.start_senkusha(now);
                    return true;
                }
                self.step = Step::MediaSessionReady { started, deadline };
                false
            }

            // ---- Senkusha ----
            Step::SenkushaOpen { deadline } => {
                if self.open_failed.contains(&Socket::Senkusha) {
                    self.log(LogLevel::Warn, "no senkusha socket - continuing without it");
                    self.after_senkusha(now);
                    return true;
                }
                if self.opened.contains_key(&Socket::Senkusha) {
                    self.senkusha_connect(now, deadline);
                    return true;
                }
                self.step = Step::SenkushaOpen { deadline };
                false
            }
            Step::SenkushaHandshake { deadline } => {
                let state = self.senkusha.as_ref().map(Connection::state);
                if state == Some(connection::State::Established) {
                    self.reach(Stage::SenkushaUp);
                    let request = tc::build_protocol_version_request(&[9]).unwrap_or_default();
                    let _ = self.senkusha.as_mut().map(|s| s.send(now, CHANNEL_PROTOCOL_VERSION, &request));
                    self.step = Step::SenkushaVersion {
                        deadline: (now + TAKION_REPLY_US).min(deadline),
                        asked: now,
                        box_end: deadline,
                    };
                    return true;
                }
                if state == Some(connection::State::Failed) || now >= deadline {
                    self.after_senkusha(now);
                    return true;
                }
                self.step = Step::SenkushaHandshake { deadline };
                false
            }
            Step::SenkushaVersion { deadline, asked, box_end } => {
                let got = take_control(&mut self.senkusha_messages, tc::PROTOCOL_VERSION_ACK).is_some();
                if got || now >= deadline {
                    if got {
                        self.outcome.version_rtt_ms = Some(((now - asked) / 1000).min(1000) as u32);
                        self.measured_rtt_us = Some(now - asked);
                    }
                    self.senkusha_session_asked = now;
                    // A keyless SESSION exchange whose whole purpose is to have happened. `encryptedKey` is present
                    // and empty, as the vendor sends it: `22 00` in every keyless senkusha request in cap53 and
                    // cap54. It was four zero bytes, the stream request's value, until 2026-09-26.
                    let request = SessionRequest {
                        client_version: 9,
                        session_key: b"",
                        launch_spec: b"",
                        encrypted_key: b"",
                        ecdh_public_key: None,
                        ecdh_signature: None,
                    }
                    .build();
                    let _ = self.senkusha.as_mut().map(|s| s.send(now, CHANNEL_SESSION, &request));
                    self.step = Step::SenkushaSession { deadline: (now + TAKION_REPLY_US).min(box_end) };
                    return true;
                }
                self.step = Step::SenkushaVersion { deadline, asked, box_end };
                false
            }
            Step::SenkushaSession { deadline } => {
                let got = take_control(&mut self.senkusha_messages, tc::SESSION_REPLY).is_some();
                if got || now >= deadline {
                    self.outcome.senkusha_ok = got;
                    if got {
                        let rtt = now - self.senkusha_session_asked;
                        self.measured_rtt_us = Some(self.measured_rtt_us.map_or(rtt, |r| r.min(rtt)));
                        self.start_echo_probe(now);
                    } else {
                        self.after_senkusha(now);
                    }
                    return true;
                }
                self.step = Step::SenkushaSession { deadline };
                false
            }
            Step::SenkushaEcho { seq, sent_at, deadline, budget_end } => {
                let mut answered = false;
                while let Some(echoed) = self.probe_echoes.pop_front() {
                    // A late echo of an earlier ping is dropped, not credited to this one.
                    if echoed == seq {
                        answered = true;
                        break;
                    }
                }
                if answered {
                    self.echo_samples.push(now - sent_at);
                }
                if now >= budget_end || now >= self.senkusha_box {
                    self.finish_echo_probe(now);
                    return true;
                }
                if answered || now >= deadline {
                    if seq + 1 >= crate::takion::senkusha::PING_COUNT {
                        self.finish_echo_probe(now);
                    } else {
                        self.send_ping(now, seq + 1);
                        self.step = Step::SenkushaEcho {
                            seq: seq + 1,
                            sent_at: now,
                            deadline: now + ECHO_REPLY_US,
                            budget_end,
                        };
                    }
                    return true;
                }
                self.step = Step::SenkushaEcho { seq, sent_at, deadline, budget_end };
                false
            }
            Step::SenkushaMtuDown { mtu, deadline, budget_end } => {
                // The console's reply ends the test; what arrived is the evidence.
                let replied = self.take_probe_reply(tc::PROBE_MTU_COMMAND);
                if replied || now >= deadline || now >= budget_end {
                    if self.mtu_probes_received > 0 && now < budget_end {
                        self.send_senkusha(now, &tc::build_client_mtu_command(1, mtu, true));
                        self.step =
                            Step::SenkushaMtuUpReply { mtu, deadline: now + MTU_REPLY_US, budget_end };
                    } else {
                        self.log(LogLevel::Info, format!("MTU {mtu} not confirmed downstream"));
                        self.after_senkusha(now);
                    }
                    return true;
                }
                self.step = Step::SenkushaMtuDown { mtu, deadline, budget_end };
                false
            }
            Step::SenkushaMtuUpReply { mtu, deadline, budget_end } => {
                // As in .NET, the probe packet goes out whether or not the console acknowledged echo mode.
                if self.take_probe_reply(tc::PROBE_CLIENT_MTU_COMMAND) || now >= deadline || now >= budget_end
                {
                    self.probe_echoes.clear();
                    let payload = (mtu as usize).saturating_sub(crate::takion::senkusha::IP_UDP_OVERHEAD);
                    if let Some(ping) = crate::takion::senkusha::build(0, now, payload, MTU_PADDING) {
                        self.send_raw_senkusha(ping);
                    }
                    self.step = Step::SenkushaMtuUpEcho {
                        mtu,
                        deadline: (now + ECHO_REPLY_US).min(budget_end.max(now)),
                    };
                    return true;
                }
                self.step = Step::SenkushaMtuUpReply { mtu, deadline, budget_end };
                false
            }
            Step::SenkushaMtuUpEcho { mtu, deadline } => {
                let echoed = self.probe_echoes.iter().any(|&s| s == 0);
                if echoed || now >= deadline {
                    if echoed {
                        self.confirmed_mtu = Some(mtu);
                        self.outcome.mtu_confirmed = true;
                    }
                    // The console is in client-MTU mode until told otherwise, however the test ended.
                    self.send_senkusha(now, &tc::build_client_mtu_command(2, mtu, false));
                    self.log(
                        LogLevel::Info,
                        format!("MTU {mtu} {}", if echoed { "confirmed" } else { "not confirmed upstream" }),
                    );
                    self.after_senkusha(now);
                    return true;
                }
                self.step = Step::SenkushaMtuUpEcho { mtu, deadline };
                false
            }

            // ---- STREAM_READY (rendezvous) ----
            Step::StreamReadyWait { deadline } => {
                if self.stream_ready || now >= deadline {
                    self.outcome.stream_ready_seen = self.stream_ready;
                    if !self.stream_ready {
                        self.log(LogLevel::Warn, "STREAM_READY never arrived; opening the stream anyway");
                    }
                    self.start_takion(now);
                    return true;
                }
                self.step = Step::StreamReadyWait { deadline };
                false
            }

            // ---- The stream's Takion ----
            Step::TakionOpen | Step::TakionHandshake if now >= self.stream_box => {
                self.log(LogLevel::Warn, "the stream's Takion handshake was not answered");
                self.request_end(EndReason::Timeout);
                true
            }
            Step::TakionOpen => {
                if self.open_failed.contains(&Socket::Stream) {
                    self.request_end(EndReason::ChannelError);
                    return true;
                }
                if let Some(o) = self.opened.get(&Socket::Stream) {
                    self.outcome.rcvbuf_granted = o.rcvbuf;
                    self.takion_connect(now);
                    return true;
                }
                self.step = Step::TakionOpen;
                false
            }
            Step::TakionHandshake => match self.stream.as_ref().map(Connection::state) {
                Some(connection::State::Established) => {
                    self.reach(Stage::TakionUp);
                    self.start_keys(now);
                    true
                }
                Some(connection::State::Failed) => {
                    self.log(LogLevel::Warn, "the stream's Takion handshake was not answered");
                    self.request_end(EndReason::Timeout);
                    true
                }
                _ => {
                    self.step = Step::TakionHandshake;
                    false
                }
            },

            // ---- Key agreement ----
            Step::KeysVersion { deadline } => {
                let ack = match self.take_stream_control(tc::PROTOCOL_VERSION_ACK) {
                    Err(()) => return true,
                    Ok(ack) => ack,
                };
                if ack.is_none() && now < deadline {
                    self.step = Step::KeysVersion { deadline };
                    return false;
                }
                // A missing field is not an error: fall back to what was asked for.
                let version = ack
                    .and_then(|m| tc::parse_protocol_version_ack(&m))
                    .filter(|&v| v != 0)
                    .unwrap_or(negotiator::CLIENT_VERSION);
                self.outcome.stream_version = version;
                self.send_session_request(now, version);
                true
            }
            Step::KeysReply { deadline } => {
                let reply = match self.take_stream_control(tc::SESSION_REPLY) {
                    Err(()) => return true,
                    Ok(reply) => reply,
                };
                let Some(reply) = reply else {
                    if now >= deadline {
                        self.log(LogLevel::Warn, "no SESSION_REPLY");
                        self.request_end(EndReason::Timeout);
                        return true;
                    }
                    self.step = Step::KeysReply { deadline };
                    return false;
                };
                let Some(neg) = self.negotiator.take() else { return true };
                match neg.accept_reply(&reply, self.ecdh.as_ref()) {
                    Ok(keys) => {
                        self.keys = Some(keys);
                        self.reach(Stage::StreamKeys);
                        self.start_stream_info(now);
                    }
                    Err(reject) => {
                        self.outcome.reply_reject = Some(reject);
                        self.log(LogLevel::Error, format!("SESSION_REPLY refused ({reject:?})"));
                        self.request_end(EndReason::Refused);
                    }
                }
                true
            }

            // ---- STREAM_INFO ----
            Step::StreamInfoWait { deadline } => {
                let message = match self.take_stream_control(tc::STREAM_INFO) {
                    Err(()) => return true,
                    Ok(m) => m,
                };
                let Some(message) = message else {
                    if now >= deadline {
                        self.log(LogLevel::Warn, "the console never described the stream");
                        self.request_end(EndReason::Timeout);
                        return true;
                    }
                    self.step = Step::StreamInfoWait { deadline };
                    return false;
                };
                self.on_stream_info(now, &message);
                true
            }

            Step::Streaming => {
                self.service_stream(now);
                self.step = Step::Streaming;
                false
            }
            Step::Ended => {
                self.step = Step::Ended;
                false
            }
        }
    }

    // ---- Steps' entry points -------------------------------------------------------------------------

    fn arm_sent(&self) -> bool {
        self.arm_probe_sent
    }

    fn send_arm_probe(&mut self) {
        let probe = discovery::arm_probe(self.cfg.pairing.is_ps5).to_vec();
        let port = self.cfg.control_port;
        // Both, because a console that has not been spoken to recently answers the broadcast.
        let unicast = Endpoint::new(self.cfg.console, port);
        self.io.push_back(Io::UdpSend { socket: Socket::Arm, to: unicast, data: probe.clone() });
        if self.cfg.arm_broadcast {
            self.io.push_back(Io::UdpSend {
                socket: Socket::Arm,
                to: Endpoint::new([255; 4], port),
                data: probe,
            });
        }
        self.arm_probe_sent = true;
    }
}

/// Takes the first queued message of `want`, discarding the others before it (heartbeats and the like:
/// taken, not wanted).
fn take_control(queue: &mut VecDeque<Message>, want: u32) -> Option<Vec<u8>> {
    while let Some(m) = queue.pop_front() {
        if tc::peek_type(&m.payload) == Some(want) {
            return Some(m.payload);
        }
    }
    None
}

impl Session {
    /// As [`take_control`] on the stream, where a DISCONNECT is an answer: the console hanging up with a
    /// reason (a rejected launch spec does exactly that) ends the session with it. `Err` when it did.
    fn take_stream_control(&mut self, want: u32) -> Result<Option<Vec<u8>>, ()> {
        while let Some(m) = self.stream_messages.pop_front() {
            match tc::peek_type(&m.payload) {
                Some(t) if t == want => return Ok(Some(m.payload)),
                Some(tc::DISCONNECT) => {
                    self.console_disconnected(&m.payload);
                    return Err(());
                }
                _ => {}
            }
        }
        Ok(None)
    }

    fn console_disconnected(&mut self, message: &[u8]) {
        let reason = tc::parse_disconnect(message).map(|r| String::from_utf8_lossy(r).into_owned());
        self.log(
            LogLevel::Warn,
            format!("the console sent DISCONNECT ({:?})", reason.as_deref().unwrap_or("")),
        );
        self.outcome.console_disconnect_reason = reason;
        self.request_end(EndReason::ConsoleClosed);
    }

    // ---- Sign-in -------------------------------------------------------------------------------------

    /// Waits for SESSION_ID or a LOGIN_PROMPT inside the prompt window. A console that never asks has
    /// "signed in" by not asking.
    fn start_signin(&mut self, now: u64) {
        self.announce(Stage::SignedIn);
        self.step = Step::SigninPrompt { deadline: now + self.cfg.signin_prompt_window_us };
    }

    fn start_passcode(&mut self, now: u64, resume: SigninResume) {
        self.log(LogLevel::Info, "the console's user is locked and it wants a passcode");
        match self.cfg.pairing.login_pin.clone().filter(|p| !p.is_empty()) {
            Some(pin) => {
                self.log(LogLevel::Info, "using the passcode from the pairing record");
                self.submit(now, 1, 0, pin, resume);
            }
            None => self.ask(0, 1, 0, resume),
        }
    }

    fn ask(&mut self, retry: u32, attempt: u32, refusals: u32, resume: SigninResume) {
        self.passcode = None;
        self.events.push_back(Event::PasscodeWanted { retry });
        self.step = Step::SigninAsk { retry, attempt, refusals, resume };
    }

    /// Each submit at a fresh counter, which the control session guarantees.
    fn submit(&mut self, now: u64, attempt: u32, refusals: u32, pin: String, resume: SigninResume) {
        self.verdict = None;
        self.outcome.login_verdict = None;
        let sent = self.control.session().map(|s| s.submit_login(&pin));
        if sent != Some(true) {
            self.log(LogLevel::Error, "the passcode could not be sent (digits only)");
            self.request_end(EndReason::SigninRejected);
            return;
        }
        self.outcome.login_attempts = attempt;
        self.log(LogLevel::Info, format!("{} digit(s) submitted, attempt {attempt}", pin.len()));
        self.step = Step::SigninVerdict { attempt, refusals, pin, deadline: now + SIGNIN_WAIT_US, resume };
    }

    fn signin_done(&mut self, now: u64, resume: SigninResume) {
        match resume {
            SigninResume::Gate => self.finish_signin(now),
            SigninResume::LateMedia => {
                // As after the ordinary gate on this route, SESSION_ID may still wait for the A/V leg
                // ([X]); C insists here, which contradicts its own gate.
                if self.session_ready {
                    self.reach(Stage::SessionReady);
                }
                self.step =
                    Step::MediaAwaitPeer { deadline: now + self.cfg.media_offer_timeout_us, asked: now };
            }
        }
    }

    fn finish_signin(&mut self, now: u64) {
        self.reach(Stage::SignedIn);
        if self.cfg.route == Route::Rendezvous {
            self.send_control_echo_probe();
        }
        if self.cfg.route == Route::Rendezvous {
            // SESSION_ID comes only after the A/V leg's prelude on this route.
            if self.session_ready {
                self.reach(Stage::SessionReady);
            } else if self.login_prompt {
                self.log(
                    LogLevel::Warn,
                    "passcode accepted, no SESSION_ID yet - continuing to the A/V leg [X]",
                );
            }
            self.start_media(now);
            return;
        }
        if self.session_ready {
            self.reach(Stage::SessionReady);
        } else if self.cfg.require_session_ready {
            // A console that has not sent it silently drops every Takion INIT (the C ports' finding).
            self.log(LogLevel::Warn, "no SESSION_ID - the console is not willing to stream");
            self.request_end(if self.login_prompt { EndReason::SigninNoSession } else { EndReason::Timeout });
            return;
        } else {
            self.log(LogLevel::Warn, "no SESSION_ID - opening Takion anyway, as configured [X]");
        }
        self.start_senkusha(now);
    }

    // ---- STUN and the A/V leg ------------------------------------------------------------------------

    fn start_stun(&mut self, now: u64, socket: Socket, local_port: u16) {
        let leg = Leg { local_port, ..Leg::default() };
        match socket {
            Socket::ControlLeg => {
                self.control_leg = leg;
                self.outcome.control_local_port = local_port;
            }
            _ => self.media_leg = leg,
        }
        if self.cfg.stun_servers.is_empty() {
            if socket == Socket::ControlLeg {
                self.events.push_back(Event::ControlLegReady(self.control_leg));
            }
            return;
        }
        let mut g = Gatherer::new(
            self.cfg.stun_servers.clone(),
            self.cfg.stun_attempts,
            self.cfg.stun_timeout_us,
            2,
            share(&self.random),
        );
        g.start(now);
        self.stun = Some((socket, g));
    }

    /// Records a finished gather on its leg, and tells the host about the control leg's.
    fn settle_stun(&mut self) {
        let Some((socket, g)) = &self.stun else { return };
        let Some(outcome) = g.outcome() else { return };
        let answers = match outcome {
            StunOutcome::Answered(a) => a.clone(),
            StunOutcome::NoAnswer => Vec::new(),
        };
        let socket = *socket;
        let leg = if socket == Socket::ControlLeg { &mut self.control_leg } else { &mut self.media_leg };
        leg.reflexive = answers.iter().find_map(|(_, a)| a.ipv4()).map(|(a, p)| Endpoint::new(a, p));
        leg.endpoint_independent = stun::endpoint_independent(&answers);
        let leg = *leg;
        self.stun = None;
        if socket == Socket::ControlLeg {
            self.events.push_back(Event::ControlLegReady(leg));
        }
    }

    fn start_media(&mut self, _now: u64) {
        self.announce(Stage::SessionReady);
        self.io.push_back(Io::UdpOpen {
            socket: Socket::MediaLeg,
            local_port: self.cfg.media_local_port,
            rcvbuf: self.cfg.rcvbuf_bytes,
        });
        self.step = Step::MediaOpen;
    }

    fn start_media_prelude(&mut self, now: u64, peer: Peer) {
        self.media_peer = Some(peer.endpoint);
        let assoc = Association::new(
            share(&self.random),
            self.local_hashed_id,
            peer.console_hashed_id,
            peer.endpoint.address,
            peer.endpoint.port,
        );
        let mut channel = Channel::new(assoc, self.dgram_options());
        channel.establish(now);
        self.media_channel = Some(channel);
        let e = peer.endpoint;
        self.log(
            LogLevel::Info,
            format!("A/V leg to {}:{}, prelude", crate::net::candidates::format_ipv4(e.address), e.port),
        );
        self.step = Step::MediaPrelude;
    }

    // ---- Senkusha ------------------------------------------------------------------------------------

    fn start_senkusha(&mut self, now: u64) {
        self.announce(Stage::SenkushaUp);
        let deadline = now + SENKUSHA_BUDGET_US;
        match self.cfg.route {
            Route::Local => {
                self.io.push_back(Io::UdpOpen { socket: Socket::Senkusha, local_port: 0, rcvbuf: 0 });
                self.step = Step::SenkushaOpen { deadline };
            }
            Route::Rendezvous => {
                // On the A/V leg's own socket, as the captured client does: a first association there, then
                // a second for the stream. The media channel's work is done.
                self.media_channel = None;
                self.senkusha_connect(now, deadline);
            }
        }
    }

    fn senkusha_connect(&mut self, now: u64, deadline: u64) {
        self.senkusha_box = deadline;
        let config = connection::Config {
            max_attempts: self.cfg.senkusha_attempts,
            attempt_timeout_us: self.cfg.attempt_interval_us,
        };
        let mut c = Connection::new(self.random_u32(), config);
        c.connect(now);
        self.senkusha = Some(c);
        self.step = Step::SenkushaHandshake { deadline };
    }

    /// The control echo probe, where .NET sends it: after senkusha on the LAN, and after sign-in on the
    /// rendezvous route, whose senkusha runs later, on the A/V leg. It spends a client field counter.
    fn send_control_echo_probe(&mut self) {
        if !self.cfg.control_echo_probe {
            return;
        }
        if let Some(s) = self.control.session() {
            s.send_field(ctrl::ECHO_PROBE, &[0; 4]);
            self.outcome.control_echo_sent = true;
            self.log(LogLevel::Info, "sent the control echo probe (0x0910)");
        }
    }

    /// Where senkusha's datagrams go: its own socket on the LAN, the A/V leg on rendezvous.
    fn senkusha_target(&self) -> (Socket, Endpoint) {
        match self.cfg.route {
            Route::Local => (Socket::Senkusha, Endpoint::new(self.cfg.console, self.cfg.senkusha_port)),
            Route::Rendezvous => (Socket::MediaLeg, self.media_peer.unwrap_or_default()),
        }
    }

    /// A raw probe datagram, after whatever the association has queued: the echo-on or client-MTU command
    /// must reach the console before the packet it announces, as .NET's awaited send puts it on the wire first.
    fn send_raw_senkusha(&mut self, data: Vec<u8>) {
        self.flush();
        let (socket, to) = self.senkusha_target();
        self.io.push_back(Io::UdpSend { socket, to, data });
    }

    /// A probe command on senkusha's bandwidth channel.
    fn send_senkusha(&mut self, now: u64, message: &[u8]) {
        let _ = self.senkusha.as_mut().map(|s| s.send(now, CHANNEL_BANDWIDTH, message));
    }

    fn send_ping(&mut self, now: u64, seq: u8) {
        if let Some(ping) = crate::takion::senkusha::build(seq, now, crate::takion::senkusha::ECHO_PAYLOAD, 0)
        {
            self.send_raw_senkusha(ping);
        }
    }

    /// Whether a BANDWIDTH_PROBE reply carrying `command` arrived; other probe traffic is dropped, since
    /// every reply shares one message type.
    fn take_probe_reply(&mut self, command: u32) -> bool {
        while let Some(m) = self.senkusha_messages.pop_front() {
            if tc::peek_type(&m.payload) == Some(tc::BANDWIDTH_PROBE)
                && tc::parse_bandwidth_probe_command(&m.payload) == Some(command)
            {
                return true;
            }
        }
        false
    }

    /// The echo probe, as the capture orders it: after SESSION_REPLY, before the MTU test. Echo mode on,
    /// ten pings each timed, echo mode off.
    fn start_echo_probe(&mut self, now: u64) {
        self.echo_samples.clear();
        self.probe_echoes.clear();
        self.send_senkusha(now, &tc::build_echo_command(true));
        self.send_ping(now, 0);
        let budget_end = (now + ECHO_BUDGET_US).min(self.senkusha_box);
        self.step = Step::SenkushaEcho { seq: 0, sent_at: now, deadline: now + ECHO_REPLY_US, budget_end };
    }

    /// A majority answered: their least round trip replaces the handshake's. Then the MTU test, at the size
    /// the session would declare: it confirms or refutes one size, as the vendor does, never searches.
    fn finish_echo_probe(&mut self, now: u64) {
        self.send_senkusha(now, &tc::build_echo_command(false));
        let pings = usize::from(crate::takion::senkusha::PING_COUNT);
        if self.echo_samples.len() * 2 >= pings
            && let Some(&least) = self.echo_samples.iter().min()
        {
            self.measured_rtt_us = Some(least);
            self.outcome.echo_rtt_us = Some(least);
        }
        if now >= self.senkusha_box {
            self.after_senkusha(now);
            return;
        }
        let mtu = self.declared().0;
        self.mtu_probes_received = 0;
        self.send_senkusha(now, &tc::build_mtu_command(1, mtu, 1));
        let budget_end = (now + MTU_BUDGET_US).min(self.senkusha_box);
        self.step = Step::SenkushaMtuDown { mtu, deadline: now + MTU_REPLY_US, budget_end };
    }

    /// Non-fatal throughout, as in both references: a failed senkusha leaves the stream to be attempted.
    fn after_senkusha(&mut self, now: u64) {
        if let Some(s) = self.senkusha.as_mut() {
            if self.outcome.senkusha_ok {
                // .NET's RunAsync ends a completed probe politely.
                let _ = s.send(now, CHANNEL_SESSION, &tc::build_disconnect(b"Client Disconnecting"));
            }
            self.flush();
        }
        self.senkusha = None;
        self.senkusha_messages.clear();
        if self.opened.remove(&Socket::Senkusha).is_some() {
            self.io.push_back(Io::UdpClose(Socket::Senkusha));
        }
        if !self.outcome.senkusha_ok {
            self.log(LogLevel::Warn, "senkusha did not complete - continuing, as both references do");
        }
        if self.cfg.route == Route::Local {
            self.send_control_echo_probe();
        }
        if self.cfg.route == Route::Rendezvous {
            self.send_probe_report();
            self.step = Step::StreamReadyWait { deadline: now + STREAM_READY_WINDOW_US };
        } else {
            self.start_takion(now);
        }
    }

    /// What the launch spec and PROBE_REPORT declare (.NET's LinkMetrics): the MTU the probe confirmed, else
    /// the interface's less the overhead, clamped; and the echo probe's least round trip when a majority
    /// answered, else the least of senkusha's handshake round trips.
    pub(super) fn declared(&self) -> (u32, u32) {
        let mtu = self.confirmed_mtu.unwrap_or_else(|| self.interface_mtu_to_declare());
        // Rounded, not truncated: a sub-millisecond LAN is not "0", which claims nothing was measured.
        let rtt = self.measured_rtt_us.map_or(0, |us| ((us + 500) / 1000).min(1000) as u32);
        (mtu, rtt)
    }

    fn interface_mtu_to_declare(&self) -> u32 {
        self.cfg
            .interface_mtu
            .map_or(DECLARED_MTU, |m| m.saturating_sub(MTU_OVERHEAD).clamp(DECLARED_MTU_MIN, DECLARED_MTU))
    }

    /// PROBE_REPORT is required on the rendezvous route: without it the console never sends STREAM_READY
    /// and answers SESSION_REQUEST with no key. Which slot means what is [X].
    fn send_probe_report(&mut self) {
        let (mtu, rtt) = self.declared();
        let report = ctrl::probe_report_plaintext(self.cfg.bitrate_kbps as u32, mtu, f64::from(rtt));
        if let Some(s) = self.control.session() {
            s.send_field(ctrl::PROBE_REPORT, &report);
            self.outcome.probe_report_sent = true;
            self.log(LogLevel::Info, "probe report sent");
        }
    }

    // ---- The stream ----------------------------------------------------------------------------------

    fn start_takion(&mut self, now: u64) {
        self.announce(Stage::TakionUp);
        self.stream_box = now + STREAM_BOX_US;
        match self.cfg.route {
            Route::Local => {
                self.io.push_back(Io::UdpOpen {
                    socket: Socket::Stream,
                    local_port: 0,
                    rcvbuf: self.cfg.rcvbuf_bytes,
                });
                self.step = Step::TakionOpen;
            }
            Route::Rendezvous => self.takion_connect(now),
        }
    }

    fn takion_connect(&mut self, now: u64) {
        let config = connection::Config {
            max_attempts: self.cfg.stream_attempts,
            attempt_timeout_us: self.cfg.attempt_interval_us,
        };
        let mut c = Connection::new(self.random_u32(), config);
        c.connect(now);
        self.stream = Some(c);
        self.step = Step::TakionHandshake;
    }

    /// The version is negotiated on this channel first and the console's choice used, because the curve
    /// follows the version.
    fn start_keys(&mut self, now: u64) {
        self.announce(Stage::StreamKeys);
        let request = tc::build_protocol_version_request(&negotiator::OFFERED_VERSIONS).unwrap_or_default();
        let _ = self.stream.as_mut().map(|s| s.send(now, CHANNEL_PROTOCOL_VERSION, &request));
        self.step = Step::KeysVersion { deadline: (now + TAKION_REPLY_US).min(self.stream_box) };
    }

    fn send_session_request(&mut self, now: u64, version: u32) {
        let mut handshake_key = [0u8; 16];
        self.fill(&mut handshake_key);
        let (mtu, rtt) = self.declared();
        let params = launch_spec::Params {
            width: self.cfg.width,
            height: self.cfg.height,
            fps: self.cfg.fps,
            bitrate_kbps: self.cfg.bitrate_kbps,
            mtu: mtu as i32,
            rtt_ms: rtt as i32,
            hevc: self.cfg.allow_hevc,
            hdr: self.cfg.hdr,
        };
        // Encrypted under the control session's streaminfo cipher at counter 0: wire-confirmed, "do not
        // fix the counter".
        let mut spec = launch_spec::build(&params, &handshake_key).into_bytes();
        let Some(field) = self.control.session().map(|s| *s.field()) else {
            self.request_end(EndReason::ChannelError);
            return;
        };
        field.streaminfo_crypt(0, &mut spec);
        let spec_b64 = crate::base64::encode(&spec);
        let random = self.random.clone();
        let mut fill = move |b: &mut [u8]| (random.lock().unwrap_or_else(|p| p.into_inner()))(b);
        let begun = Negotiator::begin(
            version,
            &handshake_key,
            negotiator::DEFAULT_SESSION_KEY,
            spec_b64.as_bytes(),
            self.ecdh.as_ref(),
            &mut fill,
        );
        handshake_key.fill(0);
        spec.fill(0);
        let Some((neg, request)) = begun else {
            self.log(
                LogLevel::Error,
                "SESSION_REQUEST could not be built (no key agreement for this version?)",
            );
            self.request_end(EndReason::ChannelError);
            return;
        };
        self.outcome.curve = Some(neg.curve());
        self.negotiator = Some(neg);
        let _ = self.stream.as_mut().map(|s| s.send(now, CHANNEL_SESSION, &request));
        self.step = Step::KeysReply { deadline: self.stream_box };
    }

    /// Sealing on before anything else goes out, verification enforced from the start (C's b70), and
    /// input sendable only now.
    fn start_stream_info(&mut self, _now: u64) {
        self.announce(Stage::StreamReady);
        let Some(keys) = self.keys else { return };
        if let Some(s) = self.stream.as_mut() {
            s.enable_sealing(&keys.send.aes_key, &keys.send.base_iv);
            s.enable_verification(&keys.receive.aes_key, &keys.receive.base_iv, true);
        }
        self.step = Step::StreamInfoWait { deadline: self.stream_box };
    }

    fn on_stream_info(&mut self, now: u64, message: &[u8]) {
        let Some(keys) = self.keys else { return };
        let info = tc::parse_stream_info(message).filter(|i| i.has_resolution);
        let mut demux = StreamDemux::new(PacketCrypto::new(&keys.receive.aes_key, &keys.receive.base_iv));
        if let Some(i) = &info
            && !i.video_header.is_empty()
        {
            demux.set_video_header(i.video_header);
        }
        // Acked whether or not it parsed (the ack says "received"), on channel 9, before the host hears.
        let _ = self
            .stream
            .as_mut()
            .map(|s| s.send(now, CHANNEL_STREAM_INFO, &tc::build_bare(tc::STREAM_INFO_ACK)));
        match info {
            Some(i) => {
                self.outcome.stream_info_parsed = true;
                self.outcome.stream_width = i.width;
                self.outcome.stream_height = i.height;
                self.outcome.stream_is_hevc = demux.video_is_hevc();
                self.events.push_back(Event::StreamInfo {
                    width: i.width,
                    height: i.height,
                    hevc: demux.video_is_hevc(),
                    video_header: i.video_header.to_vec(),
                });
            }
            None => self.log(LogLevel::Warn, "STREAM_INFO did not parse - acked anyway"),
        }
        self.demux = Some(demux);
        let mut ladder = bandwidth::Controller::new(
            self.cfg.width,
            self.cfg.height,
            self.cfg.fps,
            self.cfg.bitrate_kbps,
            now,
        );
        ladder.set_power(self.power);
        self.bandwidth = Some(ladder);
        self.reach(Stage::StreamReady);
        // The IDR latch is armed at the start, because at the start we are blind by definition (C's b141).
        self.live = Some(Streaming {
            writer: input::Writer::new(),
            next_input: now,
            next_congestion: now + CONGESTION_US,
            next_heartbeat: now,
            window_start: now,
            window_bytes: 0,
            last_activity: now,
            last_video: now,
            idr_awaiting: true,
            idr_last: None,
            idr_requests: 0,
            packets_received: 0,
            packets_lost: 0,
            video_frames: 0,
            audio_frames: 0,
            keyframes: 0,
        });
        self.step = Step::Streaming;
    }

    /// The things owed to the console on a clock, and what it said on the stream's control channel.
    fn service_stream(&mut self, now: u64) {
        while let Some(m) = self.stream_messages.pop_front() {
            match tc::peek_type(&m.payload) {
                // The console re-sends STREAM_INFO if it did not hear the ack.
                Some(tc::STREAM_INFO) => {
                    let _ = self
                        .stream
                        .as_mut()
                        .map(|s| s.send(now, CHANNEL_STREAM_INFO, &tc::build_bare(tc::STREAM_INFO_ACK)));
                    self.outcome.stream_info_repeats += 1;
                }
                Some(tc::DISCONNECT) => {
                    self.console_disconnected(&m.payload);
                    return;
                }
                _ => {}
            }
        }
        let (socket, to) = self.stream_target();
        let (Some(live), Some(stream)) = (self.live.as_mut(), self.stream.as_mut()) else { return };

        if let Some(state) = self.controller
            && now >= live.next_input
        {
            live.next_input = now + INPUT_POLL_US;
            let packets = live.writer.step(&state, now);
            for (packet, count) in [
                (packets.history, &mut self.outcome.input_history_sent),
                (packets.state, &mut self.outcome.input_state_sent),
            ] {
                if let (Some(mut p), Some(sealer)) = (packet, stream.sealer_mut()) {
                    sealer.seal_input(&mut p, input::HEADER_LENGTH);
                    self.io.push_back(Io::UdpSend { socket, to, data: p });
                    *count += 1;
                }
            }
        }

        if now >= live.next_congestion {
            live.next_congestion = now + CONGESTION_US;
            let (got, missed) = self.demux.as_mut().map_or((0, 0), |d| d.take_packet_stats());
            live.packets_received += got;
            live.packets_lost += missed;
            let mut feedback = sealer::congestion_packet(got, missed);
            if let Some(sealer) = stream.sealer_mut() {
                sealer.seal_congestion(&mut feedback);
                self.io.push_back(Io::UdpSend { socket, to, data: feedback.to_vec() });
                self.outcome.congestion_sent += 1;
            }
            let rtt_ms = stream.round_trip().0 / 1000.0;
            let observed = got + missed;
            let loss_ratio = if observed == 0 { 0.0 } else { missed as f64 / observed as f64 };
            // The ladder decides from what arrived; the console hears about it only when the host opted in.
            let mut target = Rung {
                width: self.cfg.width,
                height: self.cfg.height,
                fps: self.cfg.fps,
                bitrate_kbps: self.cfg.bitrate_kbps,
            };
            if let Some(ladder) = self.bandwidth.as_mut() {
                ladder.report(now, bandwidth::Sample { rtt_ms, loss_ratio, observed_units: observed });
                target = ladder.current();
                if self.cfg.report_connection_quality
                    && let Some(r) = self.reporter.next(now, target.bitrate_kbps, rtt_ms, loss_ratio * 100.0)
                    && stream
                        .send(
                            now,
                            CHANNEL_SESSION,
                            &tc::build_connection_quality(r.target_bitrate_kbps, r.rtt_ms, r.loss_percent),
                        )
                        .is_ok()
                {
                    self.outcome.quality_reports_sent += 1;
                }
            }
            let window = now - live.window_start;
            let ms = |us: u64| (us / 1000).min(u64::from(u32::MAX)) as u32;
            self.events.push_back(Event::Stats(Stats {
                packets_received: live.packets_received,
                packets_lost: live.packets_lost,
                video_frames: live.video_frames,
                audio_frames: live.audio_frames,
                keyframes: live.keyframes,
                // Bits per microsecond is megabits a second; a thousand times that is kilobits.
                kbps: live
                    .window_bytes
                    .saturating_mul(8000)
                    .checked_div(window)
                    .unwrap_or(0)
                    .min(u64::from(u32::MAX)) as u32,
                ms_since_console_activity: ms(now - live.last_activity),
                ms_since_video_frame: ms(now - live.last_video),
                idr_requests: live.idr_requests,
                window_ms: ms(window),
                verify_dropped: stream.verify_dropped(),
                rtt_ms,
                target_bitrate_kbps: target.bitrate_kbps.max(0) as u32,
                target_height: target.height.max(0) as u32,
            }));
            live.window_start = now;
            live.window_bytes = 0;
        }

        // Still blind? Ask again: the request is a demand for the repair.
        if live.idr_awaiting && live.idr_last.is_none_or(|t| now - t >= IDR_MIN_US) {
            live.idr_last = Some(now);
            live.idr_requests += 1;
            let _ = stream.send(now, CHANNEL_SESSION, &tc::build_bare(tc::IDR_REQUEST));
        }

        if now >= live.next_heartbeat {
            live.next_heartbeat = now + HEARTBEAT_US;
            if stream.send(now, CHANNEL_SESSION, &tc::build_bare(tc::HEARTBEAT)).is_ok() {
                self.outcome.heartbeats_sent += 1;
            }
        }
    }

    // ---- Ending --------------------------------------------------------------------------------------

    /// Teardown, once: the goodbye (REST_MODE when asked, then the Takion DISCONNECT, both best-effort),
    /// the polite close of the rendezvous control connection before its socket goes, every socket closed,
    /// the keys dropped, ENDED announced.
    fn finish(&mut self, reason: EndReason, now_hint: u64) {
        self.outcome.end_reason = Some(reason);
        if self.rest_wanted
            && let Some(s) = self.control.session()
        {
            s.request_rest();
            self.outcome.rest_requested = true;
            self.log(LogLevel::Info, "asked the console to rest");
        }
        if let Some(s) = self.stream.as_mut()
            && s.state() == connection::State::Established
            && s.send(now_hint, CHANNEL_SESSION, &tc::build_disconnect(b"")).is_ok()
        {
            self.outcome.disconnect_sent = true;
        }
        if let Some(s) = &self.stream {
            self.outcome.verify_dropped = s.verify_dropped();
        }
        self.flush();
        if let Control::Dgram(p) = &mut self.control {
            p.close();
        }
        self.flush();
        if matches!(self.control, Control::Tcp { .. }) {
            self.tcp_close();
        }
        let mut sockets: Vec<Socket> = self.opened.keys().copied().collect();
        sockets.sort_by_key(|s| *s as u8);
        for socket in sockets {
            self.io.push_back(Io::UdpClose(socket));
        }
        self.opened.clear();
        self.control = Control::None;
        self.senkusha = None;
        self.stream = None;
        self.negotiator = None;
        self.keys = None;
        self.demux = None;
        self.live = None;
        self.channel = None;
        self.media_channel = None;
        self.exchange = None;
        self.cfg.pairing.registration_key.fill(0);
        self.cfg.pairing.companion = [0; 16];
        self.log(LogLevel::Info, format!("ended at {:?}: {reason:?}", self.reached));
        self.step = Step::Ended;
        self.announce(Stage::Ended);
        self.events.push_back(Event::Ended(reason));
    }
}
