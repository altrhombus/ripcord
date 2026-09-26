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
