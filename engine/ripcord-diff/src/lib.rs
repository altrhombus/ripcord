//! The C core, wrapped safely, for differential tests against `ripcord-proto`. docs/engine-plan.md: during
//! the transition the C core is a free second implementation of everything, and any difference in output
//! for the same input is a finding.
//!
//! Test tooling, never linked into a host. It is the only crate besides `ripcord-ffi` with `unsafe`, and
//! all of it is here: each wrapper checks lengths before handing a pointer to `shim/diff_shim.c`.

use std::ffi::c_void;
use std::os::raw::{c_char, c_int, c_long};
use std::sync::{Mutex, MutexGuard};

/// The C core is single-threaded by contract: `fec_reed_solomon_decode` keeps its matrices in `static`
/// buffers (a 32 KB console stack is why), so two test threads decoding at once corrupt each other. The
/// first run of these tests found exactly that. Every call into the C core holds this lock.
static C_CORE: Mutex<()> = Mutex::new(());

fn c_core() -> MutexGuard<'static, ()> {
    C_CORE.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

pub use ripcord_proto;

unsafe extern "C" {
    fn diff_pc_new(key: *const u8, iv: *const u8) -> *mut c_void;
    fn diff_pc_free(c: *mut c_void);
    fn diff_pc_compute_tag(
        c: *mut c_void,
        key_pos: u64,
        packet: *const u8,
        length: usize,
        tag_offset: c_int,
        zero_key_pos: c_int,
        out: *mut u8,
    ) -> c_int;
    fn diff_pc_crypt(c: *mut c_void, key_pos: u64, payload: *mut u8, length: usize);

    fn diff_fec_encode(buf: *mut u8, unit_size: usize, stride: usize, k: c_int, m: c_int) -> c_int;
    fn diff_fec_decode(
        buf: *mut u8,
        unit_size: usize,
        stride: usize,
        k: c_int,
        m: c_int,
        present: *const u8,
    ) -> c_int;

    fn diff_demux_new(key: *const u8, iv: *const u8, f: EventFn, user: *mut c_void) -> *mut c_void;
    fn diff_demux_free(d: *mut c_void);
    fn diff_demux_set_header(d: *mut c_void, data: *const u8, length: usize);
    fn diff_demux_ingest(d: *mut c_void, packet: *const u8, length: usize);
    fn diff_demux_stats(
        d: *mut c_void,
        received: *mut c_long,
        lost: *mut c_long,
        auth: *mut c_long,
        too_many: *mut c_long,
    );
    fn diff_demux_is_hevc(d: *mut c_void) -> c_int;

    fn diff_kdf(
        nonce: *const u8,
        companion: *const u8,
        version: c_int,
        key: *mut u8,
        material: *mut u8,
    ) -> c_int;
    fn diff_context_key(codec: c_int, version: c_int, out: *mut u8);
    fn diff_control_crypt(
        nonce: *const u8,
        companion: *const u8,
        codec: c_int,
        version: c_int,
        counter: u64,
        mode: c_int,
        data: *mut u8,
        length: usize,
    ) -> c_int;

    fn diff_regist_key(
        is_ps5: c_int,
        context: *const u8,
        length: usize,
        passcode: u32,
        out: *mut u8,
    ) -> c_int;
    fn diff_regist_account_key(
        is_ps5: c_int,
        context: *const u8,
        length: usize,
        seed: *const u8,
        out: *mut u8,
    ) -> c_int;
    fn diff_regist_wrap(
        is_ps5: c_int,
        account: c_int,
        unwrap: c_int,
        input: *const u8,
        context: *const u8,
        length: usize,
        out: *mut u8,
    ) -> c_int;
    fn diff_seed_decode(text: *const c_char, length: usize, out: *mut u8, cap: usize) -> usize;
    fn diff_seed_recover(
        is_ps5: c_int,
        d1: *const u8,
        d2: *const u8,
        text: *const c_char,
        length: usize,
        out: *mut u8,
    ) -> c_int;
}

type EventFn =
    extern "C" fn(user: *mut c_void, kind: c_int, a: c_int, b: c_int, data: *const u8, length: usize);

/// What a demuxer reported, in order. Both engines report into this, so a run compares as `==`.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Event {
    Video { data: Vec<u8>, keyframe: bool },
    Audio(Vec<u8>),
    Loss { first: u16, last: u16 },
    Control { packet_type: u8, data: Vec<u8> },
}

/// The counters both demuxers keep, read (and the unit counts reset) after a run.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Stats {
    pub received: u64,
    pub lost: u64,
    pub auth_failures: u64,
    pub frames_too_many_units: u64,
}

fn slice<'a>(data: *const u8, length: usize) -> &'a [u8] {
    if length == 0 {
        &[]
    } else {
        // SAFETY: the C core passes a buffer valid for `length` bytes for the duration of the callback.
        unsafe { std::slice::from_raw_parts(data, length) }
    }
}

extern "C" fn record(user: *mut c_void, kind: c_int, a: c_int, b: c_int, data: *const u8, length: usize) {
    // SAFETY: `user` is the `Box<Vec<Event>>` CDemux owns, alive for as long as the C demuxer is.
    let events = unsafe { &mut *(user as *mut Vec<Event>) };
    let data = slice(data, length).to_vec();
    events.push(match kind {
        1 => Event::Video { data, keyframe: a != 0 },
        2 => Event::Audio(data),
        3 => Event::Loss { first: a as u16, last: b as u16 },
        _ => Event::Control { packet_type: a as u8, data },
    });
}

/// The C core's `stream_demux`, with its events collected.
pub struct CDemux {
    raw: *mut c_void,
    // Boxed so the Vec itself has a fixed address: the C demuxer holds a pointer to it, and CDemux moves.
    #[allow(clippy::box_collection)]
    events: Box<Vec<Event>>,
}

impl CDemux {
    /// `keys` None selects the passthrough seam.
    pub fn new(keys: Option<(&[u8; 16], &[u8; 16])>) -> Self {
        let _core = c_core();
        let mut events = Box::new(Vec::new());
        let user = &mut *events as *mut Vec<Event> as *mut c_void;
        let (k, i) = keys.map_or((std::ptr::null(), std::ptr::null()), |(k, i)| (k.as_ptr(), i.as_ptr()));
        // SAFETY: keys are 16 bytes or null; `user` outlives the demuxer because both live in Self.
        let raw = unsafe { diff_demux_new(k, i, record, user) };
        assert!(!raw.is_null(), "the C demuxer could not be allocated");
        Self { raw, events }
    }

    pub fn set_video_header(&mut self, data: &[u8]) {
        let _core = c_core();
        // SAFETY: a valid slice, and a live demuxer.
        unsafe { diff_demux_set_header(self.raw, data.as_ptr(), data.len()) }
    }

    pub fn ingest(&mut self, packet: &[u8]) {
        let _core = c_core();
        // SAFETY: as above.
        unsafe { diff_demux_ingest(self.raw, packet.as_ptr(), packet.len()) }
    }

    pub fn is_hevc(&self) -> bool {
        let _core = c_core();
        // SAFETY: a live demuxer.
        unsafe { diff_demux_is_hevc(self.raw) != 0 }
    }

    pub fn take_events(&mut self) -> Vec<Event> {
        std::mem::take(&mut self.events)
    }

    pub fn stats(&mut self) -> Stats {
        let _core = c_core();
        let (mut r, mut l, mut a, mut t) = (0, 0, 0, 0);
        // SAFETY: four valid out-pointers and a live demuxer.
        unsafe { diff_demux_stats(self.raw, &mut r, &mut l, &mut a, &mut t) };
        Stats { received: r as u64, lost: l as u64, auth_failures: a as u64, frames_too_many_units: t as u64 }
    }
}

impl Drop for CDemux {
    fn drop(&mut self) {
        let _core = c_core();
        // SAFETY: allocated by diff_demux_new and freed once.
        unsafe { diff_demux_free(self.raw) }
    }
}

/// The C core's `stream_packet_crypto`.
pub struct CPacketCrypto(*mut c_void);

impl CPacketCrypto {
    pub fn new(key: &[u8; 16], iv: &[u8; 16]) -> Self {
        let _core = c_core();
        // SAFETY: two 16-byte arrays.
        let raw = unsafe { diff_pc_new(key.as_ptr(), iv.as_ptr()) };
        assert!(!raw.is_null());
        Self(raw)
    }

    pub fn compute_tag(
        &mut self,
        key_pos: u64,
        packet: &[u8],
        tag_offset: usize,
        zero_key_pos: bool,
    ) -> Option<[u8; 4]> {
        let _core = c_core();
        let mut out = [0u8; 4];
        let offset = c_int::try_from(tag_offset).ok()?;
        // SAFETY: a valid slice and a 4-byte out buffer.
        let ok = unsafe {
            diff_pc_compute_tag(
                self.0,
                key_pos,
                packet.as_ptr(),
                packet.len(),
                offset,
                c_int::from(zero_key_pos),
                out.as_mut_ptr(),
            )
        };
        (ok == 1).then_some(out)
    }

    pub fn crypt_payload(&mut self, key_pos: u64, payload: &mut [u8]) {
        let _core = c_core();
        // SAFETY: a valid mutable slice.
        unsafe { diff_pc_crypt(self.0, key_pos, payload.as_mut_ptr(), payload.len()) }
    }
}

impl Drop for CPacketCrypto {
    fn drop(&mut self) {
        let _core = c_core();
        // SAFETY: allocated by diff_pc_new and freed once.
        unsafe { diff_pc_free(self.0) }
    }
}

/// Whether `units` units of `unit_size` at `stride` fit in `len` bytes: checked before the C core, which
/// trusts its caller, is handed the buffer.
fn fits(len: usize, unit_size: usize, stride: usize, units: usize) -> bool {
    unit_size <= stride
        && units > 0
        && (units - 1).checked_mul(stride).and_then(|o| o.checked_add(unit_size)).is_some_and(|e| e <= len)
}

pub fn c_fec_encode(buf: &mut [u8], unit_size: usize, stride: usize, k: usize, m: usize) -> bool {
    let _core = c_core();
    if !fits(buf.len(), unit_size, stride, k + m) {
        return false;
    }
    // SAFETY: the geometry was checked against the buffer above.
    unsafe { diff_fec_encode(buf.as_mut_ptr(), unit_size, stride, k as c_int, m as c_int) == 1 }
}

pub fn c_fec_decode(
    buf: &mut [u8],
    unit_size: usize,
    stride: usize,
    k: usize,
    m: usize,
    present: &[bool],
) -> bool {
    let _core = c_core();
    if !fits(buf.len(), unit_size, stride, k + m) || present.len() < k + m {
        return false;
    }
    let flags: Vec<u8> = present.iter().map(|&p| u8::from(p)).collect();
    // SAFETY: the geometry was checked, and the flags cover k+m units.
    unsafe {
        diff_fec_decode(buf.as_mut_ptr(), unit_size, stride, k as c_int, m as c_int, flags.as_ptr()) == 1
    }
}

pub fn c_kdf(nonce: &[u8; 16], companion: &[u8; 16], version: i32) -> Option<([u8; 16], [u8; 16])> {
    let _core = c_core();
    let (mut key, mut material) = ([0u8; 16], [0u8; 16]);
    // SAFETY: 16-byte inputs and outputs.
    let ok = unsafe {
        diff_kdf(nonce.as_ptr(), companion.as_ptr(), version, key.as_mut_ptr(), material.as_mut_ptr())
    };
    (ok == 1).then_some((key, material))
}

pub fn c_context_key(codec: i32, version: i32) -> [u8; 16] {
    let _core = c_core();
    let mut out = [0u8; 16];
    // SAFETY: a 16-byte out buffer.
    unsafe { diff_context_key(codec, version, out.as_mut_ptr()) };
    out
}

/// mode 0 encrypt, 1 decrypt, 2 streaminfo. `false` if the family's tables are absent.
pub fn c_control_crypt(
    nonce: &[u8; 16],
    companion: &[u8; 16],
    codec: i32,
    version: i32,
    counter: u64,
    mode: i32,
    data: &mut [u8],
) -> bool {
    let _core = c_core();
    // SAFETY: 16-byte keys and a valid mutable slice.
    unsafe {
        diff_control_crypt(
            nonce.as_ptr(),
            companion.as_ptr(),
            codec,
            version,
            counter,
            mode,
            data.as_mut_ptr(),
            data.len(),
        ) == 1
    }
}

pub fn c_regist_key(is_ps5: bool, context: &[u8], passcode: u32) -> Option<[u8; 16]> {
    let _core = c_core();
    let mut out = [0u8; 16];
    // SAFETY: a valid slice and a 16-byte out buffer.
    let ok = unsafe {
        diff_regist_key(c_int::from(is_ps5), context.as_ptr(), context.len(), passcode, out.as_mut_ptr())
    };
    (ok == 1).then_some(out)
}

pub fn c_regist_account_key(is_ps5: bool, context: &[u8], seed: &[u8; 16]) -> Option<[u8; 16]> {
    let _core = c_core();
    let mut out = [0u8; 16];
    // SAFETY: as above.
    let ok = unsafe {
        diff_regist_account_key(
            c_int::from(is_ps5),
            context.as_ptr(),
            context.len(),
            seed.as_ptr(),
            out.as_mut_ptr(),
        )
    };
    (ok == 1).then_some(out)
}

pub fn c_regist_wrap(
    is_ps5: bool,
    account: bool,
    unwrap: bool,
    input: &[u8; 16],
    context: &[u8],
) -> Option<[u8; 16]> {
    let _core = c_core();
    let mut out = [0u8; 16];
    // SAFETY: as above.
    let ok = unsafe {
        diff_regist_wrap(
            c_int::from(is_ps5),
            c_int::from(account),
            c_int::from(unwrap),
            input.as_ptr(),
            context.as_ptr(),
            context.len(),
            out.as_mut_ptr(),
        )
    };
    (ok == 1).then_some(out)
}

/// The C decoder's answer, normalised to the Rust one's shape: `None` for a refusal (its 0).
pub fn c_seed_decode(text: &[u8]) -> Option<Vec<u8>> {
    let _core = c_core();
    let mut out = [0u8; 64];
    // SAFETY: a valid slice, and a 64-byte out buffer whose size is passed.
    let n = unsafe { diff_seed_decode(text.as_ptr().cast(), text.len(), out.as_mut_ptr(), out.len()) };
    (n > 0).then(|| out[..n].to_vec())
}

pub fn c_seed_recover(is_ps5: bool, d1: &[u8; 16], d2: &[u8; 16], text: &[u8]) -> Option<[u8; 16]> {
    let _core = c_core();
    let mut out = [0u8; 16];
    // SAFETY: 16-byte keys, a valid slice, a 16-byte out buffer.
    let ok = unsafe {
        diff_seed_recover(
            c_int::from(is_ps5),
            d1.as_ptr(),
            d2.as_ptr(),
            text.as_ptr().cast(),
            text.len(),
            out.as_mut_ptr(),
        )
    };
    (ok == 1).then_some(out)
}

/// A deterministic xorshift, so a failing differential case can be replayed from its seed.
pub struct Rng(u64);

impl Rng {
    pub fn new(seed: u64) -> Self {
        Self(seed | 1)
    }

    pub fn next_u64(&mut self) -> u64 {
        self.0 ^= self.0 << 13;
        self.0 ^= self.0 >> 7;
        self.0 ^= self.0 << 17;
        self.0
    }

    pub fn below(&mut self, n: u64) -> u64 {
        self.next_u64() % n.max(1)
    }

    pub fn fill(&mut self, buf: &mut [u8]) {
        for b in buf {
            *b = self.next_u64() as u8;
        }
    }

    pub fn bytes<const N: usize>(&mut self) -> [u8; N] {
        let mut b = [0u8; N];
        self.fill(&mut b);
        b
    }

    /// `min + below(spread)` random bytes.
    pub fn vec_of(&mut self, min: usize, spread: u64) -> Vec<u8> {
        let len = min + self.below(spread) as usize;
        self.vec(len)
    }

    pub fn vec(&mut self, len: usize) -> Vec<u8> {
        let mut v = vec![0u8; len];
        self.fill(&mut v);
        v
    }
}

// ---- Takion, the 9303 wire and the scripted console ----

unsafe extern "C" {
    fn diff_takion_data(
        first: c_int,
        chunk: *const u8,
        length: usize,
        tsn: *mut u32,
        channel: *mut u32,
        ending: *mut c_int,
        off: *mut usize,
        len: *mut usize,
    ) -> c_int;
    fn diff_takion_sack(chunk: *const u8, length: usize, out: *mut u32) -> c_int;
    fn diff_takion_init_ack(
        chunk: *const u8,
        length: usize,
        tag: *mut u32,
        tsn: *mut u32,
        cookie: *mut u8,
    ) -> c_int;
    fn diff_control_peek(data: *const u8, length: usize, kind: *mut u32) -> c_int;
    fn diff_control_validate(data: *const u8, length: usize) -> c_int;
    fn diff_control_reply(data: *const u8, length: usize, nums: *mut u32, spans: *mut usize) -> c_int;
    fn diff_control_stream_info(data: *const u8, length: usize, nums: *mut u32, spans: *mut usize) -> c_int;
    fn diff_control_disconnect(data: *const u8, length: usize, span: *mut usize) -> c_int;
    fn diff_control_version_ack(data: *const u8, length: usize, version: *mut u32) -> c_int;
    fn diff_seal_sequence(
        key: *const u8,
        iv: *const u8,
        kinds: *const c_int,
        packets: *const *mut u8,
        lengths: *const usize,
        offsets: *const usize,
        count: usize,
    );
    fn diff_verify_control(key: *const u8, iv: *const u8, packet: *const u8, length: usize) -> c_int;
    fn diff_senkusha_echo(datagram: *const u8, length: usize, sequence: *mut u8) -> c_int;
    fn diff_dgram_prelude(data: *const u8, length: usize, out: *mut u8) -> c_int;
    fn diff_dgram_chunks(data: *const u8, length: usize, out: *mut usize, max: usize) -> usize;
    fn diff_http_complete(data: *const u8, length: usize) -> c_int;
    fn diff_console_new(
        emit: ConsoleEmitFn,
        user: *mut c_void,
        init: *const c_char,
        other: *const c_char,
        close: c_int,
    ) -> *mut c_void;
    fn diff_console_free(c: *mut c_void);
    fn diff_console_datagram(c: *mut c_void, d: *const u8, n: usize);
    fn diff_console_counts(c: *mut c_void, out: *mut c_int);
    fn diff_console_frame(c: *mut c_void, i: c_int, data: *mut *const u8) -> usize;
    fn diff_console_request(c: *mut c_void, i: c_int, text: *mut *const c_char) -> usize;
}

type ConsoleEmitFn = extern "C" fn(user: *mut c_void, datagram: *const u8, length: usize);

/// A DATA chunk as the C parser reads it: (tsn, channel, ending, payload).
pub fn c_takion_data(first: bool, chunk: &[u8]) -> Option<(u32, u16, bool, Vec<u8>)> {
    let _core = c_core();
    let (mut tsn, mut channel, mut ending, mut off, mut len) = (0u32, 0u32, 0, 0usize, 0usize);
    // SAFETY: a valid slice and five out-pointers.
    let ok = unsafe {
        diff_takion_data(
            c_int::from(first),
            chunk.as_ptr(),
            chunk.len(),
            &mut tsn,
            &mut channel,
            &mut ending,
            &mut off,
            &mut len,
        )
    };
    (ok == 1).then(|| (tsn, channel as u16, ending != 0, chunk[off..off + len].to_vec()))
}

pub fn c_takion_sack(chunk: &[u8]) -> Option<[u32; 4]> {
    let _core = c_core();
    let mut out = [0u32; 4];
    // SAFETY: a valid slice and a 4-element out array.
    (unsafe { diff_takion_sack(chunk.as_ptr(), chunk.len(), out.as_mut_ptr()) } == 1).then_some(out)
}

pub fn c_takion_init_ack(chunk: &[u8]) -> Option<(u32, u32, [u8; 32])> {
    let _core = c_core();
    let (mut tag, mut tsn, mut cookie) = (0u32, 0u32, [0u8; 32]);
    // SAFETY: a valid slice and outputs of the right sizes.
    (unsafe { diff_takion_init_ack(chunk.as_ptr(), chunk.len(), &mut tag, &mut tsn, cookie.as_mut_ptr()) }
        == 1)
        .then_some((tag, tsn, cookie))
}

pub fn c_control_peek(data: &[u8]) -> Option<u32> {
    let _core = c_core();
    let mut t = 0;
    // SAFETY: a valid slice.
    (unsafe { diff_control_peek(data.as_ptr(), data.len(), &mut t) } == 1).then_some(t)
}

pub fn c_control_validate(data: &[u8]) -> bool {
    let _core = c_core();
    // SAFETY: a valid slice.
    unsafe { diff_control_validate(data.as_ptr(), data.len()) == 1 }
}

fn span(data: &[u8], offset: usize, length: usize) -> Option<Vec<u8>> {
    (offset != usize::MAX).then(|| data[offset..offset + length].to_vec())
}

/// SESSION_REPLY as the C parser reads it: the four numbers, then session key, server version string,
/// public key and signature (optional ones `None` when absent).
pub type CReply = ([u32; 4], Option<Vec<u8>>, Option<Vec<u8>>, Option<Vec<u8>>, Option<Vec<u8>>);

pub fn c_control_reply(data: &[u8]) -> Option<CReply> {
    let _core = c_core();
    let (mut nums, mut spans) = ([0u32; 4], [0usize; 8]);
    // SAFETY: a valid slice and outputs of the right sizes.
    if unsafe { diff_control_reply(data.as_ptr(), data.len(), nums.as_mut_ptr(), spans.as_mut_ptr()) } != 1 {
        return None;
    }
    Some((
        nums,
        span(data, spans[0], spans[1]),
        span(data, spans[2], spans[3]),
        span(data, spans[4], spans[5]),
        span(data, spans[6], spans[7]),
    ))
}

pub fn c_control_stream_info(data: &[u8]) -> Option<([u32; 3], Vec<u8>, Vec<u8>)> {
    let _core = c_core();
    let (mut nums, mut spans) = ([0u32; 3], [0usize; 4]);
    // SAFETY: as above.
    if unsafe { diff_control_stream_info(data.as_ptr(), data.len(), nums.as_mut_ptr(), spans.as_mut_ptr()) }
        != 1
    {
        return None;
    }
    Some((
        nums,
        span(data, spans[0], spans[1]).unwrap_or_default(),
        span(data, spans[2], spans[3]).unwrap_or_default(),
    ))
}

pub fn c_control_disconnect(data: &[u8]) -> Option<Vec<u8>> {
    let _core = c_core();
    let mut s = [0usize; 2];
    // SAFETY: as above.
    (unsafe { diff_control_disconnect(data.as_ptr(), data.len(), s.as_mut_ptr()) } == 1)
        .then(|| data[s[0]..s[0] + s[1]].to_vec())
}

pub fn c_control_version_ack(data: &[u8]) -> Option<u32> {
    let _core = c_core();
    let mut v = 0;
    // SAFETY: as above.
    (unsafe { diff_control_version_ack(data.as_ptr(), data.len(), &mut v) } == 1).then_some(v)
}

/// Seals `packets` in order through one C sealer: kind 0 control, 1 congestion, 2 input at `offset`.
pub fn c_seal_sequence(key: &[u8; 16], iv: &[u8; 16], packets: &mut [(i32, Vec<u8>, usize)]) {
    let _core = c_core();
    let kinds: Vec<c_int> = packets.iter().map(|p| p.0).collect();
    let lengths: Vec<usize> = packets.iter().map(|p| p.1.len()).collect();
    let offsets: Vec<usize> = packets.iter().map(|p| p.2).collect();
    let ptrs: Vec<*mut u8> = packets.iter_mut().map(|p| p.1.as_mut_ptr()).collect();
    // SAFETY: every pointer is a live buffer of its listed length, and the arrays share one length.
    unsafe {
        diff_seal_sequence(
            key.as_ptr(),
            iv.as_ptr(),
            kinds.as_ptr(),
            ptrs.as_ptr(),
            lengths.as_ptr(),
            offsets.as_ptr(),
            packets.len(),
        )
    }
}

pub fn c_verify_control(key: &[u8; 16], iv: &[u8; 16], packet: &[u8]) -> bool {
    let _core = c_core();
    // SAFETY: 16-byte keys and a valid slice.
    unsafe { diff_verify_control(key.as_ptr(), iv.as_ptr(), packet.as_ptr(), packet.len()) == 1 }
}

pub fn c_senkusha_echo(datagram: &[u8]) -> Option<u8> {
    let _core = c_core();
    let mut s = 0u8;
    // SAFETY: a valid slice.
    (unsafe { diff_senkusha_echo(datagram.as_ptr(), datagram.len(), &mut s) } == 1).then_some(s)
}

/// The prelude as C parses it, re-serialised.
pub fn c_dgram_prelude(data: &[u8]) -> Option<[u8; 88]> {
    let _core = c_core();
    let mut out = [0u8; 88];
    // SAFETY: a valid slice and an 88-byte out buffer.
    (unsafe { diff_dgram_prelude(data.as_ptr(), data.len(), out.as_mut_ptr()) } == 1).then_some(out)
}

/// Every chunk as C iterates them: (kind, flags, body, source, destination, words).
pub fn c_dgram_chunks(data: &[u8]) -> Vec<(u8, u8, Vec<u8>, u16, u16, u8)> {
    let _core = c_core();
    let mut out = vec![0usize; 7 * 64];
    // SAFETY: a valid slice and room for 64 chunks.
    let n = unsafe { diff_dgram_chunks(data.as_ptr(), data.len(), out.as_mut_ptr(), 64) };
    out.chunks(7)
        .take(n)
        .map(|c| {
            (c[0] as u8, c[1] as u8, data[c[2]..c[2] + c[3]].to_vec(), c[4] as u16, c[5] as u16, c[6] as u8)
        })
        .collect()
}

pub fn c_http_complete(data: &[u8]) -> bool {
    let _core = c_core();
    // SAFETY: a valid slice.
    unsafe { diff_http_complete(data.as_ptr(), data.len()) == 1 }
}

extern "C" fn console_emit(user: *mut c_void, datagram: *const u8, length: usize) {
    // SAFETY: `user` is the Box<Vec<Vec<u8>>> CConsole owns; the datagram is valid for the call.
    let out = unsafe { &mut *(user as *mut Vec<Vec<u8>>) };
    out.push(slice(datagram, length).to_vec());
}

/// `libripcord/tests/fake_dgram_console.h`, the C scripted console.
pub struct CConsole {
    raw: *mut c_void,
    // Boxed for a fixed address, which the C side holds.
    #[allow(clippy::box_collection)]
    emitted: Box<Vec<Vec<u8>>>,
    _replies: (Option<std::ffi::CString>, Option<std::ffi::CString>),
}

impl CConsole {
    pub fn new(init_reply: Option<&str>, other_reply: Option<&str>, close_before_answering: bool) -> Self {
        let _core = c_core();
        let mut emitted = Box::new(Vec::new());
        let init = init_reply.map(|s| std::ffi::CString::new(s).unwrap());
        let other = other_reply.map(|s| std::ffi::CString::new(s).unwrap());
        let user = &mut *emitted as *mut Vec<Vec<u8>> as *mut c_void;
        // SAFETY: the strings and the emit buffer outlive the console, all three living in Self.
        let raw = unsafe {
            diff_console_new(
                console_emit,
                user,
                init.as_ref().map_or(std::ptr::null(), |s| s.as_ptr()),
                other.as_ref().map_or(std::ptr::null(), |s| s.as_ptr()),
                c_int::from(close_before_answering),
            )
        };
        assert!(!raw.is_null());
        Self { raw, emitted, _replies: (init, other) }
    }

    /// What the console sends in answer to `datagram`.
    pub fn on_datagram(&mut self, datagram: &[u8]) -> Vec<Vec<u8>> {
        let _core = c_core();
        // SAFETY: a live console and a valid slice.
        unsafe { diff_console_datagram(self.raw, datagram.as_ptr(), datagram.len()) };
        std::mem::take(&mut self.emitted)
    }

    /// (inits, hellos, closes received, requests, ctrl open, frames)
    pub fn counts(&self) -> [i32; 6] {
        let _core = c_core();
        let mut out = [0; 6];
        // SAFETY: a live console and a 6-element out array.
        unsafe { diff_console_counts(self.raw, out.as_mut_ptr()) };
        out
    }

    pub fn frame(&self, i: usize) -> Vec<u8> {
        let _core = c_core();
        let mut p = std::ptr::null();
        // SAFETY: i is below the frame count the caller read from counts().
        let n = unsafe { diff_console_frame(self.raw, i as c_int, &mut p) };
        slice(p, n).to_vec()
    }

    pub fn request(&self, i: usize) -> Vec<u8> {
        let _core = c_core();
        let mut p = std::ptr::null();
        // SAFETY: as above.
        let n = unsafe { diff_console_request(self.raw, i as c_int, &mut p) };
        slice(p.cast(), n).to_vec()
    }
}

impl Drop for CConsole {
    fn drop(&mut self) {
        let _core = c_core();
        // SAFETY: allocated by diff_console_new and freed once.
        unsafe { diff_console_free(self.raw) }
    }
}

// ---- discovery, wake, /sess ----

unsafe extern "C" {
    fn diff_discovery_parse(
        data: *const u8,
        length: usize,
        out: *mut [c_char; 128],
        awake: *mut c_int,
    ) -> c_int;
    fn diff_wake_credential(key: *const u8, length: usize, out: *mut c_char) -> c_int;
    fn diff_wake_payload(ps5: c_int, credential: *const c_char, out: *mut c_char, size: usize) -> usize;
    fn diff_sess_auth(key: *const u8, length: usize, out: *mut u8);
    fn diff_sess_did(id: *const u8, length: usize, out: *mut u8);
    fn diff_sess_os(major: c_int, minor: c_int, out: *mut c_char, size: usize) -> usize;
    fn diff_ctrl_parse(data: *const u8, length: usize, kind: *mut u32, payload_length: *mut usize) -> usize;
    fn diff_sess_response(
        data: *const c_char,
        length: usize,
        status: *mut c_int,
        name: *const c_char,
        out: *mut c_char,
        size: usize,
        has: *mut c_int,
    ) -> usize;
}

fn c_string(buf: &[c_char]) -> String {
    let bytes: Vec<u8> = buf.iter().take_while(|&&c| c != 0).map(|&c| c as u8).collect();
    String::from_utf8_lossy(&bytes).into_owned()
}

/// A SRCH reply as C parses it: (host id, host type, host name, system version, awake).
pub fn c_discovery_parse(data: &[u8]) -> Option<(String, String, String, String, bool)> {
    let _core = c_core();
    let mut out = [[0 as c_char; 128]; 4];
    let mut awake = 0;
    // SAFETY: a valid slice, four 128-byte buffers and an out-int.
    if unsafe { diff_discovery_parse(data.as_ptr(), data.len(), out.as_mut_ptr(), &mut awake) } != 1 {
        return None;
    }
    Some((c_string(&out[0]), c_string(&out[1]), c_string(&out[2]), c_string(&out[3]), awake != 0))
}

pub fn c_wake_credential(key: &[u8]) -> Option<String> {
    let _core = c_core();
    let mut out = [0 as c_char; 16];
    // SAFETY: a valid slice and a 16-byte buffer, which the C side is told the size of.
    (unsafe { diff_wake_credential(key.as_ptr(), key.len(), out.as_mut_ptr()) } == 1).then(|| c_string(&out))
}

pub fn c_wake_payload(ps5: bool, credential: &str) -> Vec<u8> {
    let _core = c_core();
    let credential = std::ffi::CString::new(credential).unwrap();
    let mut out = [0 as c_char; 512];
    // SAFETY: a NUL-terminated string and a buffer whose size is passed.
    let n = unsafe { diff_wake_payload(c_int::from(ps5), credential.as_ptr(), out.as_mut_ptr(), out.len()) };
    out[..n].iter().map(|&c| c as u8).collect()
}

/// (RP-Auth, RP-Did, RP-OSType) plaintexts as C builds them.
pub fn c_sess_fields(key: &[u8], device_id: &[u8], major: i32, minor: i32) -> ([u8; 16], [u8; 32], Vec<u8>) {
    let _core = c_core();
    let (mut auth, mut did, mut os) = ([0u8; 16], [0u8; 32], [0 as c_char; 32]);
    // SAFETY: valid slices and outputs of the sizes the C side writes.
    let n = unsafe {
        diff_sess_auth(key.as_ptr(), key.len(), auth.as_mut_ptr());
        diff_sess_did(device_id.as_ptr(), device_id.len(), did.as_mut_ptr());
        diff_sess_os(major, minor, os.as_mut_ptr(), os.len())
    };
    (auth, did, os[..n].iter().map(|&c| c as u8).collect())
}

/// (type, payload length, bytes used) as C parses a control frame.
pub fn c_ctrl_parse(data: &[u8]) -> Option<(u16, usize, usize)> {
    let _core = c_core();
    let (mut kind, mut len) = (0u32, 0usize);
    // SAFETY: a valid slice.
    let used = unsafe { diff_ctrl_parse(data.as_ptr(), data.len(), &mut kind, &mut len) };
    (used > 0).then_some((kind as u16, len, used))
}

/// (status, bytes used, the named header) as C parses a /sess response.
pub fn c_sess_response(data: &[u8], header: &str) -> Option<(i32, usize, Option<String>)> {
    let _core = c_core();
    let name = std::ffi::CString::new(header).unwrap();
    let (mut status, mut has) = (0, 0);
    let mut out = [0 as c_char; 256];
    // SAFETY: a valid slice, a NUL-terminated name and a buffer whose size is passed.
    let used = unsafe {
        diff_sess_response(
            data.as_ptr().cast(),
            data.len(),
            &mut status,
            name.as_ptr(),
            out.as_mut_ptr(),
            out.len(),
            &mut has,
        )
    };
    (used > 0).then(|| (status, used, (has == 1).then(|| c_string(&out))))
}
