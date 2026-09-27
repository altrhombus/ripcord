//! Everything around a session that the Mac takes from the C core today: discovery and wake (the
//! datagrams; the host owns those sockets), the account-id normaliser, the account seed, and
//! registration on both routes. Registration returns a [`RipcordPairingRecord`]; saving it is the host's.

use std::ffi::{CStr, c_char, c_void};

use ripcord_net::RegisterError;
use ripcord_net::pairing::{self, PinError, PinParams};
use ripcord_proto::discovery;
use ripcord_proto::halyard::account_seed;
use ripcord_proto::sess::account_id;
use ripcord_proto::sess::regist::{PairingRecord, RegistError};

use super::{RipcordClient, RipcordRandom, RipcordStatus, bytes, bytes_mut, guard, with_handle};

/// Copies `text` NUL-terminated into a C buffer, truncating to fit. `false` for no room at all.
fn write_text(text: &str, out: &mut [u8]) -> bool {
    let Some(room) = out.len().checked_sub(1) else { return false };
    let n = text.len().min(room);
    out[..n].copy_from_slice(&text.as_bytes()[..n]);
    out[n] = 0;
    true
}

/// # Safety
/// `text` null or NUL-terminated.
unsafe fn c_text(text: *const c_char) -> Option<String> {
    // SAFETY: forwarded.
    (!text.is_null()).then(|| unsafe { CStr::from_ptr(text) }.to_string_lossy().into_owned())
}

// ---- discovery and wake ----

/// What a console says about itself in a SRCH reply. Strings NUL-terminated, truncated to fit.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct RipcordDiscoveredConsole {
    pub host_id: [u8; 64],
    pub host_type: [u8; 16],
    pub host_name: [u8; 128],
    pub system_version: [u8; 32],
    /// host-request-port; 0 when absent.
    pub request_port: u16,
    /// "HTTP/1.1 200" is awake; anything else (620 Server Standby in practice) is resting.
    pub is_awake: bool,
}

/// The SRCH probe for a family, and the port it goes to (9302 PS5, 987 PS4). Writes the length.
///
/// # Safety
/// `out` valid for `capacity` bytes; the other out pointers valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_discovery_probe(
    is_ps5: bool,
    out: *mut u8,
    capacity: usize,
    out_length: *mut usize,
    out_port: *mut u16,
) -> RipcordStatus {
    guard(RipcordStatus::Panicked, || {
        let profile = if is_ps5 { &discovery::PS5 } else { &discovery::PS4 };
        let probe = discovery::probe(profile);
        // SAFETY: forwarded.
        let Some(out) = (unsafe { bytes_mut(out, capacity) }) else { return RipcordStatus::InvalidArgument };
        if out_length.is_null() || out_port.is_null() || probe.len() > out.len() {
            return RipcordStatus::InvalidArgument;
        }
        out[..probe.len()].copy_from_slice(&probe);
        // SAFETY: non-null, checked above.
        unsafe {
            out_length.write(probe.len());
            out_port.write(profile.port);
        }
        RipcordStatus::Ok
    })
}

/// Parses a SRCH reply. `Rejected` for anything that is not one.
///
/// # Safety
/// `data` valid for `length` bytes; `out` valid for a write.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_discovery_parse(
    data: *const u8,
    length: usize,
    out: *mut RipcordDiscoveredConsole,
) -> RipcordStatus {
    guard(RipcordStatus::Panicked, || {
        // SAFETY: forwarded.
        let Some(data) = (unsafe { bytes(data, length) }) else { return RipcordStatus::InvalidArgument };
        if out.is_null() {
            return RipcordStatus::InvalidArgument;
        }
        let Some(c) = discovery::parse_reply(data) else { return RipcordStatus::Rejected };
        let mut r = RipcordDiscoveredConsole {
            host_id: [0; 64],
            host_type: [0; 16],
            host_name: [0; 128],
            system_version: [0; 32],
            request_port: c.request_port,
            is_awake: c.is_awake,
        };
        write_text(&c.host_id, &mut r.host_id);
        write_text(&c.host_type, &mut r.host_type);
        write_text(&c.host_name, &mut r.host_name);
        write_text(&c.system_version, &mut r.system_version);
        // SAFETY: non-null, checked above.
        unsafe { out.write(r) };
        RipcordStatus::Ok
    })
}

/// The wake datagram for a pairing's registration key (the record's bytes, which are hex text), the port
/// it goes to, and the source port to send it from where it can be bound (a resting console may honour
/// only the vendor's; fall back to an ephemeral port rather than not waking). `Rejected` for a key that is
/// not a 32-bit hex number.
///
/// # Safety
/// `registration_key` valid for `key_length` bytes; `out` for `capacity`; the others for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_wake_payload(
    is_ps5: bool,
    registration_key: *const u8,
    key_length: usize,
    out: *mut u8,
    capacity: usize,
    out_length: *mut usize,
    out_port: *mut u16,
    out_source_port: *mut u16,
) -> RipcordStatus {
    guard(RipcordStatus::Panicked, || {
        // SAFETY: forwarded.
        let args = unsafe { (bytes(registration_key, key_length), bytes_mut(out, capacity)) };
        let (Some(key), Some(out)) = args else {
            return RipcordStatus::InvalidArgument;
        };
        if out_length.is_null() || out_port.is_null() || out_source_port.is_null() {
            return RipcordStatus::InvalidArgument;
        }
        let Some(credential) = discovery::wake_credential(key) else { return RipcordStatus::Rejected };
        let profile = if is_ps5 { &discovery::PS5 } else { &discovery::PS4 };
        let payload = discovery::wake(profile, &credential);
        if payload.len() > out.len() {
            return RipcordStatus::InvalidArgument;
        }
        out[..payload.len()].copy_from_slice(&payload);
        // SAFETY: non-null, checked above.
        unsafe {
            out_length.write(payload.len());
            out_port.write(profile.port);
            out_source_port.write(profile.wake_source_port);
        }
        RipcordStatus::Ok
    })
}

// ---- the account id and the account seed ----

pub const RIPCORD_ACCOUNT_ID_OK: u32 = 0;
pub const RIPCORD_ACCOUNT_ID_EMPTY: u32 = 1;
/// Recognised; its byte order is not ours to guess.
pub const RIPCORD_ACCOUNT_ID_BASE64: u32 = 2;
pub const RIPCORD_ACCOUNT_ID_UNREADABLE: u32 = 3;

/// Reads a typed PSN account id and writes it back as decimal into `out` (21 bytes is enough), or says
/// why not: a `RIPCORD_ACCOUNT_ID_*` value. `reason`, if not null, gets the refusal in words a person can
/// act on.
///
/// # Safety
/// `input` null or NUL-terminated; `out` valid for `capacity` bytes; `reason` null or valid for
/// `reason_capacity` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_account_id_normalise(
    input: *const c_char,
    out: *mut u8,
    capacity: usize,
    reason: *mut u8,
    reason_capacity: usize,
) -> u32 {
    guard(RIPCORD_ACCOUNT_ID_UNREADABLE, || {
        // SAFETY: forwarded.
        let Some(out) = (unsafe { bytes_mut(out, capacity) }) else { return RIPCORD_ACCOUNT_ID_UNREADABLE };
        // SAFETY: forwarded.
        let text = unsafe { c_text(input) }.unwrap_or_default();
        let (code, written) = match account_id::normalise(&text) {
            Ok(decimal) if decimal.len() < out.len() => (RIPCORD_ACCOUNT_ID_OK, decimal),
            Ok(_) => (RIPCORD_ACCOUNT_ID_UNREADABLE, String::new()),
            Err(refusal) => {
                if !reason.is_null() {
                    // SAFETY: non-null and valid per the contract.
                    if let Some(r) = unsafe { bytes_mut(reason, reason_capacity) } {
                        write_text(refusal.text(), r);
                    }
                }
                let code = match refusal {
                    account_id::Refusal::Empty => RIPCORD_ACCOUNT_ID_EMPTY,
                    account_id::Refusal::Base64 => RIPCORD_ACCOUNT_ID_BASE64,
                    account_id::Refusal::Unreadable => RIPCORD_ACCOUNT_ID_UNREADABLE,
                };
                (code, String::new())
            }
        };
        write_text(&written, out);
        code
    })
}

/// Fresh key material for the account route's `commands` call: two independent draws from the host's
/// CSPRNG, as .NET's GenerateEphemeralKeyMaterial makes.
///
/// # Safety
/// `random` valid; `data1` and `data2` valid for 16 bytes each.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_account_key_material(
    random: *const RipcordRandom,
    data1: *mut u8,
    data2: *mut u8,
) -> RipcordStatus {
    guard(RipcordStatus::Panicked, || {
        // SAFETY: forwarded.
        let args = unsafe { (random.as_ref(), bytes_mut(data1, 16), bytes_mut(data2, 16)) };
        let (Some(random), Some(d1), Some(d2)) = args else {
            return RipcordStatus::InvalidArgument;
        };
        let Some(fill) = random.fill else { return RipcordStatus::InvalidArgument };
        if !fill(random.user, d1.as_mut_ptr(), 16) || !fill(random.user, d2.as_mut_ptr(), 16) {
            d1.fill(0);
            d2.fill(0);
            return RipcordStatus::Rejected;
        }
        RipcordStatus::Ok
    })
}

/// The registration seed from the console's published customData1 (double base64), decrypted under the
/// data1/data2 sent in `commands`. `Rejected` for a malformed value; a wrong data1/data2 is not
/// detectable here and shows up as the registration's BAD_RECORD.
///
/// # Safety
/// `data1`, `data2` and `out_seed` valid for 16 bytes; `custom_data1` for `length`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_account_seed_recover(
    is_ps5: bool,
    data1: *const u8,
    data2: *const u8,
    custom_data1: *const u8,
    length: usize,
    out_seed: *mut u8,
) -> RipcordStatus {
    guard(RipcordStatus::Panicked, || {
        // SAFETY: forwarded.
        let args = unsafe {
            (bytes(data1, 16), bytes(data2, 16), bytes(custom_data1, length), bytes_mut(out_seed, 16))
        };
        let (Some(d1), Some(d2), Some(text), Some(out)) = args else {
            return RipcordStatus::InvalidArgument;
        };
        let (d1, d2): ([u8; 16], [u8; 16]) = (d1.try_into().expect("16"), d2.try_into().expect("16"));
        match account_seed::recover_custom_data1(is_ps5, &d1, &d2, text) {
            Some(seed) => {
                out.copy_from_slice(&seed);
                RipcordStatus::Ok
            }
            None => RipcordStatus::Rejected,
        }
    })
}

// ---- registration ----

/// What a registration produced. The host keeps it (a pairing file, the Keychain); the engine does not.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct RipcordPairingRecord {
    pub registration_key: [u8; 64],
    pub registration_key_length: usize,
    pub companion: [u8; 16],
    pub key_type: i32,
    /// The family whose field the console used.
    pub is_ps5: bool,
}

pub const RIPCORD_REGIST_OK: u32 = 0;
/// A missing account id or client address, or no tables for the family.
pub const RIPCORD_REGIST_BAD_PARAMS: u32 = 1;
pub const RIPCORD_REGIST_NO_RANDOM: u32 = 2;
/// TCP 9295 refused or unreachable.
pub const RIPCORD_REGIST_CONNECT: u32 = 3;
pub const RIPCORD_REGIST_SEND: u32 = 4;
/// Connected, then silence.
pub const RIPCORD_REGIST_NO_REPLY: u32 = 5;
pub const RIPCORD_REGIST_MALFORMED: u32 = 6;
/// A non-2xx answer; `http_status` and `console_reason` say more.
pub const RIPCORD_REGIST_REFUSED: u32 = 7;
/// 2xx, but the body is not a pairing record: a wrong PIN, or a wrong seed.
pub const RIPCORD_REGIST_BAD_RECORD: u32 = 8;
/// Rendezvous: the 9303 exchange failed, or it was not between begin and connect.
pub const RIPCORD_REGIST_TRANSPORT: u32 = 9;

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct RipcordRegistResult {
    /// `RIPCORD_REGIST_*`.
    pub status: u32,
    pub saw_arm_reply: bool,
    pub http_status: i32,
    /// RP-Application-Reason, NUL-terminated, when the console gave one.
    pub console_reason: [u8; 32],
    /// Valid only when `status` is OK.
    pub record: RipcordPairingRecord,
}

fn result_of(
    saw_arm_reply: bool,
    r: Result<PairingRecord, (u32, i32, Option<String>)>,
) -> RipcordRegistResult {
    let mut out = RipcordRegistResult {
        status: RIPCORD_REGIST_OK,
        saw_arm_reply,
        http_status: 0,
        console_reason: [0; 32],
        record: RipcordPairingRecord {
            registration_key: [0; 64],
            registration_key_length: 0,
            companion: [0; 16],
            key_type: 0,
            is_ps5: false,
        },
    };
    match r {
        Ok(rec) => {
            let n = rec.registration_key.len().min(64);
            out.record.registration_key[..n].copy_from_slice(&rec.registration_key[..n]);
            out.record.registration_key_length = n;
            out.record.companion = rec.companion;
            out.record.key_type = rec.key_type;
            out.record.is_ps5 = rec.is_ps5;
            out.http_status = 200;
        }
        Err((status, http, reason)) => {
            out.status = status;
            out.http_status = http;
            if let Some(r) = reason {
                write_text(&r, &mut out.console_reason);
            }
        }
    }
    out
}

fn regist_error(e: RegistError) -> (u32, i32, Option<String>) {
    match e {
        RegistError::BadParams => (RIPCORD_REGIST_BAD_PARAMS, 0, None),
        RegistError::Malformed => (RIPCORD_REGIST_MALFORMED, 0, None),
        RegistError::Refused { status, reason } => (RIPCORD_REGIST_REFUSED, status, reason),
        RegistError::BadRecord => (RIPCORD_REGIST_BAD_RECORD, 200, None),
    }
}

/// Registers with the PIN the console shows, over TCP 9295. Blocking: about two seconds arming the
/// console's listener, then the exchange. `console` is 4 bytes; `account_id` and `client_ip` (this
/// machine's address toward the console) NUL-terminated. `port` 0 is 9295; only a test changes it, and
/// `no_arm_broadcast` keeps a loopback test on the machine.
///
/// # Safety
/// Every pointer valid per the above; `random` valid; `out` valid for a write.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_regist_pin(
    console: *const u8,
    is_ps5: bool,
    account_id: *const c_char,
    passcode: u32,
    client_ip: *const c_char,
    port: u16,
    no_arm_broadcast: bool,
    random: *const RipcordRandom,
    out: *mut RipcordRegistResult,
) -> RipcordStatus {
    guard(RipcordStatus::Panicked, || {
        // SAFETY: forwarded.
        let args = unsafe { (bytes(console, 4), c_text(account_id), c_text(client_ip), random.as_ref()) };
        let (Some(console), Some(account_id), Some(client_ip), Some(random)) = args else {
            return RipcordStatus::InvalidArgument;
        };
        let (Some(fill), false) = (random.fill, out.is_null()) else { return RipcordStatus::InvalidArgument };
        let mut params =
            PinParams::new(console.try_into().expect("4"), is_ps5, &account_id, passcode, &client_ip);
        if port != 0 {
            params.port = port;
        }
        params.arm_broadcast = !no_arm_broadcast;
        let user = random.user as usize;
        let refused = std::cell::Cell::new(false);
        let mut source: ripcord_proto::RandomSource = Box::new(move |b: &mut [u8]| {
            if !fill(user as *mut c_void, b.as_mut_ptr(), b.len()) {
                // A refused draw must never become key material: zero it and fail the registration below.
                b.fill(0);
                panic!("the host's CSPRNG refused");
            }
        });
        let outcome = match std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            pairing::register_pin(&params, &mut source)
        })) {
            Ok(o) => o,
            Err(_) => {
                refused.set(true);
                pairing::PinOutcome { saw_arm_reply: false, result: Err(PinError::Send) }
            }
        };
        let r = if refused.get() {
            Err((RIPCORD_REGIST_NO_RANDOM, 0, None))
        } else {
            outcome.result.map_err(|e| match e {
                PinError::Connect => (RIPCORD_REGIST_CONNECT, 0, None),
                PinError::Send => (RIPCORD_REGIST_SEND, 0, None),
                PinError::NoReply => (RIPCORD_REGIST_NO_REPLY, 0, None),
                PinError::Regist(e) => regist_error(e),
            })
        };
        // SAFETY: non-null, checked above.
        unsafe { out.write(result_of(outcome.saw_arm_reply, r)) };
        RipcordStatus::Ok
    })
}

/// Account registration on a rendezvous client's control leg, between begin and connect: the request built
/// from the recovered seed, exchanged, and the answer opened. Blocking, bounded by the 9303 stage deadline.
///
/// # Safety
/// `client` from `ripcord_client_new`; `seed` valid for 16 bytes; the strings NUL-terminated; `out` valid
/// for a write.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_client_rendezvous_register(
    client: *mut RipcordClient,
    is_ps5: bool,
    seed: *const u8,
    account_id: *const c_char,
    client_ip: *const c_char,
    out: *mut RipcordRegistResult,
) -> RipcordStatus {
    // SAFETY: forwarded.
    let args = unsafe { (bytes(seed, 16), c_text(account_id), c_text(client_ip)) };
    let (Some(seed), Some(account_id), Some(client_ip)) = args else {
        return RipcordStatus::InvalidArgument;
    };
    if out.is_null() {
        return RipcordStatus::InvalidArgument;
    }
    let seed: [u8; 16] = seed.try_into().expect("16");
    with_handle(client, |c| {
        let r = c.register(is_ps5, &seed, &account_id, &client_ip).map_err(|e| match e {
            RegisterError::NotReady | RegisterError::Transport(_) => (RIPCORD_REGIST_TRANSPORT, 0, None),
            RegisterError::Regist(e) => regist_error(e),
        });
        // SAFETY: non-null, checked above.
        unsafe { out.write(result_of(false, r)) };
        RipcordStatus::Ok
    })
}

// ---- test support: the account route's console, on loopback ----

/// The scripted 9303 console on a loopback socket, answering the account route's /sess/rgst under a seed.
/// Opaque. Test builds only (`test-support`).
#[cfg(feature = "test-support")]
pub struct RipcordLoopbackDgramConsole {
    inner: ripcord_net::testing::LoopbackDgramConsole,
}

/// What the loopback 9303 console saw. `requests` holds each HTTP request's path as a code, in order: 1
/// /sess/rgst, 2 /sess/init, 3 /sess/ctrl, 0 anything else.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct RipcordDgramConsoleReport {
    pub inits: u32,
    pub rgst_requests: u32,
    pub rgst_field_ok: bool,
    pub request_count: u32,
    pub requests: [u8; 4],
}

/// Starts the console on 127.0.0.1 and writes its port. The ids a client needs to reach it are
/// [`ripcord_loopback_dgram_console_ids`]'s.
///
/// # Safety
/// `seed` and `nonce` valid for 16 bytes; `out_port` valid for a write.
#[cfg(feature = "test-support")]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_loopback_dgram_console_start(
    is_ps5: bool,
    seed: *const u8,
    nonce: *const u8,
    out_port: *mut u16,
) -> *mut RipcordLoopbackDgramConsole {
    guard(std::ptr::null_mut(), || {
        // SAFETY: forwarded.
        let args = unsafe { (bytes(seed, 16), bytes(nonce, 16)) };
        let (Some(seed), Some(nonce), false) = (args.0, args.1, out_port.is_null()) else {
            return std::ptr::null_mut();
        };
        let Ok(inner) = ripcord_net::testing::LoopbackDgramConsole::start(
            is_ps5,
            seed.try_into().expect("16"),
            nonce.try_into().expect("16"),
        ) else {
            return std::ptr::null_mut();
        };
        // SAFETY: non-null, checked above.
        unsafe { out_port.write(inner.port) };
        Box::into_raw(Box::new(RipcordLoopbackDgramConsole { inner }))
    })
}

/// The scripted console's localHashedId and the one it expects of its client, 20 bytes each.
///
/// # Safety
/// Both valid for 20 bytes.
#[cfg(feature = "test-support")]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_loopback_dgram_console_ids(
    console_id: *mut u8,
    client_id: *mut u8,
) -> RipcordStatus {
    use ripcord_proto::testing::scripted_console;
    // SAFETY: forwarded.
    let args = unsafe { (bytes_mut(console_id, 20), bytes_mut(client_id, 20)) };
    let (Some(c), Some(k)) = args else { return RipcordStatus::InvalidArgument };
    c.copy_from_slice(&scripted_console::console_id());
    k.copy_from_slice(&scripted_console::client_id());
    RipcordStatus::Ok
}

/// # Safety
/// `console` from [`ripcord_loopback_dgram_console_start`]; `out` valid for a write.
#[cfg(feature = "test-support")]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_loopback_dgram_console_report(
    console: *const RipcordLoopbackDgramConsole,
    out: *mut RipcordDgramConsoleReport,
) -> RipcordStatus {
    guard(RipcordStatus::Panicked, || {
        // SAFETY: valid per the contract.
        let (Some(c), false) = (unsafe { console.as_ref() }, out.is_null()) else {
            return RipcordStatus::InvalidArgument;
        };
        let r = c.inner.report();
        let mut report = RipcordDgramConsoleReport {
            inits: r.inits as u32,
            rgst_requests: r.rgst_requests as u32,
            rgst_field_ok: r.rgst_field_ok,
            request_count: r.requests.len() as u32,
            requests: [0; 4],
        };
        for (slot, path) in report.requests.iter_mut().zip(&r.requests) {
            *slot = match path.as_str() {
                "/sess/rgst" => 1,
                "/sess/init" => 2,
                "/sess/ctrl" => 3,
                _ => 0,
            };
        }
        // SAFETY: non-null, checked above.
        unsafe { out.write(report) };
        RipcordStatus::Ok
    })
}

/// # Safety
/// `console` null or from [`ripcord_loopback_dgram_console_start`], not used after this call.
#[cfg(feature = "test-support")]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_loopback_dgram_console_stop(console: *mut RipcordLoopbackDgramConsole) {
    guard((), || {
        if !console.is_null() {
            // SAFETY: from Box::into_raw, freed once.
            drop(unsafe { Box::from_raw(console) });
        }
    })
}

/// customData1 as a console publishes it: `seed` sealed under data1/data2, double base64, NUL-terminated
/// into `out`. The console's side, for tests (`test-support`).
///
/// # Safety
/// `data1`, `data2` and `seed` valid for 16 bytes; `out` for `capacity`.
#[cfg(feature = "test-support")]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ripcord_account_seed_seal(
    is_ps5: bool,
    data1: *const u8,
    data2: *const u8,
    seed: *const u8,
    out: *mut u8,
    capacity: usize,
) -> RipcordStatus {
    guard(RipcordStatus::Panicked, || {
        // SAFETY: forwarded.
        let args = unsafe { (bytes(data1, 16), bytes(data2, 16), bytes(seed, 16), bytes_mut(out, capacity)) };
        let (Some(d1), Some(d2), Some(seed), Some(out)) = args else { return RipcordStatus::InvalidArgument };
        let Some(sealed) = account_seed::seal(
            is_ps5,
            &d1.try_into().expect("16"),
            &d2.try_into().expect("16"),
            &seed.try_into().expect("16"),
        ) else {
            return RipcordStatus::Rejected;
        };
        let text = account_seed::encode_custom_data1(&sealed);
        if text.len() >= out.len() {
            return RipcordStatus::InvalidArgument;
        }
        write_text(&text, out);
        RipcordStatus::Ok
    })
}
