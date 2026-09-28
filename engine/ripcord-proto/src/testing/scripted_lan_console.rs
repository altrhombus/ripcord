//! A console on the LAN, scripted end to end: the arm probe, /sess/init and /sess/ctrl over TCP, the
//! binary control channel (SESSION_ID, the sign-in gate), senkusha, and the stream's Takion through key
//! agreement, STREAM_INFO and sealed A/V. New in this engine: the C core's connect sequence was never
//! testable without a console, because it owned its sockets.
//!
//! It answers; it originates only through [`ScriptedLanConsole::video_keyframe`] and
//! [`ScriptedLanConsole::disconnect`]. The crypto is the real crypto, with the console's side of every
//! derivation computed from what the client sent, so a client that gets a key wrong fails here as it
//! would against hardware.

use crate::crypto::ecdh::{self, Ecdh, RustCryptoEcdh};
use crate::halyard::control::ControlField;
use crate::halyard::{VERSION_SELECTOR_PS4, VERSION_SELECTOR_PS5};
use crate::sess::{ctrl, fields};
use crate::stream::header::{self, StreamHeader};
use crate::stream::key_schedule::{self, DIRECTION_SERVER_TO_CLIENT};
use crate::stream::packet_crypto::PacketCrypto;
use crate::takion::chunks::{self, CHANNEL_SESSION, CHANNEL_STREAM_INFO, Header};
use crate::takion::control::{self as tc, SessionReply};
use crate::takion::negotiator::{self, curve_for_version};
use crate::takion::sealer::Sealer;

pub const NONCE: [u8; 16] = [0x5a; 16];
pub const COMPANION: [u8; 16] = [0x3c; 16];
/// The registration key the console hands out on /sess/rgst.
pub const REGISTRATION_KEY: [u8; 8] = [0xab; 8];
pub const WIDTH: u32 = 1280;
pub const HEIGHT: u32 = 720;
pub const SPS_PPS: [u8; 7] = [0x00, 0x00, 0x00, 0x01, 0x67, 0xaa, 0xbb];

/// What the TCP side does next.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Tcp {
    Data(Vec<u8>),
    Close,
}

/// A console's side of one Takion association.
#[derive(Default)]
struct TakionPeer {
    tag: u32,
    client_tag: u32,
    next_tsn: u32,
    sealer: Option<Sealer>,
    /// A message still arriving: continuations follow its first chunk.
    partial: Option<Vec<u8>>,
}

impl TakionPeer {
    fn new(tag: u32) -> Self {
        Self { tag, next_tsn: tag ^ 0x5555, ..Default::default() }
    }

    fn packet(&mut self, chunk: &[u8]) -> Vec<u8> {
        let mut p = Header::control(self.client_tag).build(chunk);
        if let Some(s) = self.sealer.as_mut() {
            s.seal_control(&mut p);
        }
        p
    }

    fn data(&mut self, channel: u16, payload: &[u8]) -> Vec<u8> {
        let tsn = self.next_tsn;
        self.next_tsn = self.next_tsn.wrapping_add(1);
        let chunk = chunks::build_data_first(tsn, channel, true, payload).unwrap();
        self.packet(&chunk)
    }

    /// The handshake and SACKs; a complete DATA message comes back for the caller to answer.
    fn on(&mut self, d: &[u8], out: &mut Vec<Vec<u8>>) -> Option<Vec<u8>> {
        let (_, chunk) = Header::parse(d)?;
        match chunks::chunk_type(chunk)? {
            chunks::CHUNK_INIT => {
                self.client_tag = chunks::parse_init(chunk)?;
                let ack = chunks::build_init_ack(self.tag, self.next_tsn, &[7; chunks::COOKIE_SIZE]);
                out.push(Header::control(self.client_tag).build(&ack));
                None
            }
            chunks::CHUNK_COOKIE_ECHO => {
                out.push(Header::control(self.client_tag).build(&chunks::build_cookie_ack()));
                None
            }
            chunks::CHUNK_DATA => {
                // First or continuation is the receiver's own state, never a guess from the bytes.
                let data = match self.partial {
                    Some(_) => chunks::parse_data_continuation(chunk)?,
                    None => chunks::parse_data_first(chunk)?,
                };
                let sack = chunks::build_sack(data.tsn, chunks::INIT_A_RWND);
                out.push(self.packet(&sack));
                let mut message = self.partial.take().unwrap_or_default();
                message.extend_from_slice(data.payload);
                if data.ending {
                    Some(message)
                } else {
                    self.partial = Some(message);
                    None
                }
            }
            _ => None,
        }
    }
}

pub struct ScriptedLanConsole {
    pub is_ps5: bool,
    /// Ask for a passcode after /sess/ctrl, and accept only this one.
    pub passcode: Option<String>,
    /// Send SESSION_ID (after the gate, if there is one).
    pub send_session_id: bool,
    /// The protocol version the stream agrees to.
    pub stream_version: u32,
    /// Refuse the stream's SESSION_REQUEST with a DISCONNECT carrying this reason.
    pub refuse_with: Option<&'static str>,
    /// Answer senkusha's echo and MTU probes: echo pings while in echo or client-MTU mode, and send the
    /// downstream MTU datagrams asked for.
    pub answer_probes: bool,
    echo_mode: bool,
    client_mtu_mode: bool,
    /// Every probe command, as (command, id, mtuReq, num-or-state).
    pub probe_commands: Vec<(u32, u32, u32, u32)>,
    pub pings_echoed: u32,
    /// The control echo probe decrypted to its four zero bytes.
    pub echo_probe_read: bool,
    /// The PIN /sess/rgst is answered under: the console's side of the registration derivation, so a
    /// client with another PIN decrypts noise, as on hardware.
    pub regist_pin: u32,
    /// Refuse /sess/rgst with this RP-Application-Reason instead.
    pub regist_refuse: Option<&'static str>,

    tcp_buf: Vec<u8>,
    field: Option<ControlField>,
    console_counter: u64,
    client_counter: u64,
    pub requests: Vec<String>,
    pub login_attempts: u32,
    pub rest_requested: bool,

    senkusha: TakionPeer,
    stream: TakionPeer,
    av: Option<(PacketCrypto, u32)>,
    frame: u16,
    pub arm_probes: u32,
    pub senkusha_messages: u32,
    pub stream_messages: Vec<u32>,
    pub input_packets: u32,
    pub congestion_packets: u32,
    pub stream_info_acks: u32,
    pub heartbeats: u32,
    pub idr_requests: u32,
    pub disconnect_received: bool,
    /// Senkusha said goodbye: on the rendezvous route the next association on the socket is the stream.
    pub senkusha_done: bool,
    pub handshake_key: Option<[u8; 16]>,
}

impl ScriptedLanConsole {
    pub fn new(is_ps5: bool) -> Self {
        Self {
            is_ps5,
            passcode: None,
            send_session_id: true,
            stream_version: negotiator::CLIENT_VERSION,
            refuse_with: None,
            answer_probes: true,
            echo_mode: false,
            client_mtu_mode: false,
            probe_commands: Vec::new(),
            pings_echoed: 0,
            echo_probe_read: false,
            regist_pin: 12_345_678,
            regist_refuse: None,
            tcp_buf: Vec::new(),
            field: None,
            console_counter: fields::COUNTER_CONSOLE_START,
            client_counter: fields::login_pin_counter(is_ps5),
            requests: Vec::new(),
            login_attempts: 0,
            rest_requested: false,
            senkusha: TakionPeer::new(0x5e7c_0001),
            stream: TakionPeer::new(0x57ea_0002),
            av: None,
            frame: 0,
            arm_probes: 0,
            senkusha_messages: 0,
            stream_messages: Vec::new(),
            input_packets: 0,
            congestion_packets: 0,
            stream_info_acks: 0,
            heartbeats: 0,
            idr_requests: 0,
            disconnect_received: false,
            senkusha_done: false,
            handshake_key: None,
        }
    }

    fn selector(&self) -> i32 {
        if self.is_ps5 { VERSION_SELECTOR_PS5 } else { VERSION_SELECTOR_PS4 }
    }

    /// A console-originated control frame, its payload encrypted at the console's next counter.
    fn frame(&mut self, kind: u16, plaintext: &[u8]) -> Vec<u8> {
        let mut payload = plaintext.to_vec();
        if !payload.is_empty()
            && let Some(f) = &self.field
        {
            f.encrypt(self.console_counter, &mut payload);
            self.console_counter += 1;
        }
        ctrl::build(kind, &payload)
    }

    /// The control-field crypto without the TCP side, for the rendezvous route, where /sess runs over
    /// the 9303 association and the rig answers it with the datagram console.
    pub fn use_nonce(&mut self) {
        self.field = ControlField::new(&NONCE, &COMPANION, 2, self.selector());
    }

    /// A new TCP connection: nothing buffered.
    pub fn tcp_open(&mut self) {
        self.tcp_buf.clear();
    }

    pub fn on_tcp(&mut self, data: &[u8]) -> Vec<Tcp> {
        self.tcp_buf.extend_from_slice(data);
        let mut out = Vec::new();
        if self.field.is_none() || !self.requests.iter().any(|r| r.contains("/sess/ctrl")) {
            let Some(end) = self.tcp_buf.windows(4).position(|w| w == b"\r\n\r\n") else { return out };
            if let Some(reply) = self.answer_regist(end) {
                out.push(Tcp::Data(reply));
                out.push(Tcp::Close);
                return out;
            }
            if self.tcp_buf.starts_with(b"POST") {
                return out; // a registration body still arriving
            }
            let text = String::from_utf8_lossy(&self.tcp_buf[..end]).into_owned();
            self.tcp_buf.drain(..end + 4);
            self.requests.push(text.clone());
            if text.contains("/sess/init") {
                let nonce = crate::base64::encode(&NONCE);
                out.push(Tcp::Data(
                    format!("HTTP/1.1 200 OK\r\nRP-Nonce: {nonce}\r\nContent-Length: 0\r\n\r\n").into_bytes(),
                ));
                out.push(Tcp::Close);
                self.field = ControlField::new(&NONCE, &COMPANION, 2, self.selector());
                return out;
            }
            let mut reply = b"HTTP/1.1 200 OK\r\nRP-Version: 1.0\r\nContent-Length: 0\r\n\r\n".to_vec();
            // The first frame rides in the same read as the response, as on hardware.
            if self.passcode.is_some() {
                reply.extend(self.frame(ctrl::LOGIN_PROMPT, &[]));
            } else if self.send_session_id {
                reply.extend(self.frame(ctrl::SESSION_ID, b"session-4321"));
            }
            out.push(Tcp::Data(reply));
            return out;
        }
        // The binary channel.
        while let Some((kind, payload, used)) = ctrl::parse(&self.tcp_buf).map(|(k, p, u)| (k, p.to_vec(), u))
        {
            self.tcp_buf.drain(..used);
            match kind {
                ctrl::LOGIN_SUBMIT => {
                    self.login_attempts += 1;
                    let counter = self.client_counter;
                    self.client_counter += 1;
                    let accepted = self.passcode.as_ref().is_some_and(|pin| {
                        ctrl::login_submit_payload(self.field.as_ref().unwrap(), counter, pin).as_deref()
                            == Some(&payload[..])
                    });
                    let verdict = if accepted { ctrl::LOGIN_ACCEPTED } else { ctrl::LOGIN_REJECTED };
                    let mut reply = self.frame(ctrl::LOGIN, &[verdict]);
                    if accepted && self.send_session_id {
                        reply.extend(self.frame(ctrl::SESSION_ID, b"session-4321"));
                    }
                    out.push(Tcp::Data(reply));
                }
                ctrl::REST_MODE => self.rest_requested = true,
                ctrl::ECHO_PROBE => {
                    // Read at the client's next counter, as a real console reads it; answered either way.
                    let mut probe = payload.clone();
                    if let Some(f) = &self.field {
                        f.decrypt(self.client_counter, &mut probe);
                    }
                    self.client_counter += 1;
                    self.echo_probe_read = probe == [0; 4];
                    out.push(Tcp::Data(self.frame(ctrl::ECHO_PROBE_ACK, &[0; 4])));
                }
                _ => {}
            }
        }
        out
    }

    /// /sess/rgst, once its whole body has arrived: the console's half of the PIN route. The material comes
    /// back out of the context, the field is derived under [`Self::regist_pin`], and a pairing record for
    /// this console's registration key and companion goes back under it.
    fn answer_regist(&mut self, header_end: usize) -> Option<Vec<u8>> {
        use crate::halyard::registration;
        let head = String::from_utf8_lossy(&self.tcp_buf[..header_end]).into_owned();
        if !head.contains("/sess/rgst") {
            return None;
        }
        let length: usize = head
            .lines()
            .find_map(|l| l.strip_prefix("Content-Length:"))
            .and_then(|v| v.trim().parse().ok())
            .unwrap_or(0);
        let body_start = header_end + 4;
        if self.tcp_buf.len() < body_start + length {
            return None;
        }
        let body = self.tcp_buf[body_start..body_start + length].to_vec();
        self.tcp_buf.clear();
        self.requests.push(head);
        if let Some(reason) = self.regist_refuse {
            return Some(
                format!(
                    "HTTP/1.1 403 Forbidden\r\nRP-Application-Reason: {reason}\r\nContent-Length: 0\r\n\r\n"
                )
                .into_bytes(),
            );
        }
        let context = &body[..registration::CONTEXT_LENGTH.min(body.len())];
        let wrapped = registration::gather(context)?;
        let material = registration::unwrap_material(self.is_ps5, &wrapped, context)?;
        let field = registration::field(self.is_ps5, context, self.regist_pin, &material)?;
        let family = if self.is_ps5 { "PS5" } else { "PS4" };
        let hex = |b: &[u8]| b.iter().map(|x| format!("{x:02x}")).collect::<String>();
        let mut record = format!(
            "AP-Ssid: scripted\r\n{family}-RegistKey: {}\r\nRP-KeyType: 2\r\nRP-Key: {}\r\n",
            hex(&REGISTRATION_KEY),
            hex(&COMPANION)
        )
        .into_bytes();
        field.encrypt(registration::FIELD_COUNTER, &mut record);
        let mut reply = format!("HTTP/1.1 200 OK\r\nContent-Length: {}\r\n\r\n", record.len()).into_bytes();
        reply.extend(record);
        Some(reply)
    }

    /// A datagram to one of the console's ports; what it sends back.
    pub fn on_udp(&mut self, port: u16, data: &[u8]) -> Vec<Vec<u8>> {
        let mut out = Vec::new();
        match port {
            9295 => {
                if data == crate::discovery::arm_probe(self.is_ps5) {
                    self.arm_probes += 1;
                    out.push(if self.is_ps5 { b"RES3".to_vec() } else { b"RES2".to_vec() });
                }
            }
            9297 => {
                if !crate::takion::connection::is_control(data) {
                    if crate::takion::senkusha::echo_sequence(data).is_some()
                        && self.answer_probes
                        && (self.echo_mode || self.client_mtu_mode)
                    {
                        self.pings_echoed += 1;
                        out.push(data.to_vec());
                    }
                    return out;
                }
                if let Some(message) = self.senkusha.on(data, &mut out) {
                    self.senkusha_messages += 1;
                    match tc::peek_type(&message) {
                        Some(tc::BANDWIDTH_PROBE) => {
                            let Some(probe @ (command, id, mtu, extra)) = tc::parse_bandwidth_probe(&message)
                            else {
                                return out;
                            };
                            self.probe_commands.push(probe);
                            if !self.answer_probes {
                                return out;
                            }
                            match command {
                                tc::PROBE_ECHO_COMMAND => self.echo_mode = extra == 1,
                                tc::PROBE_MTU_COMMAND => {
                                    let size = (mtu as usize)
                                        .saturating_sub(crate::takion::senkusha::IP_UDP_OVERHEAD);
                                    for _ in 0..extra {
                                        let mut d = vec![0x5au8; size.max(1)];
                                        d[0] = 0x02;
                                        out.push(d);
                                    }
                                    let reply = tc::build_mtu_command(id, mtu, extra);
                                    out.push(self.senkusha.data(chunks::CHANNEL_BANDWIDTH, &reply));
                                }
                                tc::PROBE_CLIENT_MTU_COMMAND => {
                                    self.client_mtu_mode = extra == 1;
                                    if extra == 1 {
                                        let reply = tc::build_client_mtu_command(id, mtu, true);
                                        out.push(self.senkusha.data(chunks::CHANNEL_BANDWIDTH, &reply));
                                    }
                                }
                                _ => {}
                            }
                        }
                        Some(tc::PROTOCOL_VERSION_REQUEST) => {
                            out.push(self.senkusha.data(CHANNEL_SESSION, &tc::build_protocol_version_ack(9)))
                        }
                        Some(tc::DISCONNECT) => self.senkusha_done = true,
                        Some(tc::SESSION_REQUEST) => {
                            let reply = SessionReply {
                                server_version: 9,
                                token: 1,
                                encrypted_key_accepted: true,
                                version_accepted: true,
                                session_key: b"",
                                server_version_string: None,
                                ecdh_public_key: None,
                                ecdh_signature: None,
                            }
                            .build();
                            out.push(self.senkusha.data(CHANNEL_SESSION, &reply));
                        }
                        _ => {}
                    }
                }
            }
            9296 => {
                if !crate::takion::connection::is_control(data) {
                    match data.first().map(|b| b & 0x0f) {
                        Some(5) => self.congestion_packets += 1,
                        Some(1) | Some(6) => self.input_packets += 1,
                        _ => {}
                    }
                    return out;
                }
                if let Some(message) = self.stream.on(data, &mut out) {
                    let kind = tc::peek_type(&message).unwrap_or(u32::MAX);
                    self.stream_messages.push(kind);
                    match kind {
                        tc::PROTOCOL_VERSION_REQUEST => {
                            let ack = tc::build_protocol_version_ack(self.stream_version);
                            out.push(self.stream.data(CHANNEL_SESSION, &ack));
                        }
                        tc::SESSION_REQUEST => out.extend(self.answer_session(&message)),
                        tc::STREAM_INFO_ACK => self.stream_info_acks += 1,
                        tc::HEARTBEAT => self.heartbeats += 1,
                        tc::IDR_REQUEST => self.idr_requests += 1,
                        tc::DISCONNECT => self.disconnect_received = true,
                        _ => {}
                    }
                }
            }
            _ => {}
        }
        out
    }

    /// The console's half of the key agreement: the handshake key from the decrypted launch spec, its own
    /// key pair on the version's curve, a signed SESSION_REPLY, then sealing on and STREAM_INFO.
    fn answer_session(&mut self, message: &[u8]) -> Vec<Vec<u8>> {
        if let Some(reason) = self.refuse_with {
            return vec![self.stream.data(CHANNEL_SESSION, &tc::build_disconnect(reason.as_bytes()))];
        }
        let request = tc::parse_session_request(message).expect("a SESSION_REQUEST");
        let mut spec = crate::base64::decode(request.launch_spec).expect("a base64 launch spec");
        self.field.as_ref().unwrap().streaminfo_crypt(0, &mut spec);
        let spec = String::from_utf8(spec).expect("the spec decrypts to JSON");
        let key_b64 = spec.split("\"handshakeKey\":\"").nth(1).and_then(|r| r.split('"').next()).unwrap();
        let handshake_key: [u8; 16] = crate::base64::decode(key_b64.as_bytes()).unwrap().try_into().unwrap();
        self.handshake_key = Some(handshake_key);

        let curve = curve_for_version(self.stream_version).unwrap();
        let mut n = 0x11u8;
        let mut fill = |b: &mut [u8]| {
            b.iter_mut().for_each(|x| {
                *x = n;
                n = n.wrapping_mul(29).wrapping_add(7);
            })
        };
        let (private, public) = RustCryptoEcdh.generate(curve, &mut fill).unwrap();
        let signature = ecdh::public_key_signature(&handshake_key, &public);
        let reply = SessionReply {
            server_version: self.stream_version,
            token: 1,
            encrypted_key_accepted: true,
            version_accepted: true,
            session_key: b"",
            server_version_string: None,
            ecdh_public_key: Some(&public),
            ecdh_signature: Some(&signature),
        }
        .build();
        let mut out = vec![self.stream.data(CHANNEL_SESSION, &reply)];

        let shared = RustCryptoEcdh.shared_secret(curve, &private, request.ecdh_public_key.unwrap()).unwrap();
        let keys = key_schedule::derive_direction(&shared, &handshake_key, DIRECTION_SERVER_TO_CLIENT);
        self.stream.sealer = Some(Sealer::new(&keys.aes_key, &keys.base_iv));
        self.av = Some((PacketCrypto::new(&keys.aes_key, &keys.base_iv), 0));
        let info = tc::build_stream_info(WIDTH, HEIGHT, &SPS_PPS, &[]);
        out.push(self.stream.data(CHANNEL_STREAM_INFO, &info));
        out
    }

    /// One sealed single-unit IDR frame on the stream port. A frame is delivered when the next begins.
    pub fn video_keyframe(&mut self) -> Option<Vec<u8>> {
        let (crypto, key_pos) = self.av.as_mut()?;
        let h = StreamHeader {
            packet_type: header::TYPE_VIDEO,
            packet_index: self.frame,
            frame_index: self.frame,
            unit_index: 0,
            total_units: 1,
            key_position: *key_pos,
            ..Default::default()
        };
        let mut p = h.build()?.to_vec();
        p.extend_from_slice(&[0, 0, 0]);
        p.extend_from_slice(&[0, 0]);
        p.extend_from_slice(&[0x00, 0x00, 0x00, 0x01, 0x65, 0xde, 0xad]);
        let offset = h.payload_offset();
        crypto.crypt_payload(u64::from(*key_pos), &mut p[offset..]);
        crypto.seal(u64::from(*key_pos), &mut p, header::TAG_OFFSET, false);
        *key_pos += p.len().next_multiple_of(16) as u32;
        self.frame = self.frame.wrapping_add(1);
        Some(p)
    }

    /// Skips frame indices, as a lost burst would, so the next keyframe opens a gap the demuxer reports.
    pub fn skip_frames(&mut self, n: u16) {
        self.frame = self.frame.wrapping_add(n);
    }

    /// The console hanging up on the stream, with a reason.
    pub fn disconnect(&mut self, reason: &str) -> Vec<u8> {
        self.stream.data(CHANNEL_SESSION, &tc::build_disconnect(reason.as_bytes()))
    }
}
