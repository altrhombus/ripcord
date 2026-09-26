//! /sess/rgst: the registration request, PIN and account routes, and the pairing record it returns.
//! Ported from `libripcord/session/halyard_regist_message.c` and `halyard_account_regist.c`, following
//! `HalyardRegistrationMessage.cs` where the two differ (listed in `engine/README.md`).
//!
//! The request body is a random 0x1e0-byte context with the wrapped material scattered into it, then the
//! encrypted field `Client-Type: <hex>\r\nNp-AccountId: <base64>\r\n`. One transport key protects that
//! field and the reply, so an exchange keeps its context and material to open the answer.

use crate::base64;
use crate::halyard::constants::CLIENT_TYPE_HEX;
use crate::halyard::control::ControlField;
use crate::halyard::registration::{self, CONTEXT_LENGTH, FIELD_COUNTER};

pub const PORT: u16 = 9295;

/// The `Client-Type` value, read from the .NET reference at build time: the engine keeps no copy.
pub fn client_type_hex() -> &'static str {
    CLIENT_TYPE_HEX
}

/// Np-AccountId: a numeric PSN id as the base64 of a little-endian u64; anything else, including a number
/// past `u64::MAX`, as the base64 of its UTF-8. .NET's rule (`ulong.TryParse`: surrounding whitespace and a
/// leading `+` allowed); the C core wraps an overflowing number silently.
pub fn encode_account_id(account_id: &str) -> String {
    let t = account_id.trim();
    let digits = t.strip_prefix('+').unwrap_or(t);
    match digits.parse::<u64>() {
        Ok(n) if !digits.is_empty() && digits.bytes().all(|b| b.is_ascii_digit()) => {
            base64::encode(&n.to_le_bytes())
        }
        _ => base64::encode(account_id.as_bytes()),
    }
}

/// The field's plaintext. `None` for an empty account id.
pub fn field_plaintext(account_id: &str) -> Option<Vec<u8>> {
    // .NET sends an empty Np-AccountId for an empty id; an empty id is a caller's mistake, so refused as C does.
    if account_id.is_empty() {
        return None;
    }
    Some(
        format!("Client-Type: {}\r\nNp-AccountId: {}\r\n", CLIENT_TYPE_HEX, encode_account_id(account_id))
            .into_bytes(),
    )
}

/// The POST. The header order is the reference's, Content-Length before RP-Version, and `HOST` is
/// uppercase with no port.
pub fn build_request(is_ps5: bool, client_ip: &str, body: &[u8]) -> Option<Vec<u8>> {
    if client_ip.is_empty() {
        return None;
    }
    let head = format!(
        "POST {} HTTP/1.1\r\nHOST: {client_ip}\r\nUser-Agent: remoteplay Windows\r\nConnection: close\r\nContent-Length: {}\r\nRP-Version: {}\r\n\r\n",
        super::http::path(is_ps5, "rgst"),
        body.len(),
        super::http::version(is_ps5),
    );
    let mut out = head.into_bytes();
    out.extend_from_slice(body);
    Some(out)
}

/// A response split into status, the console's own `RP-Application-Reason` (the first, as .NET keeps it),
/// and the body. Without the reason every refusal reads alike.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Reply<'a> {
    pub status: i32,
    pub reason: Option<String>,
    pub body: &'a [u8],
}

pub fn split_response(response: &[u8]) -> Option<Reply<'_>> {
    let sep = response.windows(4).position(|w| w == b"\r\n\r\n")?;
    let head = String::from_utf8_lossy(&response[..sep]);
    let mut lines = head.split("\r\n");
    let status = lines.next()?.split(' ').nth(1)?.trim().parse::<i32>().ok()?;
    let reason = lines.find_map(|line| {
        let colon = line.find(':').filter(|&c| c > 0)?;
        line[..colon]
            .trim()
            .eq_ignore_ascii_case("RP-Application-Reason")
            .then(|| line[colon + 1..].trim().to_owned())
    });
    Some(Reply { status, reason, body: &response[sep + 4..] })
}

/// A pairing record: what the console returns once registration succeeds.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PairingRecord {
    /// The raw registration-key bytes (the field is their hex).
    pub registration_key: Vec<u8>,
    pub companion: [u8; 16],
    pub key_type: i32,
    /// Which family's field the console used: it names the field by its own family.
    pub is_ps5: bool,
}

fn decode_hex(s: &str) -> Option<Vec<u8>> {
    if !s.len().is_multiple_of(2) {
        return None;
    }
    (0..s.len()).step_by(2).map(|i| u8::from_str_radix(s.get(i..i + 2)?, 16).ok()).collect()
}

/// Parses the decrypted body's `Key: Value` lines. .NET's rules: a repeated field takes the last value, a
/// RegistKey that is not hex is taken as its ASCII, and an unparseable key type is 0. `None` without a
/// RegistKey or a 16-byte RP-Key.
pub fn parse_pairing_record(decrypted: &[u8]) -> Option<PairingRecord> {
    let text = String::from_utf8_lossy(decrypted);
    let mut fields = std::collections::HashMap::new();
    for line in text.split('\n') {
        if let Some(colon) = line.find(':').filter(|&c| c > 0) {
            fields.insert(
                line[..colon].trim().to_ascii_lowercase(),
                line[colon + 1..].trim_matches(['\r', ' ', '\t']).to_owned(),
            );
        }
    }
    let (is_ps5, key) = match (fields.get("ps5-registkey"), fields.get("ps4-registkey")) {
        (Some(k), _) => (true, k),
        (None, Some(k)) => (false, k),
        _ => return None,
    };
    let key = key.trim();
    let registration_key = decode_hex(key).unwrap_or_else(|| key.as_bytes().to_vec());
    let companion: [u8; 16] = decode_hex(fields.get("rp-key")?.trim())?.try_into().ok()?;
    let key_type = fields.get("rp-keytype").and_then(|v| v.trim().parse().ok()).unwrap_or(0);
    Some(PairingRecord { registration_key, companion, key_type, is_ps5 })
}

/// Why an exchange did not produce a record.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum RegistError {
    /// No account id or client address, or the tables for this family are absent.
    BadParams,
    /// Not an HTTP response this understands.
    Malformed,
    /// A non-2xx answer, with the console's own reason when it gave one.
    Refused { status: i32, reason: Option<String> },
    /// 2xx, but the body does not decrypt to a record: a wrong PIN, or a wrong seed.
    BadRecord,
}

/// One registration exchange, PIN or account: it builds the request and opens the reply with the same key.
pub struct Exchange {
    field: ControlField,
    is_ps5: bool,
}

impl Exchange {
    /// The PIN route. `random_context` and `random_material` come from the host's CSPRNG.
    pub fn pin(
        is_ps5: bool,
        passcode: u32,
        account_id: &str,
        client_ip: &str,
        random_context: &[u8; CONTEXT_LENGTH],
        random_material: &[u8; 16],
    ) -> Result<(Self, Vec<u8>), RegistError> {
        let mut context = *random_context;
        let wrapped =
            registration::wrap_material(is_ps5, random_material, &context).ok_or(RegistError::BadParams)?;
        registration::scatter(&wrapped, &mut context);
        let field =
            registration::field(is_ps5, &context, passcode, random_material).ok_or(RegistError::BadParams)?;
        Self::finish(field, is_ps5, context, account_id, client_ip)
    }

    /// The account (no-PIN) route: the seed the console published replaces the passcode, and the material
    /// is wrapped the account route's way.
    pub fn account(
        is_ps5: bool,
        seed: &[u8; 16],
        account_id: &str,
        client_ip: &str,
        random_context: &[u8; CONTEXT_LENGTH],
        random_material: &[u8; 16],
    ) -> Result<(Self, Vec<u8>), RegistError> {
        let mut context = *random_context;
        let wrapped = registration::wrap_account_material(is_ps5, random_material, &context)
            .ok_or(RegistError::BadParams)?;
        registration::scatter(&wrapped, &mut context);
        let field = registration::account_field(is_ps5, &context, seed, random_material)
            .ok_or(RegistError::BadParams)?;
        Self::finish(field, is_ps5, context, account_id, client_ip)
    }

    fn finish(
        field: ControlField,
        is_ps5: bool,
        context: [u8; CONTEXT_LENGTH],
        account_id: &str,
        client_ip: &str,
    ) -> Result<(Self, Vec<u8>), RegistError> {
        let mut encrypted = field_plaintext(account_id).ok_or(RegistError::BadParams)?;
        field.encrypt(FIELD_COUNTER, &mut encrypted);
        let mut body = context.to_vec();
        body.extend_from_slice(&encrypted);
        let request = build_request(is_ps5, client_ip, &body).ok_or(RegistError::BadParams)?;
        Ok((Self { field, is_ps5 }, request))
    }

    pub fn is_ps5(&self) -> bool {
        self.is_ps5
    }

    /// The transport key protecting the field and the reply: for checking against the reference.
    pub fn transport_key(&self) -> [u8; 16] {
        self.field.key
    }

    /// Opens the console's answer into a pairing record.
    pub fn open(&self, response: &[u8]) -> Result<PairingRecord, RegistError> {
        let reply = split_response(response).ok_or(RegistError::Malformed)?;
        if !(200..300).contains(&reply.status) {
            return Err(RegistError::Refused { status: reply.status, reason: reply.reason });
        }
        if reply.body.is_empty() {
            return Err(RegistError::BadRecord);
        }
        let mut plain = reply.body.to_vec();
        self.field.decrypt(FIELD_COUNTER, &mut plain);
        parse_pairing_record(&plain).ok_or(RegistError::BadRecord)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The synthetic fixtures `registration_test.c` checks.
    #[test]
    fn the_message_layer_matches_the_c_fixtures() {
        let field = String::from_utf8(field_plaintext("1234567890123456").unwrap()).unwrap();
        assert!(field.starts_with(&format!("Client-Type: {}\r\n", client_type_hex())));
        assert!(field.contains("Np-AccountId: wLqKPNViBAA=\r\n"), "a numeric id is a little-endian u64");
        let req =
            String::from_utf8_lossy(&build_request(true, "192.0.2.5", &[0xde, 0xad, 0xbe, 0xef]).unwrap())
                .into_owned();
        assert!(req.starts_with("POST /sie/ps5/rp/sess/rgst HTTP/1.1\r\n"));
        assert!(
            req.contains("\r\nHOST: 192.0.2.5\r\n")
                && req.contains("\r\nContent-Length: 4\r\n")
                && req.contains("\r\nRP-Version: 1.0\r\n")
        );
        assert!(!req.contains("Content-Type") && !req.contains("Np-AccountId"));
        assert!(
            String::from_utf8(build_request(false, "h", b"").unwrap()).unwrap().contains("RP-Version: 10.0")
        );

        let refusal = split_response(
            b"HTTP/1.1 403 Forbidden\r\nRP-Application-Reason: 80108bff\r\nContent-Length: 0\r\n\r\n",
        )
        .unwrap();
        assert_eq!(
            (refusal.status, refusal.reason.as_deref(), refusal.body),
            (403, Some("80108bff"), &b""[..])
        );

        let rec = parse_pairing_record(b"PS5-RegistKey: 3161326233633464\r\nRP-Key: 000102030405060708090a0b0c0d0e0f\r\nRP-KeyType: 2\r\n").unwrap();
        assert_eq!(rec.registration_key, b"1a2b3c4d", "hex-decoded to the ASCII the console meant");
        assert_eq!((rec.is_ps5, rec.key_type, rec.companion[15]), (true, 2, 0x0f));
        assert_eq!(parse_pairing_record(b"PS5-RegistKey: 3161326233633464\r\n"), None, "no RP-Key");
    }

    #[test]
    fn account_ids_follow_the_reference() {
        assert_eq!(encode_account_id(" 1234567890123456 "), "wLqKPNViBAA=");
        assert_eq!(encode_account_id("+1"), base64::encode(&1u64.to_le_bytes()));
        assert_eq!(
            encode_account_id("18446744073709551616"),
            base64::encode(b"18446744073709551616"),
            "past u64::MAX: UTF-8, not a wrap"
        );
        assert_eq!(encode_account_id("psn-name"), base64::encode(b"psn-name"));
    }

    #[test]
    fn an_exchange_opens_its_own_reply_and_refuses_others() {
        let context: [u8; CONTEXT_LENGTH] = std::array::from_fn(|i| i as u8);
        let (exchange, request) =
            Exchange::pin(true, 12345678, "1", "192.0.2.9", &context, &[7; 16]).unwrap();
        assert!(request.starts_with(b"POST /sie/ps5/rp/sess/rgst"));
        // The console's side: encrypt a record with the same field.
        let record = b"PS5-RegistKey: 3161326233633464\r\nRP-Key: 000102030405060708090a0b0c0d0e0f\r\n";
        let mut body = record.to_vec();
        exchange.field.encrypt(FIELD_COUNTER, &mut body);
        let mut response = b"HTTP/1.1 200 OK\r\n\r\n".to_vec();
        response.extend(&body);
        assert_eq!(exchange.open(&response).unwrap().registration_key, b"1a2b3c4d");
        let (wrong_pin, _) = Exchange::pin(true, 87654321, "1", "192.0.2.9", &context, &[7; 16]).unwrap();
        assert_eq!(wrong_pin.open(&response), Err(RegistError::BadRecord));
        assert_eq!(
            exchange.open(b"HTTP/1.1 403 Forbidden\r\nRP-Application-Reason: 80108b09\r\n\r\n"),
            Err(RegistError::Refused { status: 403, reason: Some("80108b09".into()) })
        );
    }
}
