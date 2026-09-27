//! The /sess/init and /sess/ctrl requests, and RP-Nonce from the init reply. Ported from the request
//! builders in `libripcord/session/halyard_control_session.c` and `HalyardStreamingSession.cs`;
//! `rendezvous-control.kat` compares both requests byte for byte with the .NET session's.

use super::fields;
use super::http::{self, Request, Response};
use crate::base64;
use crate::halyard::control::ControlField;
use crate::halyard::{VERSION_SELECTOR_PS4, VERSION_SELECTOR_PS5};

/// RP-ConPath: which A/V bring-up the console runs. 1 local, 3 rendezvous, [W] both; 2 unclaimed.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ConnectionPath {
    Local = 1,
    Rendezvous = 3,
}

/// How the requests address the console.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Addressing {
    /// The Host header. [`padded_host`] is .NET's form on both routes and the captured client's; a LAN
    /// console also accepts the plain address, which the C core sends there.
    pub host_header: String,
    /// "Rp-Version" is .NET's spelling on /sess/init, as the captured client sends it; the C core's LAN
    /// path sends "RP-Version", also accepted. /sess/ctrl always sends "RP-Version".
    pub init_version_header: &'static str,
    pub connection_path: ConnectionPath,
}

impl Addressing {
    /// .NET's defaults: the padded Host and "Rp-Version".
    pub fn new(address: [u8; 4], port: u16, connection_path: ConnectionPath) -> Self {
        Self { host_header: padded_host(address, port), init_version_header: "Rp-Version", connection_path }
    }
}

/// The Host value as every captured vendor request writes it: octets right-aligned in three columns, then
/// the port (`192.  0.  2.104:9295`, shown with a documentation address).
pub fn padded_host(a: [u8; 4], port: u16) -> String {
    format!("{:3}.{:3}.{:3}.{:3}:{port}", a[0], a[1], a[2], a[3])
}

/// /sess/init: the registration key in plaintext hex, and nothing else the console does not expect.
pub fn init(is_ps5: bool, addressing: &Addressing, registration_key: &[u8]) -> Vec<u8> {
    let hex: String = registration_key.iter().map(|b| format!("{b:02x}")).collect();
    Request::get(http::path(is_ps5, "init"))
        .header("Host", addressing.host_header.as_str())
        .header("User-Agent", "remoteplay Windows")
        .header("Connection", "close")
        .header("RP-Registkey", hex)
        .header(addressing.init_version_header, http::version(is_ps5))
        .serialize()
}

/// Why /sess/init did not give a nonce.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum InitError {
    Refused {
        status: u16,
        reason: Option<String>,
    },
    /// No RP-Nonce, or not 16 bytes of strict base64. A hard failure, as in C: .NET carries on to an
    /// unauthenticated /sess/ctrl (on the roadmap).
    NoNonce,
}

/// The control-field crypto for this connection, from the /sess/init reply and the pairing's companion.
/// Codec selector 2 is what this project's captures resolve to, confirmed on a console.
pub fn open_init(
    is_ps5: bool,
    reply: &Response<'_>,
    companion: &[u8; 16],
) -> Result<ControlField, InitError> {
    if !reply.is_success() {
        let reason = reply.header("RP-Application-Reason").map(|r| String::from_utf8_lossy(r).into_owned());
        return Err(InitError::Refused { status: reply.status, reason });
    }
    let nonce: [u8; 16] = reply
        .header("RP-Nonce")
        .and_then(base64::decode)
        .and_then(|n| n.try_into().ok())
        .ok_or(InitError::NoNonce)?;
    let selector = if is_ps5 { VERSION_SELECTOR_PS5 } else { VERSION_SELECTOR_PS4 };
    // The KDF can only refuse for a family whose tables are absent.
    ControlField::new(&nonce, companion, 2, selector).ok_or(InitError::NoNonce)
}

/// The five (PS5) or four (PS4) values the encrypted /sess/ctrl fields carry.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CtrlFields<'a> {
    pub registration_key: &'a [u8],
    pub device_id: &'a [u8],
    /// RP-OSType's `Win<major>.<minor>`. The console validates it; the C core defaults to 10.0, and .NET
    /// sends the running Windows version. A host that is not Windows should send 10.0.
    pub os_major: i32,
    pub os_minor: i32,
    pub start_bitrate_kbps: i32,
    pub streaming_type: i32,
}

/// /sess/ctrl, with the encrypted fields at counters 0 to 3, and on PS5 RP-StreamingType at 4. PS4 sends
/// four: its fifth field goes after the passcode as a binary frame, and spending counter 4 here would put
/// the passcode where the console does not expect it. The next counter on the connection is then
/// [`fields::login_pin_counter`].
pub fn ctrl(is_ps5: bool, addressing: &Addressing, field: &ControlField, values: &CtrlFields<'_>) -> Vec<u8> {
    let enc = |counter: u64, mut plain: Vec<u8>| {
        field.encrypt(counter, &mut plain);
        base64::encode(&plain)
    };
    let mut request = Request::get(http::path(is_ps5, "ctrl"))
        .header("Host", addressing.host_header.as_str())
        .header("User-Agent", "remoteplay Windows")
        .header("Connection", "keep-alive")
        .header("RP-Version", http::version(is_ps5))
        .header("RP-ControllerType", "0")
        .header("RP-ClientType", "11")
        .header("RP-ConPath", (addressing.connection_path as i32).to_string())
        .header("RP-PadProcNo", "2")
        .header("RP-SupportCmd", "060000")
        .header(
            "RP-Auth",
            enc(fields::COUNTER_AUTH, fields::auth_plaintext(values.registration_key).to_vec()),
        )
        .header("RP-Did", enc(fields::COUNTER_DID, fields::did_plaintext(values.device_id).to_vec()))
        .header(
            "RP-OSType",
            enc(fields::COUNTER_OS_TYPE, fields::os_type_plaintext(values.os_major, values.os_minor)),
        )
        .header(
            "RP-StartBitrate",
            enc(fields::COUNTER_START_BITRATE, fields::int32le_plaintext(values.start_bitrate_kbps).to_vec()),
        );
    if is_ps5 {
        request = request.header(
            "RP-StreamingType",
            enc(fields::COUNTER_STREAMING_TYPE, fields::int32le_plaintext(values.streaming_type).to_vec()),
        );
    }
    request.serialize()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_host_is_padded_as_captured() {
        assert_eq!(padded_host([192, 0, 2, 104], 9295), "192.  0.  2.104:9295");
    }

    #[test]
    fn a_bad_nonce_is_a_hard_failure() {
        let companion = [0u8; 16];
        let ok = Response::parse(b"HTTP/1.1 200 OK\r\nRP-Nonce: AAAAAAAAAAAAAAAAAAAAAA==\r\n\r\n").unwrap();
        assert!(open_init(true, &ok, &companion).is_ok());
        for bad in [
            &b"HTTP/1.1 200 OK\r\n\r\n"[..],
            b"HTTP/1.1 200 OK\r\nRP-Nonce: AAAA\r\n\r\n",
            b"HTTP/1.1 200 OK\r\nRP-Nonce: AAAA AAAAAAAAAAAAAAAAAA==\r\n\r\n",
        ] {
            assert_eq!(
                open_init(true, &Response::parse(bad).unwrap(), &companion).err(),
                Some(InitError::NoNonce)
            );
        }
        let refused =
            Response::parse(b"HTTP/1.1 403 Forbidden\r\nRP-Application-Reason: 80108b13\r\n\r\n").unwrap();
        assert_eq!(
            open_init(true, &refused, &companion).err(),
            Some(InitError::Refused { status: 403, reason: Some("80108b13".into()) })
        );
    }
}
