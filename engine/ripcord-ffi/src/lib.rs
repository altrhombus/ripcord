//! The Rust engine's C ABI. Every host (Swift, .NET, and whatever follows) reaches the engine through
//! these exports and nothing else; `ripcord.h` and `NativeMethods.g.cs` are generated from this file by
//! `build.rs`. The rules are `docs/engine-plan.md`'s, and each is kept here as follows:
//!
//! - **The header is generated.** Edit this file, never `ripcord.h`.
//! - **A layout check at load.** [`ripcord_api_version`] and [`ripcord_struct_size`] let a binding
//!   compare its view of every struct that crosses the boundary against the library's, and refuse to
//!   run on a mismatch.
//! - **No panic crosses the boundary.** Every export runs inside `catch_unwind`. A panic poisons the
//!   handle it happened on, which then refuses further work with [`RipcordStatus::Poisoned`]; it never
//!   unwinds into the host and never aborts the app. A panic is still a bug, and the fuzzers treat it
//!   as one.
//! - **The threading contract.** A handle is used from one thread at a time. The engine creates no
//!   threads and takes no locks.
//! - **Borrowed buffers.** Every pointer passed to a callback is valid for that call only.
//!
//! This is the Phase 1 surface: the stream plane only. Phase 2 grows it toward `halyard_client.h`.

use std::ffi::c_void;
use std::panic::{AssertUnwindSafe, catch_unwind};

use ripcord_proto::crypto::ecdh::{Curve, Ecdh};
use ripcord_proto::stream::demux::{DemuxSink, PacketOpener, Passthrough, StreamDemux};
use ripcord_proto::stream::header::StreamHeader;
use ripcord_proto::stream::packet_crypto::PacketCrypto;

/// Bumped whenever an export's signature or a crossing struct's layout changes.
pub const RIPCORD_API_VERSION: u32 = 2;

/// `repr(C)`, not `repr(i32)`, for the header's sake: cbindgen writes a fixed-width enum as an `enum` tag
/// plus a same-named integer typedef before C23, and Swift imports those as two different types. A C
/// enum is `int`-sized on every target this engine builds for, and csbindgen maps it to `uint`.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RipcordStatus {
    Ok = 0,
    /// The input was well-formed but did not pass: a packet whose tag does not verify, or one the
    /// engine refuses to process (longer than the engine's packet limit).
    Rejected = 1,
    /// A required pointer was null, or a length was impossible.
    InvalidArgument = 2,
    /// An earlier call on this handle panicked. Free it; it will do no more work.
    Poisoned = 3,
    /// This call panicked. The handle is now poisoned.
    Panicked = 4,
}

/// Ids for [`ripcord_struct_size`]: every struct that crosses the boundary by value or by layout.
/// `repr(C)` for the reason [`RipcordStatus`] gives.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RipcordStructId {
    StreamHeader = 1,
    DemuxSink = 2,
    DemuxCounters = 3,
    EcdhBackend = 4,
    KatResult = 5,
    ScriptedConsoleCounts = 6,
}

/// The parsed A/V header, as the demuxer hands it to a control-packet callback.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct RipcordStreamHeader {
    pub packet_type: u8,
    pub has_extended_header: bool,
    pub packet_index: u16,
    pub frame_index: u16,
    pub unit_index: u16,
    pub total_units: u16,
    pub parity_units: u16,
    pub codec: u8,
    pub key_position: u32,
}

impl From<&StreamHeader> for RipcordStreamHeader {
    fn from(h: &StreamHeader) -> Self {
        Self {
            packet_type: h.packet_type,
            has_extended_header: h.has_extended_header,
            packet_index: h.packet_index,
            frame_index: h.frame_index,
            unit_index: h.unit_index,
            total_units: h.total_units,
            parity_units: h.parity_units,
            codec: h.codec,
            key_position: h.key_position,
        }
    }
}

/// Where the demuxer reports. Any callback may be null to ignore that event. `user` is passed back
/// unchanged. Every buffer is borrowed for the duration of the call.
#[repr(C)]
pub struct RipcordDemuxSink {
    pub user: *mut c_void,
    pub video_frame:
        Option<extern "C" fn(user: *mut c_void, data: *const u8, length: usize, is_keyframe: bool)>,
    pub audio_frame: Option<extern "C" fn(user: *mut c_void, data: *const u8, length: usize)>,
    /// An inclusive range of lost frame indices, to be reported to the console with a request for a
    /// fresh IDR.
    pub video_loss: Option<extern "C" fn(user: *mut c_void, first_frame_index: u16, last_frame_index: u16)>,
    pub control_packet: Option<
        extern "C" fn(user: *mut c_void, header: *const RipcordStreamHeader, data: *const u8, length: usize),
    >,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct RipcordDemuxCounters {
    pub auth_failures: u64,
    pub frames_too_many_units: u64,
}

/// Curve ids for [`RipcordEcdhBackend`]: the same numbers as `libripcord/crypto/rc_ecdh.h`.
pub const RIPCORD_CURVE_P256: u32 = 1;
pub const RIPCORD_CURVE_P521: u32 = 2;

/// Key agreement supplied by the host: CryptoKit on Apple platforms, CNG on Windows
/// (docs/engine-plan.md, "Dependencies"). Both functions write at most `out_capacity` bytes, set
/// `*out_length`, and return false on any failure, never anything key-shaped. The contract is
/// `ripcord_proto::crypto::ecdh`'s:
///
/// - `public_key`: the uncompressed SEC1 point (`0x04 || X || Y`) for a private scalar, refusing zero or a
///   scalar at or above the group order.
/// - `shared_secret`: the X coordinate at the curve's full width, after validating that the peer's
///   uncompressed point is on the curve.
///
/// `curve` is a `RIPCORD_CURVE_*` value. A backend is used on a platform only once it has passed
/// `session-crypto.kat` there.
#[repr(C)]
pub struct RipcordEcdhBackend {
    pub user: *mut c_void,
    pub public_key: Option<
        extern "C" fn(
            user: *mut c_void,
            curve: u32,
            private_key: *const u8,
            private_key_length: usize,
            out: *mut u8,
            out_capacity: usize,
            out_length: *mut usize,
        ) -> bool,
    >,
    pub shared_secret: Option<
        extern "C" fn(
            user: *mut c_void,
            curve: u32,
            private_key: *const u8,
            private_key_length: usize,
            peer_public_key: *const u8,
            peer_public_key_length: usize,
            out: *mut u8,
            out_capacity: usize,
            out_length: *mut usize,
        ) -> bool,
    >,
}

/// A host backend behind the engine's `Ecdh` trait. Only the test-support exports use it until the
/// connect sequence's key agreement is exported.
#[cfg_attr(not(feature = "test-support"), allow(dead_code))]
pub(crate) struct HostEcdh<'a>(pub(crate) &'a RipcordEcdhBackend);

#[cfg_attr(not(feature = "test-support"), allow(dead_code))]
fn curve_id(curve: Curve) -> u32 {
    match curve {
        Curve::P256 => RIPCORD_CURVE_P256,
        Curve::P521 => RIPCORD_CURVE_P521,
    }
}

impl Ecdh for HostEcdh<'_> {
    fn public_key(&self, curve: Curve, private_key: &[u8]) -> Option<Vec<u8>> {
        let f = self.0.public_key?;
        let mut out = vec![0u8; curve.public_key_length()];
        let mut n = 0usize;
        let ok = f(
            self.0.user,
            curve_id(curve),
            private_key.as_ptr(),
            private_key.len(),
            out.as_mut_ptr(),
            out.len(),
            &mut n,
        );
        (ok && n == out.len()).then_some(out)
    }

    fn shared_secret(&self, curve: Curve, private_key: &[u8], peer_public_key: &[u8]) -> Option<Vec<u8>> {
        let f = self.0.shared_secret?;
        let mut out = vec![0u8; curve.secret_length()];
        let mut n = 0usize;
        let ok = f(
            self.0.user,
            curve_id(curve),
            private_key.as_ptr(),
            private_key.len(),
            peer_public_key.as_ptr(),
            peer_public_key.len(),
            out.as_mut_ptr(),
            out.len(),
            &mut n,
        );
        (ok && n == out.len()).then_some(out)
    }
}

/// One direction's per-packet stream crypto. Opaque.
pub struct RipcordPacketCrypto {
    inner: PacketCrypto,
    poisoned: bool,
}

/// The A/V demuxer, with its crypto seam. Opaque; about 4.6 MB, all allocated by the constructor.
pub struct RipcordStreamDemux {
    inner: StreamDemux<Box<dyn PacketOpener + Send>>,
    poisoned: bool,
}

/// A handle that a panic poisons.
trait Handle {
    fn poisoned(&mut self) -> &mut bool;
}

impl Handle for RipcordPacketCrypto {
    fn poisoned(&mut self) -> &mut bool {
        &mut self.poisoned
    }
}

impl Handle for RipcordStreamDemux {
    fn poisoned(&mut self) -> &mut bool {
        &mut self.poisoned
    }
}

/// Runs `f` on the handle behind `handle`, containing any panic: a panic poisons the handle and
/// reports [`RipcordStatus::Panicked`], and a poisoned or null handle does no work.
fn with_handle<H: Handle>(handle: *mut H, f: impl FnOnce(&mut H) -> RipcordStatus) -> RipcordStatus {
    // SAFETY: the caller's contract is that a non-null handle came from this library's constructor, has
    // not been freed, and is used from one thread at a time.
    let Some(h) = (unsafe { handle.as_mut() }) else {
        return RipcordStatus::InvalidArgument;
    };
    if *h.poisoned() {
        return RipcordStatus::Poisoned;
    }
    match catch_unwind(AssertUnwindSafe(|| f(&mut *h))) {
        Ok(status) => status,
        Err(_) => {
            *h.poisoned() = true;
            RipcordStatus::Panicked
        }
    }
}

/// Runs a call that has no handle yet (constructors, queries), turning a panic into `on_panic`.
fn guard<T>(on_panic: T, f: impl FnOnce() -> T) -> T {
    catch_unwind(AssertUnwindSafe(f)).unwrap_or(on_panic)
}

/// A borrowed byte slice from a pointer and length. A zero length needs no pointer.
///
/// # Safety
/// A non-null `data` must be valid for `length` bytes for `'a`.
unsafe fn bytes<'a>(data: *const u8, length: usize) -> Option<&'a [u8]> {
    if length == 0 {
        return Some(&[]);
    }
    if data.is_null() || length > isize::MAX as usize {
        return None;
    }
    // SAFETY: non-null, within isize::MAX, and valid for `length` bytes per the caller's contract.
    Some(unsafe { std::slice::from_raw_parts(data, length) })
}

/// # Safety
/// A non-null `data` must be valid for writes of `length` bytes for `'a`, and alias nothing else.
unsafe fn bytes_mut<'a>(data: *mut u8, length: usize) -> Option<&'a mut [u8]> {
    if length == 0 {
        return Some(&mut []);
    }
    if data.is_null() || length > isize::MAX as usize {
        return None;
    }
    // SAFETY: as above, and exclusively borrowed per the caller's contract.
    Some(unsafe { std::slice::from_raw_parts_mut(data, length) })
}

/// # Safety
/// A non-null `data` must be valid for 16 bytes.
unsafe fn key16(data: *const u8) -> Option<[u8; 16]> {
    // SAFETY: forwarded.
    unsafe { bytes(data, 16) }.map(|b| b.try_into().expect("16 bytes"))
}

// ---- version and layout ----

/// The ABI version this library was built with. A binding compares it with the version it was
/// generated against and refuses to run on a mismatch.
#[unsafe(no_mangle)]
pub extern "C" fn ripcord_api_version() -> u32 {
    RIPCORD_API_VERSION
}

/// `sizeof` the struct named by `id` (a [`RipcordStructId`]) as this library was compiled, or 0 for an
/// id it does not know. Taken as a plain integer so an unknown id is an answer, not undefined behaviour.
#[unsafe(no_mangle)]
pub extern "C" fn ripcord_struct_size(id: u32) -> usize {
    match id {
        x if x == RipcordStructId::StreamHeader as u32 => size_of::<RipcordStreamHeader>(),
        x if x == RipcordStructId::DemuxSink as u32 => size_of::<RipcordDemuxSink>(),
        x if x == RipcordStructId::DemuxCounters as u32 => size_of::<RipcordDemuxCounters>(),
        x if x == RipcordStructId::EcdhBackend as u32 => size_of::<RipcordEcdhBackend>(),
        x if x == RipcordStructId::KatResult as u32 => size_of::<RipcordKatResult>(),
        x if x == RipcordStructId::ScriptedConsoleCounts as u32 => size_of::<RipcordScriptedConsoleCounts>(),
        _ => 0,
    }
}

// ---- packet crypto ----

/// A new packet-crypto context for one direction, from the 16-byte AES key and 16-byte base IV the
/// stream key schedule derived. Null if either pointer is null. Free with [`ripcord_packet_crypto_free`].
///
/// # Safety
/// `aes_key` and `base_iv` must each be valid for 16 bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_packet_crypto_new(
    aes_key: *const u8,
    base_iv: *const u8,
) -> *mut RipcordPacketCrypto {
    guard(std::ptr::null_mut(), || {
        // SAFETY: forwarded from this function's contract.
        let (Some(key), Some(iv)) = (unsafe { key16(aes_key) }, unsafe { key16(base_iv) }) else {
            return std::ptr::null_mut();
        };
        Box::into_raw(Box::new(RipcordPacketCrypto { inner: PacketCrypto::new(&key, &iv), poisoned: false }))
    })
}

/// # Safety
/// `ctx` must be null or a live pointer from [`ripcord_packet_crypto_new`], not used after this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_packet_crypto_free(ctx: *mut RipcordPacketCrypto) {
    if !ctx.is_null() {
        // SAFETY: per the contract, `ctx` came from Box::into_raw and is freed once.
        guard((), || drop(unsafe { Box::from_raw(ctx) }));
    }
}

/// Verifies the packet's on-wire 4-byte tag at `tag_offset`. `zero_key_pos` selects the control and
/// congestion AAD rule, which also zeroes the key position after the tag; A/V and feedback leave it.
/// [`RipcordStatus::Ok`] if the tag holds, [`RipcordStatus::Rejected`] if not.
///
/// # Safety
/// `ctx` as for [`ripcord_packet_crypto_free`]; `packet` valid for `length` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_packet_crypto_verify(
    ctx: *mut RipcordPacketCrypto,
    key_pos: u64,
    packet: *const u8,
    length: usize,
    tag_offset: usize,
    zero_key_pos: bool,
) -> RipcordStatus {
    with_handle(ctx, |c| {
        // SAFETY: forwarded from this function's contract.
        let Some(packet) = (unsafe { bytes(packet, length) }) else {
            return RipcordStatus::InvalidArgument;
        };
        if c.inner.verify(key_pos, packet, tag_offset, zero_key_pos) {
            RipcordStatus::Ok
        } else {
            RipcordStatus::Rejected
        }
    })
}

/// Computes the tag and writes it into `packet[tag_offset..tag_offset + 4]` (sender side).
///
/// # Safety
/// As for [`ripcord_packet_crypto_verify`], with `packet` writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_packet_crypto_seal(
    ctx: *mut RipcordPacketCrypto,
    key_pos: u64,
    packet: *mut u8,
    length: usize,
    tag_offset: usize,
    zero_key_pos: bool,
) -> RipcordStatus {
    with_handle(ctx, |c| {
        // SAFETY: forwarded from this function's contract.
        let Some(packet) = (unsafe { bytes_mut(packet, length) }) else {
            return RipcordStatus::InvalidArgument;
        };
        if c.inner.seal(key_pos, packet, tag_offset, zero_key_pos) {
            RipcordStatus::Ok
        } else {
            RipcordStatus::Rejected
        }
    })
}

/// AES-128-CTR over `payload` in place. The same call encrypts and decrypts.
///
/// # Safety
/// As for [`ripcord_packet_crypto_seal`], with `payload` writable for `length` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_packet_crypto_crypt_payload(
    ctx: *mut RipcordPacketCrypto,
    key_pos: u64,
    payload: *mut u8,
    length: usize,
) -> RipcordStatus {
    with_handle(ctx, |c| {
        // SAFETY: forwarded from this function's contract.
        let Some(payload) = (unsafe { bytes_mut(payload, length) }) else {
            return RipcordStatus::InvalidArgument;
        };
        c.inner.crypt_payload(key_pos, payload);
        RipcordStatus::Ok
    })
}

// ---- the demuxer ----

fn new_demux(opener: Box<dyn PacketOpener + Send>) -> *mut RipcordStreamDemux {
    Box::into_raw(Box::new(RipcordStreamDemux { inner: StreamDemux::new(opener), poisoned: false }))
}

/// A demuxer that verifies and decrypts every media packet with the given direction's keys (the
/// console-to-client keys, in a real session). Null if either pointer is null.
///
/// # Safety
/// `aes_key` and `base_iv` must each be valid for 16 bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_stream_demux_new(
    aes_key: *const u8,
    base_iv: *const u8,
) -> *mut RipcordStreamDemux {
    guard(std::ptr::null_mut(), || {
        // SAFETY: forwarded from this function's contract.
        let (Some(key), Some(iv)) = (unsafe { key16(aes_key) }, unsafe { key16(base_iv) }) else {
            return std::ptr::null_mut();
        };
        new_demux(Box::new(PacketCrypto::new(&key, &iv)))
    })
}

/// A demuxer that trusts every packet as plaintext: the passthrough seam, for fixtures and captures.
#[unsafe(no_mangle)]
pub extern "C" fn ripcord_stream_demux_new_passthrough() -> *mut RipcordStreamDemux {
    guard(std::ptr::null_mut(), || new_demux(Box::new(Passthrough)))
}

/// # Safety
/// `demux` must be null or a live pointer from a demux constructor, not used after this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_stream_demux_free(demux: *mut RipcordStreamDemux) {
    if !demux.is_null() {
        // SAFETY: per the contract, `demux` came from Box::into_raw and is freed once.
        guard((), || drop(unsafe { Box::from_raw(demux) }));
    }
}

/// The out-of-band parameter sets from STREAM_INFO, prepended to every keyframe. Also classifies the
/// stream as HEVC or H.264.
///
/// # Safety
/// `demux` as for [`ripcord_stream_demux_free`]; `data` valid for `length` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_stream_demux_set_video_header(
    demux: *mut RipcordStreamDemux,
    data: *const u8,
    length: usize,
) -> RipcordStatus {
    with_handle(demux, |d| {
        // SAFETY: forwarded from this function's contract.
        let Some(data) = (unsafe { bytes(data, length) }) else {
            return RipcordStatus::InvalidArgument;
        };
        d.inner.set_video_header(data);
        RipcordStatus::Ok
    })
}

struct FfiSink<'a>(&'a RipcordDemuxSink);

impl DemuxSink for FfiSink<'_> {
    fn video_frame(&mut self, data: &[u8], is_keyframe: bool) {
        if let Some(f) = self.0.video_frame {
            f(self.0.user, data.as_ptr(), data.len(), is_keyframe);
        }
    }
    fn audio_frame(&mut self, data: &[u8]) {
        if let Some(f) = self.0.audio_frame {
            f(self.0.user, data.as_ptr(), data.len());
        }
    }
    fn video_loss(&mut self, first: u16, last: u16) {
        if let Some(f) = self.0.video_loss {
            f(self.0.user, first, last);
        }
    }
    fn control_packet(&mut self, header: &StreamHeader, data: &[u8]) {
        if let Some(f) = self.0.control_packet {
            let h = RipcordStreamHeader::from(header);
            f(self.0.user, &h, data.as_ptr(), data.len());
        }
    }
}

/// Feeds one whole UDP payload from the stream port. Events are delivered through `sink` before this
/// returns. A packet that is malformed or fails authentication is dropped and still returns
/// [`RipcordStatus::Ok`]; the counters say how many.
///
/// # Safety
/// `demux` as for [`ripcord_stream_demux_free`]; `packet` valid for `length` bytes; `sink` valid for
/// the call, with callbacks that do not unwind.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_stream_demux_ingest(
    demux: *mut RipcordStreamDemux,
    packet: *const u8,
    length: usize,
    sink: *const RipcordDemuxSink,
) -> RipcordStatus {
    with_handle(demux, |d| {
        // SAFETY: forwarded from this function's contract.
        let (Some(packet), Some(sink)) = (unsafe { bytes(packet, length) }, unsafe { sink.as_ref() }) else {
            return RipcordStatus::InvalidArgument;
        };
        d.inner.ingest(packet, &mut FfiSink(sink));
        RipcordStatus::Ok
    })
}

/// Reads and resets the wire unit counts since the last call, for congestion feedback.
///
/// # Safety
/// `demux` as for [`ripcord_stream_demux_free`]; `received` and `lost` valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_stream_demux_take_packet_stats(
    demux: *mut RipcordStreamDemux,
    received: *mut u64,
    lost: *mut u64,
) -> RipcordStatus {
    with_handle(demux, |d| {
        if received.is_null() || lost.is_null() {
            return RipcordStatus::InvalidArgument;
        }
        let (r, l) = d.inner.take_packet_stats();
        // SAFETY: non-null and valid for writes per the contract.
        unsafe {
            received.write(r);
            lost.write(l);
        }
        RipcordStatus::Ok
    })
}

/// # Safety
/// `demux` as for [`ripcord_stream_demux_free`]; `out` valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_stream_demux_counters(
    demux: *mut RipcordStreamDemux,
    out: *mut RipcordDemuxCounters,
) -> RipcordStatus {
    with_handle(demux, |d| {
        if out.is_null() {
            return RipcordStatus::InvalidArgument;
        }
        let c = d.inner.counters();
        // SAFETY: non-null and valid for writes per the contract.
        unsafe {
            out.write(RipcordDemuxCounters {
                auth_failures: c.auth_failures,
                frames_too_many_units: c.frames_too_many_units,
            })
        };
        RipcordStatus::Ok
    })
}

/// # Safety
/// `demux` as for [`ripcord_stream_demux_free`]; `out` valid for a write.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_stream_demux_video_is_hevc(
    demux: *mut RipcordStreamDemux,
    out: *mut bool,
) -> RipcordStatus {
    with_handle(demux, |d| {
        if out.is_null() {
            return RipcordStatus::InvalidArgument;
        }
        // SAFETY: non-null and valid for a write per the contract.
        unsafe { out.write(d.inner.video_is_hevc()) };
        RipcordStatus::Ok
    })
}

// ---- test support ----

/// Results of [`ripcord_kat_run`].
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct RipcordKatResult {
    pub passed: u64,
    pub failed: u64,
    pub deferred: u64,
}

/// Runs a known-answer vector file (the text of one `.kat` file) through the engine, with key agreement
/// on `backend`, or on the engine's RustCrypto backend when `backend` is null. This is how a host checks
/// its own platform's backend against the .NET vectors. [`RipcordStatus::Ok`] only if every line passed
/// and at least one was checked; failures print to stderr. Test builds only (`test-support`).
///
/// # Safety
/// `text` valid for `length` bytes; `backend` null or valid, with callbacks that do not unwind; `out`
/// null or valid for a write.
#[cfg(feature = "test-support")]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_kat_run(
    text: *const u8,
    length: usize,
    backend: *const RipcordEcdhBackend,
    out: *mut RipcordKatResult,
) -> RipcordStatus {
    guard(RipcordStatus::Panicked, || {
        // SAFETY: forwarded from this function's contract.
        let Some(text) = (unsafe { bytes(text, length) }).and_then(|t| std::str::from_utf8(t).ok()) else {
            return RipcordStatus::InvalidArgument;
        };
        // SAFETY: as above.
        let report = match unsafe { backend.as_ref() } {
            Some(b) => ripcord_kat::run_with(text, &HostEcdh(b)),
            None => ripcord_kat::run(text),
        };
        for failure in &report.failures {
            eprintln!("FAIL {failure}");
        }
        if !out.is_null() {
            let result = RipcordKatResult {
                passed: report.passed as u64,
                failed: report.failures.len() as u64,
                deferred: report.deferred.values().sum::<usize>() as u64,
            };
            // SAFETY: non-null and valid for a write per the contract.
            unsafe { out.write(result) };
        }
        if report.ok() { RipcordStatus::Ok } else { RipcordStatus::Rejected }
    })
}

/// The scripted 9303 console (`ripcord_proto::testing::scripted_console`), for host test suites: the same
/// scenarios in every client's tests (docs/engine-plan.md, "What each client must agree on"). Opaque.
#[cfg(feature = "test-support")]
pub struct RipcordScriptedConsole {
    inner: ripcord_proto::testing::scripted_console::ScriptedConsole,
}

/// What the scripted console has seen.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct RipcordScriptedConsoleCounts {
    pub inits: u64,
    pub hellos: u64,
    pub closes_received: u64,
    pub requests: u64,
    pub ctrl_open: bool,
    pub frames: u64,
}

/// Receives each datagram the scripted console sends, borrowed for the call.
pub type RipcordEmitFn = Option<extern "C" fn(user: *mut c_void, datagram: *const u8, length: usize)>;

#[cfg(feature = "test-support")]
fn emit_all(datagrams: Vec<Vec<u8>>, emit: RipcordEmitFn, user: *mut c_void) {
    if let Some(f) = emit {
        for d in &datagrams {
            f(user, d.as_ptr(), d.len());
        }
    }
}

#[cfg(feature = "test-support")]
#[unsafe(no_mangle)]
pub extern "C" fn ripcord_scripted_console_new() -> *mut RipcordScriptedConsole {
    guard(std::ptr::null_mut(), || {
        Box::into_raw(Box::new(RipcordScriptedConsole { inner: Default::default() }))
    })
}

/// # Safety
/// `console` null or from [`ripcord_scripted_console_new`], not used after this call.
#[cfg(feature = "test-support")]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_scripted_console_free(console: *mut RipcordScriptedConsole) {
    if !console.is_null() {
        // SAFETY: per the contract.
        guard((), || drop(unsafe { Box::from_raw(console) }));
    }
}

/// Configures the console: `init_reply` answers /sess/init and `other_reply` anything else that is not
/// /sess/ctrl (either null for a plain 200); `close_before_answering` tears down such requests instead.
///
/// # Safety
/// `console` live; each reply null or valid for its length.
#[cfg(feature = "test-support")]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_scripted_console_configure(
    console: *mut RipcordScriptedConsole,
    init_reply: *const u8,
    init_reply_length: usize,
    other_reply: *const u8,
    other_reply_length: usize,
    close_before_answering: bool,
) -> RipcordStatus {
    guard(RipcordStatus::Panicked, || {
        // SAFETY: per the contract.
        let Some(c) = (unsafe { console.as_mut() }) else { return RipcordStatus::InvalidArgument };
        // SAFETY: per the contract.
        let reply = |p: *const u8, n: usize| {
            (!p.is_null()).then(|| unsafe { bytes(p, n) }.map(<[u8]>::to_vec)).flatten()
        };
        c.inner.init_reply = reply(init_reply, init_reply_length);
        c.inner.other_reply = reply(other_reply, other_reply_length);
        c.inner.close_before_answering = close_before_answering;
        RipcordStatus::Ok
    })
}

/// Feeds the console one datagram from the client; what it answers goes to `emit`.
///
/// # Safety
/// `console` live; `datagram` valid for `length`; `emit` does not unwind.
#[cfg(feature = "test-support")]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_scripted_console_on_datagram(
    console: *mut RipcordScriptedConsole,
    datagram: *const u8,
    length: usize,
    emit: RipcordEmitFn,
    user: *mut c_void,
) -> RipcordStatus {
    guard(RipcordStatus::Panicked, || {
        // SAFETY: per the contract.
        let (Some(c), Some(d)) = (unsafe { console.as_mut() }, unsafe { bytes(datagram, length) }) else {
            return RipcordStatus::InvalidArgument;
        };
        emit_all(c.inner.on_datagram(d), emit, user);
        RipcordStatus::Ok
    })
}

/// A console-originated payload on the open connection (a control frame, say), to `emit`.
///
/// # Safety
/// As for [`ripcord_scripted_console_on_datagram`].
#[cfg(feature = "test-support")]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_scripted_console_push(
    console: *mut RipcordScriptedConsole,
    payload: *const u8,
    length: usize,
    emit: RipcordEmitFn,
    user: *mut c_void,
) -> RipcordStatus {
    guard(RipcordStatus::Panicked, || {
        // SAFETY: per the contract.
        let (Some(c), Some(p)) = (unsafe { console.as_mut() }, unsafe { bytes(payload, length) }) else {
            return RipcordStatus::InvalidArgument;
        };
        match c.inner.push(p) {
            Some(d) => {
                emit_all(vec![d], emit, user);
                RipcordStatus::Ok
            }
            None => RipcordStatus::Rejected,
        }
    })
}

/// # Safety
/// `console` live; `out` valid for a write.
#[cfg(feature = "test-support")]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_scripted_console_counts(
    console: *const RipcordScriptedConsole,
    out: *mut RipcordScriptedConsoleCounts,
) -> RipcordStatus {
    guard(RipcordStatus::Panicked, || {
        // SAFETY: per the contract.
        let Some(c) = (unsafe { console.as_ref() }) else { return RipcordStatus::InvalidArgument };
        if out.is_null() {
            return RipcordStatus::InvalidArgument;
        }
        let i = &c.inner;
        let counts = RipcordScriptedConsoleCounts {
            inits: i.inits as u64,
            hellos: i.hellos as u64,
            closes_received: i.closes_received as u64,
            requests: i.request_count as u64,
            ctrl_open: i.ctrl_open,
            frames: i.frames.len() as u64,
        };
        // SAFETY: non-null and valid per the contract.
        unsafe { out.write(counts) };
        RipcordStatus::Ok
    })
}

/// Control frame `index` as the console recorded it, borrowed until the next call on the console.
///
/// # Safety
/// `console` live; `data` and `length` valid for writes.
#[cfg(feature = "test-support")]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_scripted_console_frame(
    console: *const RipcordScriptedConsole,
    index: usize,
    data: *mut *const u8,
    length: *mut usize,
) -> RipcordStatus {
    guard(RipcordStatus::Panicked, || {
        // SAFETY: per the contract.
        let Some(c) = (unsafe { console.as_ref() }) else { return RipcordStatus::InvalidArgument };
        let Some(frame) = c.inner.frames.get(index) else { return RipcordStatus::Rejected };
        if data.is_null() || length.is_null() {
            return RipcordStatus::InvalidArgument;
        }
        // SAFETY: non-null and valid per the contract.
        unsafe {
            data.write(frame.as_ptr());
            length.write(frame.len());
        }
        RipcordStatus::Ok
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ptr::{null, null_mut};

    #[test]
    fn layout_answers() {
        assert_eq!(ripcord_api_version(), RIPCORD_API_VERSION);
        assert_eq!(ripcord_struct_size(RipcordStructId::StreamHeader as u32), 20);
        assert_eq!(ripcord_struct_size(RipcordStructId::DemuxSink as u32), 5 * size_of::<usize>());
        assert_eq!(ripcord_struct_size(RipcordStructId::DemuxCounters as u32), 16);
        assert_eq!(ripcord_struct_size(RipcordStructId::EcdhBackend as u32), 3 * size_of::<usize>());
        assert_eq!(ripcord_struct_size(0), 0);
        assert_eq!(ripcord_struct_size(999), 0);
    }

    #[test]
    fn null_arguments_are_refused_not_dereferenced() {
        unsafe {
            assert!(ripcord_packet_crypto_new(null(), null()).is_null());
            assert_eq!(
                ripcord_packet_crypto_verify(null_mut(), 0, null(), 0, 10, false),
                RipcordStatus::InvalidArgument
            );
            let key = [0u8; 16];
            let c = ripcord_packet_crypto_new(key.as_ptr(), key.as_ptr());
            assert_eq!(
                ripcord_packet_crypto_verify(c, 0, null(), 30, 10, false),
                RipcordStatus::InvalidArgument
            );
            assert_eq!(
                ripcord_packet_crypto_verify(c, 0, null(), 0, 10, false),
                RipcordStatus::Rejected,
                "an empty packet has no tag"
            );
            ripcord_packet_crypto_free(c);
            ripcord_packet_crypto_free(null_mut());

            let d = ripcord_stream_demux_new_passthrough();
            assert_eq!(
                ripcord_stream_demux_ingest(d, [0u8; 4].as_ptr(), 4, null()),
                RipcordStatus::InvalidArgument
            );
            assert_eq!(
                ripcord_stream_demux_take_packet_stats(d, null_mut(), null_mut()),
                RipcordStatus::InvalidArgument
            );
            ripcord_stream_demux_free(d);
        }
    }

    #[test]
    fn seal_verify_and_decrypt_through_the_abi() {
        unsafe {
            let (key, iv) = ([7u8; 16], [9u8; 16]);
            let tx = ripcord_packet_crypto_new(key.as_ptr(), iv.as_ptr());
            let rx = ripcord_packet_crypto_new(key.as_ptr(), iv.as_ptr());
            let original: Vec<u8> = (0..200u8).collect();
            let mut p = original.clone();
            assert_eq!(
                ripcord_packet_crypto_crypt_payload(tx, 4_096, p[21..].as_mut_ptr(), p.len() - 21),
                RipcordStatus::Ok
            );
            assert_eq!(
                ripcord_packet_crypto_seal(tx, 4_096, p.as_mut_ptr(), p.len(), 10, false),
                RipcordStatus::Ok
            );
            assert_eq!(
                ripcord_packet_crypto_verify(rx, 4_096, p.as_ptr(), p.len(), 10, false),
                RipcordStatus::Ok
            );
            assert_eq!(
                ripcord_packet_crypto_crypt_payload(rx, 4_096, p[21..].as_mut_ptr(), p.len() - 21),
                RipcordStatus::Ok
            );
            assert_eq!(p[21..], original[21..]);
            p[50] ^= 1;
            assert_eq!(
                ripcord_packet_crypto_verify(rx, 4_096, p.as_ptr(), p.len(), 10, false),
                RipcordStatus::Rejected
            );
            ripcord_packet_crypto_free(tx);
            ripcord_packet_crypto_free(rx);
        }
    }

    #[derive(Default)]
    struct Seen {
        frames: Vec<Vec<u8>>,
        losses: Vec<(u16, u16)>,
        control_types: Vec<u8>,
    }

    extern "C" fn on_frame(user: *mut c_void, data: *const u8, length: usize, _key: bool) {
        let seen = unsafe { &mut *(user as *mut Seen) };
        seen.frames.push(unsafe { std::slice::from_raw_parts(data, length) }.to_vec());
    }
    extern "C" fn on_loss(user: *mut c_void, first: u16, last: u16) {
        unsafe { &mut *(user as *mut Seen) }.losses.push((first, last));
    }
    extern "C" fn on_control(user: *mut c_void, header: *const RipcordStreamHeader, _: *const u8, _: usize) {
        unsafe { &mut *(user as *mut Seen) }.control_types.push(unsafe { (*header).packet_type });
    }

    #[test]
    fn demux_reports_through_callbacks() {
        let mut seen = Seen::default();
        let sink = RipcordDemuxSink {
            user: &mut seen as *mut Seen as *mut c_void,
            video_frame: Some(on_frame),
            audio_frame: None,
            video_loss: Some(on_loss),
            control_packet: Some(on_control),
        };
        let packet = |frame: u16| {
            let h = StreamHeader { packet_type: 2, frame_index: frame, total_units: 1, ..Default::default() };
            let mut p = h.build().unwrap().to_vec();
            p.extend_from_slice(&[0, 0, 0, 0, 0, 0xab, 0xcd]);
            p
        };
        unsafe {
            let d = ripcord_stream_demux_new_passthrough();
            for f in [0u16, 2] {
                let p = packet(f);
                assert_eq!(ripcord_stream_demux_ingest(d, p.as_ptr(), p.len(), &sink), RipcordStatus::Ok);
            }
            let mut control = [0x05u8; 24];
            control[0] = 0x05;
            assert_eq!(
                ripcord_stream_demux_ingest(d, control.as_ptr(), control.len(), &sink),
                RipcordStatus::Ok
            );
            let (mut r, mut l) = (0u64, 0u64);
            assert_eq!(ripcord_stream_demux_take_packet_stats(d, &mut r, &mut l), RipcordStatus::Ok);
            assert_eq!((r, l), (1, 0));
            ripcord_stream_demux_free(d);
        }
        assert_eq!(seen.frames, [vec![0xab, 0xcd]]);
        assert_eq!(seen.losses, [(1, 1)]);
        assert_eq!(seen.control_types, [5]);
    }

    struct Bomb(bool);
    impl Handle for Bomb {
        fn poisoned(&mut self) -> &mut bool {
            &mut self.0
        }
    }

    #[test]
    fn a_panic_poisons_the_handle_and_goes_no_further() {
        let mut h = Bomb(false);
        let prev = std::panic::take_hook();
        std::panic::set_hook(Box::new(|_| {}));
        let status = with_handle(&mut h, |_| panic!("a bug"));
        std::panic::set_hook(prev);
        assert_eq!(status, RipcordStatus::Panicked);
        assert_eq!(with_handle(&mut h, |_| RipcordStatus::Ok), RipcordStatus::Poisoned);
        assert_eq!(guard(7, || panic!("constructor bug")), 7);
    }

    extern "C" fn rust_public_key(
        _: *mut c_void,
        curve: u32,
        k: *const u8,
        n: usize,
        out: *mut u8,
        cap: usize,
        len: *mut usize,
    ) -> bool {
        use ripcord_proto::crypto::ecdh::RustCryptoEcdh;
        let curve = if curve == RIPCORD_CURVE_P256 { Curve::P256 } else { Curve::P521 };
        let key = unsafe { std::slice::from_raw_parts(k, n) };
        match RustCryptoEcdh.public_key(curve, key) {
            Some(p) if p.len() <= cap => unsafe {
                std::ptr::copy_nonoverlapping(p.as_ptr(), out, p.len());
                *len = p.len();
                true
            },
            _ => false,
        }
    }

    #[test]
    fn a_host_backend_is_called_through_the_table() {
        let backend =
            RipcordEcdhBackend { user: null_mut(), public_key: Some(rust_public_key), shared_secret: None };
        let host = HostEcdh(&backend);
        let mut private = [0u8; 32];
        private[31] = 7;
        assert_eq!(host.public_key(Curve::P256, &private).map(|p| p.len()), Some(65));
        assert_eq!(
            host.shared_secret(Curve::P256, &private, &[4; 65]),
            None,
            "a missing callback is a refusal"
        );
    }

    #[cfg(feature = "test-support")]
    #[test]
    fn the_scripted_console_answers_through_the_abi() {
        use ripcord_proto::dgram::wire;
        extern "C" fn collect(user: *mut c_void, d: *const u8, n: usize) {
            unsafe { &mut *(user as *mut Vec<Vec<u8>>) }
                .push(unsafe { std::slice::from_raw_parts(d, n) }.to_vec());
        }
        let mut out: Vec<Vec<u8>> = Vec::new();
        let user = &mut out as *mut Vec<Vec<u8>> as *mut c_void;
        unsafe {
            let c = ripcord_scripted_console_new();
            let init =
                wire::Prelude { kind: wire::PRELUDE_INIT, tag_pair: 0x0001_0002, ..Default::default() }
                    .write();
            assert_eq!(
                ripcord_scripted_console_on_datagram(c, init.as_ptr(), init.len(), Some(collect), user),
                RipcordStatus::Ok
            );
            let mut counts = RipcordScriptedConsoleCounts::default();
            assert_eq!(ripcord_scripted_console_counts(c, &mut counts), RipcordStatus::Ok);
            assert_eq!(counts.inits, 1);
            ripcord_scripted_console_free(c);
        }
        assert_eq!(out.len(), 2, "an Init answer and a CookieEcho");
    }
}
