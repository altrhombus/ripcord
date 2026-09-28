//! The control-plane protobuf envelope (`ControlMessage`) and the payloads the engine builds or reads.
//! Ported from `libripcord/takion/takion_control_proto.c`. Field numbers come from the committed
//! `docs/protocol/stream_control.proto` and `bandwidth_probe.proto`, which are the authority; the .NET side
//! compiles those with Google.Protobuf, and `control-proto.kat` compares this codec with that output.
//!
//! Hand-rolled like the C one: a strict subset. Wire types 0, 1, 2 and 5 are understood and unknown fields
//! skipped; groups are rejected. proto2 `required` is enforced both ways. Parsed strings and bytes borrow
//! from the input. The builders keep the C codec's size limits so the two refuse the same inputs.

pub const SESSION_REQUEST: u32 = 0;
pub const SESSION_REPLY: u32 = 1;
pub const HEARTBEAT: u32 = 3;
pub const CORRUPT_FRAME: u32 = 5;
pub const DISCONNECT: u32 = 8;
pub const BANDWIDTH_PROBE: u32 = 12;
pub const STREAM_INFO: u32 = 13;
pub const STREAM_INFO_ACK: u32 = 14;
pub const CONNECTION_QUALITY: u32 = 16;
pub const IDR_REQUEST: u32 = 25;
pub const PROTOCOL_VERSION_REQUEST: u32 = 31;
pub const PROTOCOL_VERSION_ACK: u32 = 32;

pub const ECDH_SIGNATURE_LENGTH: usize = 32;

const F_MSG_TYPE: u32 = 1;
const F_MSG_SESSION_REQUEST: u32 = 2;
const F_MSG_SESSION_REPLY: u32 = 3;
const F_MSG_CORRUPT_FRAME: u32 = 6;
const F_MSG_DISCONNECT: u32 = 10;
const F_MSG_BANDWIDTH_PROBE: u32 = 14;
const F_MSG_STREAM_INFO: u32 = 15;
const F_MSG_CONNECTION_QUALITY: u32 = 17;
const F_MSG_PROTOCOL_VERSION_REQUEST: u32 = 31;
const F_MSG_PROTOCOL_VERSION_ACK: u32 = 32;

const WT_VARINT: u8 = 0;
const WT_64BIT: u8 = 1;
const WT_LEN: u8 = 2;
const WT_32BIT: u8 = 5;

// ---- the wire subset ----

fn put_varint(out: &mut Vec<u8>, mut value: u64) {
    while value >= 0x80 {
        out.push((value as u8 & 0x7F) | 0x80);
        value >>= 7;
    }
    out.push(value as u8);
}

fn put_tag(out: &mut Vec<u8>, field: u32, wire: u8) {
    put_varint(out, (u64::from(field) << 3) | u64::from(wire));
}

fn put_varint_field(out: &mut Vec<u8>, field: u32, value: u64) {
    put_tag(out, field, WT_VARINT);
    put_varint(out, value);
}

fn put_len_field(out: &mut Vec<u8>, field: u32, bytes: &[u8]) {
    put_tag(out, field, WT_LEN);
    put_varint(out, bytes.len() as u64);
    out.extend_from_slice(bytes);
}

/// A cursor over a message. Every read fails rather than running past the end.
struct Reader<'a> {
    data: &'a [u8],
    pos: usize,
}

impl<'a> Reader<'a> {
    fn new(data: &'a [u8]) -> Self {
        Self { data, pos: 0 }
    }

    fn done(&self) -> bool {
        self.pos >= self.data.len()
    }

    fn varint(&mut self) -> Option<u64> {
        let mut value = 0u64;
        let mut shift = 0u32;
        while let Some(&byte) = self.data.get(self.pos) {
            self.pos += 1;
            // The tenth byte may carry only the top bit of a u64.
            if shift == 63 && byte & 0x7F > 1 {
                return None;
            }
            value |= u64::from(byte & 0x7F) << shift;
            if byte & 0x80 == 0 {
                return Some(value);
            }
            shift += 7;
            if shift > 63 {
                return None;
            }
        }
        None
    }

    /// The next (field, wire type), refusing field 0.
    fn tag(&mut self) -> Option<(u32, u8)> {
        let tag = self.varint()?;
        let field = (tag >> 3) as u32;
        (field != 0).then_some((field, (tag & 7) as u8))
    }

    fn bytes(&mut self) -> Option<&'a [u8]> {
        let len = self.varint()?;
        let remaining = (self.data.len() - self.pos) as u64;
        if len > remaining {
            return None;
        }
        let start = self.pos;
        self.pos += len as usize;
        Some(&self.data[start..self.pos])
    }

    fn skip(&mut self, wire: u8) -> Option<()> {
        match wire {
            WT_VARINT => self.varint().map(drop),
            WT_64BIT | WT_32BIT => {
                let n = if wire == WT_64BIT { 8 } else { 4 };
                if self.data.len() - self.pos < n {
                    return None;
                }
                self.pos += n;
                Some(())
            }
            WT_LEN => self.bytes().map(drop),
            _ => None, // groups and the unused wire types
        }
    }
}

/// Walks every field of a message, true if the whole thing is well-formed protobuf. Checks hand-built
/// literals: a two-byte tag written as one byte leaves the first field readable and the rest garbage.
pub fn validate(data: &[u8]) -> bool {
    let mut r = Reader::new(data);
    while !r.done() {
        let Some((_, wire)) = r.tag() else { return false };
        if r.skip(wire).is_none() {
            return false;
        }
    }
    true
}

/// The envelope's `type`, so a receiver can dispatch before committing to a payload shape.
pub fn peek_type(data: &[u8]) -> Option<u32> {
    let mut r = Reader::new(data);
    while !r.done() {
        let (field, wire) = r.tag()?;
        if field == F_MSG_TYPE && wire == WT_VARINT {
            return Some(r.varint()? as u32);
        }
        r.skip(wire)?;
    }
    None
}

/// The payload of envelope field `payload_field`, requiring `type` to be `want_type`. The last occurrence
/// of the payload wins, as in the C codec.
fn envelope(data: &[u8], want_type: u32, payload_field: u32) -> Option<&[u8]> {
    let mut r = Reader::new(data);
    let (mut have_type, mut payload) = (false, None);
    while !r.done() {
        let (field, wire) = r.tag()?;
        if field == F_MSG_TYPE && wire == WT_VARINT {
            if r.varint()? != u64::from(want_type) {
                return None;
            }
            have_type = true;
        } else if field == payload_field && wire == WT_LEN {
            payload = Some(r.bytes()?);
        } else {
            r.skip(wire)?;
        }
    }
    payload.filter(|_| have_type)
}

// ---- SESSION_REQUEST ----

/// SESSION_REQUEST's payload. `launch_spec` is the already-encrypted, already-base64 launch spec, not raw
/// JSON, whatever its wire name suggests. The ECDH fields are proto2-optional; a real request has both.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SessionRequest<'a> {
    pub client_version: u32,
    pub session_key: &'a [u8],
    pub launch_spec: &'a [u8],
    pub encrypted_key: &'a [u8],
    pub ecdh_public_key: Option<&'a [u8]>,
    pub ecdh_signature: Option<&'a [u8]>,
}

impl SessionRequest<'_> {
    /// `ControlMessage{type = SESSION_REQUEST, sessionRequestPayload}`, fields in number order, as
    /// Google.Protobuf writes them.
    pub fn build(&self) -> Vec<u8> {
        let mut p = Vec::new();
        put_varint_field(&mut p, 1, u64::from(self.client_version));
        put_len_field(&mut p, 2, self.session_key);
        put_len_field(&mut p, 3, self.launch_spec);
        put_len_field(&mut p, 4, self.encrypted_key);
        if let Some(k) = self.ecdh_public_key {
            put_len_field(&mut p, 5, k);
        }
        if let Some(s) = self.ecdh_signature {
            put_len_field(&mut p, 6, s);
        }
        let mut out = Vec::with_capacity(p.len() + 8);
        put_varint_field(&mut out, F_MSG_TYPE, u64::from(SESSION_REQUEST));
        put_len_field(&mut out, F_MSG_SESSION_REQUEST, &p);
        out
    }
}

/// Reads a SESSION_REQUEST: the console's side, for scripted peers. The four required fields must be there.
pub fn parse_session_request(data: &[u8]) -> Option<SessionRequest<'_>> {
    let mut r = Reader::new(envelope(data, SESSION_REQUEST, F_MSG_SESSION_REQUEST)?);
    let (mut version, mut session_key, mut launch_spec, mut encrypted_key) = (None, None, None, None);
    let (mut public_key, mut signature) = (None, None);
    while !r.done() {
        match r.tag()? {
            (1, WT_VARINT) => version = Some(r.varint()? as u32),
            (2, WT_LEN) => session_key = Some(r.bytes()?),
            (3, WT_LEN) => launch_spec = Some(r.bytes()?),
            (4, WT_LEN) => encrypted_key = Some(r.bytes()?),
            (5, WT_LEN) => public_key = Some(r.bytes()?),
            (6, WT_LEN) => signature = Some(r.bytes()?),
            (1..=6, _) => return None,
            (_, wire) => r.skip(wire)?,
        }
    }
    Some(SessionRequest {
        client_version: version?,
        session_key: session_key?,
        launch_spec: launch_spec?,
        encrypted_key: encrypted_key?,
        ecdh_public_key: public_key,
        ecdh_signature: signature,
    })
}

// ---- SESSION_REPLY ----

/// SESSION_REPLY, parsed. `has_ecdh` separates "accepted but sent no key" from a key of zero bytes.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct SessionReply<'a> {
    pub server_version: u32,
    pub token: u32,
    pub encrypted_key_accepted: bool,
    pub version_accepted: bool,
    pub session_key: &'a [u8],
    pub server_version_string: Option<&'a [u8]>,
    pub ecdh_public_key: Option<&'a [u8]>,
    pub ecdh_signature: Option<&'a [u8]>,
}

impl SessionReply<'_> {
    pub fn has_ecdh(&self) -> bool {
        self.ecdh_public_key.is_some() && self.ecdh_signature.is_some()
    }

    /// The console's side, for scripted peers.
    pub fn build(&self) -> Vec<u8> {
        let mut p = Vec::new();
        put_varint_field(&mut p, 1, u64::from(self.server_version));
        put_varint_field(&mut p, 2, u64::from(self.token));
        put_varint_field(&mut p, 3, u64::from(self.encrypted_key_accepted));
        put_varint_field(&mut p, 4, u64::from(self.version_accepted));
        put_len_field(&mut p, 5, self.session_key);
        if let Some(s) = self.server_version_string {
            put_len_field(&mut p, 7, s);
        }
        if let Some(k) = self.ecdh_public_key {
            put_len_field(&mut p, 8, k);
        }
        if let Some(s) = self.ecdh_signature {
            put_len_field(&mut p, 9, s);
        }
        let mut out = Vec::with_capacity(p.len() + 8);
        put_varint_field(&mut out, F_MSG_TYPE, u64::from(SESSION_REPLY));
        put_len_field(&mut out, F_MSG_SESSION_REPLY, &p);
        out
    }
}

/// `None` if malformed, not a SESSION_REPLY, without a payload, or missing one of the five required fields
/// (server version, token, both acceptance flags, session key). A field with the wrong wire type fails.
pub fn parse_session_reply(data: &[u8]) -> Option<SessionReply<'_>> {
    let mut r = Reader::new(envelope(data, SESSION_REPLY, F_MSG_SESSION_REPLY)?);
    let mut reply = SessionReply::default();
    let mut seen = 0u8;
    while !r.done() {
        let (field, wire) = r.tag()?;
        let want = |w: u8| (wire == w).then_some(());
        match field {
            1 => {
                want(WT_VARINT)?;
                reply.server_version = r.varint()? as u32;
                seen |= 1;
            }
            2 => {
                want(WT_VARINT)?;
                reply.token = r.varint()? as u32;
                seen |= 2;
            }
            3 => {
                want(WT_VARINT)?;
                reply.encrypted_key_accepted = r.varint()? != 0;
                seen |= 4;
            }
            4 => {
                want(WT_VARINT)?;
                reply.version_accepted = r.varint()? != 0;
                seen |= 8;
            }
            5 => {
                want(WT_LEN)?;
                reply.session_key = r.bytes()?;
                seen |= 16;
            }
            7 => {
                want(WT_LEN)?;
                reply.server_version_string = Some(r.bytes()?);
            }
            8 => {
                want(WT_LEN)?;
                reply.ecdh_public_key = Some(r.bytes()?);
            }
            9 => {
                want(WT_LEN)?;
                reply.ecdh_signature = Some(r.bytes()?);
            }
            _ => r.skip(wire)?,
        }
    }
    (seen == 0x1F).then_some(reply)
}

// ---- STREAM_INFO, DISCONNECT, protocol version ----

/// STREAM_INFO, parsed. `video_header` is the parameter-set blob from the first resolution entry, which
/// the video stream itself does not carry.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct StreamInfo<'a> {
    pub width: u32,
    pub height: u32,
    pub video_header: &'a [u8],
    pub audio_header: &'a [u8],
    pub has_resolution: bool,
}

pub fn parse_stream_info(data: &[u8]) -> Option<StreamInfo<'_>> {
    let mut r = Reader::new(envelope(data, STREAM_INFO, F_MSG_STREAM_INFO)?);
    let mut info = StreamInfo::default();
    while !r.done() {
        match r.tag()? {
            (1, WT_LEN) => {
                let nested = r.bytes()?;
                if !info.has_resolution {
                    let mut n = Reader::new(nested);
                    while !n.done() {
                        match n.tag()? {
                            (1, WT_VARINT) => info.width = n.varint()? as u32,
                            (2, WT_VARINT) => info.height = n.varint()? as u32,
                            (3, WT_LEN) => info.video_header = n.bytes()?,
                            (_, wire) => n.skip(wire)?,
                        }
                    }
                    info.has_resolution = true;
                }
            }
            (2, WT_LEN) => info.audio_header = r.bytes()?,
            (_, wire) => r.skip(wire)?,
        }
    }
    Some(info)
}

/// DISCONNECT's reason, when the console says why it hung up.
pub fn parse_disconnect(data: &[u8]) -> Option<&[u8]> {
    // DISCONNECT carries no type check here, as in the C codec: any envelope with a disconnect payload.
    let mut r = Reader::new(data);
    let mut payload = None;
    while !r.done() {
        match r.tag()? {
            (F_MSG_DISCONNECT, WT_LEN) => payload = Some(r.bytes()?),
            (_, wire) => r.skip(wire)?,
        }
    }
    let mut p = Reader::new(payload?);
    while !p.done() {
        match p.tag()? {
            (1, WT_LEN) => return p.bytes(),
            (_, wire) => p.skip(wire)?,
        }
    }
    None
}

/// A bare message carrying only `type`: HEARTBEAT and the acknowledgements.
pub fn build_bare(kind: u32) -> Vec<u8> {
    let mut out = Vec::new();
    put_varint_field(&mut out, F_MSG_TYPE, u64::from(kind));
    out
}

/// The graceful goodbye. The reason is a required string, so an empty one is still a present field.
pub fn build_disconnect(reason: &[u8]) -> Vec<u8> {
    let mut p = Vec::new();
    put_len_field(&mut p, 1, reason);
    let mut out = Vec::new();
    put_varint_field(&mut out, F_MSG_TYPE, u64::from(DISCONNECT));
    put_len_field(&mut out, F_MSG_DISCONNECT, &p);
    out
}

/// PROTOCOL_VERSION_REQUEST with the versions we will speak, highest last. `None` for none, or more than
/// fit the C codec's 64-byte inner buffer. The session's curve follows the negotiated version, so ask
/// and then read the answer.
pub fn build_protocol_version_request(versions: &[u32]) -> Option<Vec<u8>> {
    let mut inner = Vec::new();
    for &v in versions {
        put_varint_field(&mut inner, 1, u64::from(v));
    }
    if versions.is_empty() || inner.len() > 64 {
        return None;
    }
    let mut out = Vec::new();
    put_varint_field(&mut out, F_MSG_TYPE, u64::from(PROTOCOL_VERSION_REQUEST));
    put_len_field(&mut out, F_MSG_PROTOCOL_VERSION_REQUEST, &inner);
    Some(out)
}

/// The version the console chose. `None` if absent (the caller falls back to what it asked for) or if the
/// message is malformed. The last occurrence wins.
pub fn parse_protocol_version_ack(data: &[u8]) -> Option<u32> {
    let mut r = Reader::new(data);
    let mut found = None;
    while !r.done() {
        match r.tag()? {
            (F_MSG_PROTOCOL_VERSION_ACK, WT_LEN) => {
                let mut n = Reader::new(r.bytes()?);
                while !n.done() {
                    match n.tag()? {
                        (1, WT_VARINT) => found = Some(n.varint()? as u32),
                        (_, wire) => n.skip(wire)?,
                    }
                }
            }
            (_, wire) => r.skip(wire)?,
        }
    }
    found
}

/// The console's PROTOCOL_VERSION_ACK. Only the scripted console sends one.
pub fn build_protocol_version_ack(version: u32) -> Vec<u8> {
    let mut inner = Vec::new();
    put_varint_field(&mut inner, 1, u64::from(version));
    let mut out = Vec::new();
    put_varint_field(&mut out, F_MSG_TYPE, u64::from(PROTOCOL_VERSION_ACK));
    put_len_field(&mut out, F_MSG_PROTOCOL_VERSION_ACK, &inner);
    out
}

/// The console's STREAM_INFO with one resolution entry, as [`parse_stream_info`] reads it. Only the
/// scripted console sends one.
pub fn build_stream_info(width: u32, height: u32, video_header: &[u8], audio_header: &[u8]) -> Vec<u8> {
    let mut resolution = Vec::new();
    put_varint_field(&mut resolution, 1, u64::from(width));
    put_varint_field(&mut resolution, 2, u64::from(height));
    put_len_field(&mut resolution, 3, video_header);
    let mut p = Vec::new();
    put_len_field(&mut p, 1, &resolution);
    put_len_field(&mut p, 2, audio_header);
    let mut out = Vec::new();
    put_varint_field(&mut out, F_MSG_TYPE, u64::from(STREAM_INFO));
    put_len_field(&mut out, F_MSG_STREAM_INFO, &p);
    out
}

/// CORRUPT_FRAME: the inclusive range of video frame indices lost, which the console reads as "these did not
/// arrive whole". .NET sends it on every loss the demuxer reports, then asks for a keyframe.
pub fn build_corrupt_frame(start: u32, end: u32) -> Vec<u8> {
    let mut p = Vec::new();
    put_varint_field(&mut p, 1, u64::from(start));
    put_varint_field(&mut p, 2, u64::from(end));
    let mut out = Vec::new();
    put_varint_field(&mut out, F_MSG_TYPE, u64::from(CORRUPT_FRAME));
    put_len_field(&mut out, F_MSG_CORRUPT_FRAME, &p);
    out
}

// ---- bandwidth probe and connection quality (channel 0x0008) ----

/// Bandwidth probe commands (BandwidthProbePayload.Command).
pub const PROBE_ECHO_COMMAND: u32 = 0;
pub const PROBE_MTU_COMMAND: u32 = 1;
pub const PROBE_CLIENT_MTU_COMMAND: u32 = 4;

/// The console's side of a probe command: (command, id, mtuReq, num-or-state), for the scripted console.
/// `num` for MTU_COMMAND, `state` (0 or 1) for CLIENT_MTU_COMMAND and ECHO_COMMAND.
pub fn parse_bandwidth_probe(data: &[u8]) -> Option<(u32, u32, u32, u32)> {
    let mut r = Reader::new(envelope(data, BANDWIDTH_PROBE, F_MSG_BANDWIDTH_PROBE)?);
    let (mut command, mut inner) = (None, None);
    while !r.done() {
        match r.tag()? {
            (1, WT_VARINT) => command = Some(r.varint()? as u32),
            (2..=5, WT_LEN) => inner = Some(r.bytes()?),
            (_, wire) => r.skip(wire)?,
        }
    }
    let command = command?;
    let (mut id, mut mtu, mut extra) = (0, 0, 0);
    let mut n = Reader::new(inner.unwrap_or_default());
    while !n.done() {
        let (field, wire) = n.tag()?;
        let value = if wire == WT_VARINT { n.varint()? as u32 } else { n.skip(wire).map(|_| 0)? };
        match (command, field) {
            (PROBE_ECHO_COMMAND, 1) => extra = value,
            (_, 1) => id = value,
            (_, 2) => mtu = value,
            (PROBE_MTU_COMMAND, 4) | (PROBE_CLIENT_MTU_COMMAND, 3) => extra = value,
            _ => {}
        }
    }
    Some((command, id, mtu, extra))
}

/// The command a BANDWIDTH_PROBE reply carries, which is how the console's replies to the downstream and
/// upstream MTU tests are told apart. `None` for anything that is not a well-formed probe.
pub fn parse_bandwidth_probe_command(data: &[u8]) -> Option<u32> {
    let mut r = Reader::new(envelope(data, BANDWIDTH_PROBE, F_MSG_BANDWIDTH_PROBE)?);
    let mut command = None;
    while !r.done() {
        match r.tag()? {
            (1, WT_VARINT) => command = Some(r.varint()? as u32),
            (_, wire) => r.skip(wire)?,
        }
    }
    command
}

fn wrap_bandwidth_probe(command: u32, field: u32, inner: &[u8]) -> Vec<u8> {
    let mut p = Vec::new();
    // `command` is required, so emitted even when it is zero (ECHO_COMMAND).
    put_varint_field(&mut p, 1, u64::from(command));
    put_len_field(&mut p, field, inner);
    let mut out = Vec::new();
    put_varint_field(&mut out, F_MSG_TYPE, u64::from(BANDWIDTH_PROBE));
    put_len_field(&mut out, F_MSG_BANDWIDTH_PROBE, &p);
    out
}

/// Echo mode on or off: BandwidthProbePayload{command = ECHO_COMMAND, echoCommand{state}}.
pub fn build_echo_command(enabled: bool) -> Vec<u8> {
    let mut inner = Vec::new();
    put_varint_field(&mut inner, 1, u64::from(enabled));
    wrap_bandwidth_probe(0, 2, &inner)
}

/// Downstream MTU: ask the console for `num` datagrams of `mtu_req` bytes. Their arrival is the result.
pub fn build_mtu_command(id: u32, mtu_req: u32, num: u32) -> Vec<u8> {
    let mut inner = Vec::new();
    put_varint_field(&mut inner, 1, u64::from(id));
    put_varint_field(&mut inner, 2, u64::from(mtu_req));
    put_varint_field(&mut inner, 4, u64::from(num));
    wrap_bandwidth_probe(1, 3, &inner)
}

/// Upstream MTU: echo mode for one large packet. `state = false` closes the test and must be sent on
/// every exit path. mtuDown repeats mtuReq, as .NET sends it.
pub fn build_client_mtu_command(id: u32, mtu_req: u32, state: bool) -> Vec<u8> {
    let mut inner = Vec::new();
    put_varint_field(&mut inner, 1, u64::from(id));
    put_varint_field(&mut inner, 2, u64::from(mtu_req));
    put_varint_field(&mut inner, 3, u64::from(state));
    put_varint_field(&mut inner, 4, u64::from(mtu_req));
    wrap_bandwidth_probe(4, 5, &inner)
}

/// CONNECTION_QUALITY: targetBitrate (1, varint), rtt (5, double) and lossPercent (7, double), inside
/// field 17. [X] targetBitrate's unit is unconfirmed; kbps matches the launch spec, and bps is as likely.
pub fn build_connection_quality(target_bitrate_kbps: u32, rtt_ms: f64, loss_percent: f64) -> Vec<u8> {
    let mut p = Vec::new();
    put_varint_field(&mut p, 1, u64::from(target_bitrate_kbps));
    put_tag(&mut p, 5, WT_64BIT);
    p.extend_from_slice(&rtt_ms.to_le_bytes());
    put_tag(&mut p, 7, WT_64BIT);
    p.extend_from_slice(&loss_percent.to_le_bytes());
    let mut out = Vec::new();
    put_varint_field(&mut out, F_MSG_TYPE, u64::from(CONNECTION_QUALITY));
    put_len_field(&mut out, F_MSG_CONNECTION_QUALITY, &p);
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn corrupt_frame_and_probe_replies() {
        let c = build_corrupt_frame(10, 12);
        assert!(validate(&c));
        assert_eq!(peek_type(&c), Some(CORRUPT_FRAME));
        assert_eq!(c, [0x08, 0x05, 0x32, 0x04, 0x08, 0x0a, 0x10, 0x0c]);
        assert_eq!(parse_bandwidth_probe_command(&build_mtu_command(1, 1454, 1)), Some(PROBE_MTU_COMMAND));
        assert_eq!(
            parse_bandwidth_probe_command(&build_client_mtu_command(2, 1454, false)),
            Some(PROBE_CLIENT_MTU_COMMAND)
        );
        assert_eq!(parse_bandwidth_probe_command(&build_echo_command(true)), Some(PROBE_ECHO_COMMAND));
        assert_eq!(parse_bandwidth_probe_command(&build_bare(HEARTBEAT)), None);
        assert_eq!(parse_bandwidth_probe(&build_mtu_command(1, 1454, 1)), Some((1, 1, 1454, 1)));
        assert_eq!(parse_bandwidth_probe(&build_client_mtu_command(2, 1254, false)), Some((4, 2, 1254, 0)));
        assert_eq!(parse_bandwidth_probe(&build_echo_command(true)), Some((0, 0, 0, 1)));
    }

    #[test]
    fn request_and_reply_round_trip() {
        let req = SessionRequest {
            client_version: 17,
            session_key: b"InvalidSessionId",
            launch_spec: b"c3BlYw==",
            encrypted_key: &[0; 4],
            ecdh_public_key: Some(&[4; 133]),
            ecdh_signature: Some(&[9; 32]),
        };
        let wire = req.build();
        assert!(validate(&wire));
        assert_eq!(peek_type(&wire), Some(SESSION_REQUEST));
        assert_eq!(parse_session_request(&wire), Some(req));

        let reply = SessionReply {
            server_version: 17,
            token: 42,
            encrypted_key_accepted: true,
            version_accepted: true,
            session_key: b"k",
            server_version_string: None,
            ecdh_public_key: Some(&[4; 133]),
            ecdh_signature: Some(&[1; 32]),
        };
        let wire = reply.build();
        assert_eq!(parse_session_reply(&wire), Some(reply));
        for n in 1..wire.len() {
            assert_eq!(parse_session_reply(&wire[..n]), None, "a {n}-byte prefix must not parse");
        }
    }

    /// DATA1 and DATA3's payloads from the captured packets: a PROTOCOL_VERSION_REQUEST and an echo command.
    #[test]
    fn builders_match_captured_payloads() {
        assert_eq!(build_protocol_version_request(&[9]).unwrap(), [0x08, 0x1f, 0xfa, 0x01, 0x02, 0x08, 0x09]);
        assert_eq!(build_echo_command(true), [0x08, 0x0c, 0x72, 0x06, 0x08, 0x00, 0x12, 0x02, 0x08, 0x01]);
        assert_eq!(parse_protocol_version_ack(&[0x08, 0x20, 0x82, 0x02, 0x02, 0x08, 0x09]), Some(9));
    }

    #[test]
    fn disconnect_and_bare() {
        let d = build_disconnect(b"bye");
        assert_eq!(peek_type(&d), Some(DISCONNECT));
        assert_eq!(parse_disconnect(&d), Some(&b"bye"[..]));
        assert_eq!(parse_disconnect(&build_disconnect(b"")), Some(&b""[..]));
        assert_eq!(build_bare(HEARTBEAT), [0x08, 0x03]);
    }

    #[test]
    fn refuses_malformed_input() {
        assert!(!validate(&[0x08]), "a tag with no value");
        assert!(!validate(&[0x0b]), "a group");
        assert!(!validate(&[0x00, 0x00]), "field zero");
        assert_eq!(peek_type(&[0x12, 0x05, 0x01]), None, "a length past the end");
        assert_eq!(build_protocol_version_request(&[]), None);
    }
}
