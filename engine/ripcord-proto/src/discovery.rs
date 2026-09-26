//! LAN discovery (the SRCH probe) and wake. Ported from `libripcord/discovery/` and checked against
//! `HalyardSearchClient.cs`, `HalyardWakeClient.cs` and `HalyardDiscoveryProfile.cs`. mDNS is not here: it
//! is DNS-SD, and each platform's own service does it better (docs/engine-plan.md).

/// One console family's discovery parameters.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Profile {
    pub host_type: &'static str,
    /// The SRCH and wake port.
    pub port: u16,
    pub protocol_version: &'static str,
    /// The source port a wake is sent from (falling back to an ephemeral one if taken).
    pub wake_source_port: u16,
    /// The source port the post-wake search polls from; 0 means ephemeral.
    pub wake_search_source_port: u16,
}

pub const PS5: Profile = Profile {
    host_type: "PS5",
    port: 9302,
    protocol_version: "00030010",
    wake_source_port: 9303,
    wake_search_source_port: 9303,
};
pub const PS4: Profile = Profile {
    host_type: "PS4",
    port: 987,
    protocol_version: "00020020",
    wake_source_port: 987,
    wake_search_source_port: 0,
};

/// The SRCH datagram.
pub fn probe(profile: &Profile) -> Vec<u8> {
    format!("SRCH * HTTP/1.1\r\ndevice-discovery-protocol-version:{}\r\n\r\n", profile.protocol_version)
        .into_bytes()
}

/// What a console says about itself.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Console {
    pub host_id: String,
    /// "PS5" when the reply does not say, as both references default.
    pub host_type: String,
    pub host_name: String,
    pub system_version: String,
    /// host-request-port, which .NET reads and C does not; 0 when absent or unparseable.
    pub request_port: u16,
    /// "HTTP/1.1 200 Ok" is awake; anything else, 620 Server Standby in practice, is resting.
    pub is_awake: bool,
}

fn trim(s: &str) -> &str {
    s.trim_matches(|c: char| c.is_ascii_whitespace())
}

/// Parses a SRCH reply. `None` unless it starts with an HTTP/1.1 status line and carries a host-id.
///
/// Awake is an exact `200` token after the protocol, the trailing CR stripped: .NET compares the raw token
/// and so reads a bare `HTTP/1.1 200\r` as resting, and C prefix-matches and would read `2000` as awake.
/// Header names are case-insensitive and a repeated header takes its last value, as in both. A host name
/// that is not ASCII is kept as lossy UTF-8 (.NET substitutes `?`; C keeps the raw bytes).
pub fn parse_reply(data: &[u8]) -> Option<Console> {
    let text = String::from_utf8_lossy(data);
    let mut lines = text.split('\n').map(|l| l.strip_suffix('\r').unwrap_or(l));
    let status = lines.next()?;
    if status.len() < 8 || !status[..8].eq_ignore_ascii_case("HTTP/1.1") {
        return None;
    }
    let mut console = Console {
        host_type: "PS5".into(),
        is_awake: status[8..].split_ascii_whitespace().next() == Some("200"),
        ..Default::default()
    };
    let mut have_host_id = false;
    for line in lines {
        let Some(colon) = line.find(':').filter(|&c| c > 0) else { continue };
        let (name, value) = (trim(&line[..colon]), trim(&line[colon + 1..]));
        match name.to_ascii_lowercase().as_str() {
            "host-id" => {
                console.host_id = value.into();
                have_host_id = true;
            }
            "host-type" => console.host_type = value.into(),
            "host-name" => console.host_name = value.into(),
            "system-version" => console.system_version = value.into(),
            "host-request-port" => console.request_port = value.parse().unwrap_or(0),
            _ => {}
        }
    }
    have_host_id.then_some(console)
}

/// The wake datagram's user-credential: the registration key's ASCII hex digits read as a 32-bit number and
/// written as a SIGNED decimal. C's rule, from a PS4 capture that carried a negative credential; .NET writes
/// it unsigned, which would misstate every key at or above 0x80000000 (on the roadmap). Surrounding
/// whitespace and extra leading zeros are allowed, as .NET's parse allows; `None` for anything that is not
/// a 32-bit hex number, where .NET throws.
pub fn wake_credential(registration_key: &[u8]) -> Option<String> {
    let text = std::str::from_utf8(registration_key).ok()?.trim();
    if text.is_empty() || !text.bytes().all(|b| b.is_ascii_hexdigit()) {
        return None;
    }
    let value = u32::from_str_radix(text, 16).ok()?; // leading zeros are fine; past 32 bits is not
    Some((value as i32).to_string())
}

/// The wake datagram: LF line endings, as both references send it.
pub fn wake(profile: &Profile, credential: &str) -> Vec<u8> {
    format!(
        "WAKEUP * HTTP/1.1\nclient-type:vr\nauth-type:R\nmodel:w\napp-type:r\nuser-credential:{credential}\ndevice-discovery-protocol-version:{}\n",
        profile.protocol_version
    )
    .into_bytes()
}

/// The UDP port the arm probe goes to: the control port, whose TCP listener it opens.
pub const ARM_PORT: u16 = 9295;

/// The arm probe. The console does not hold TCP 9295 open continuously; this datagram opens it briefly,
/// before /sess/init on the LAN route and before registration. Best effort: sending it is what arms it.
pub fn arm_probe(is_ps5: bool) -> &'static [u8] {
    if is_ps5 { b"SRC3" } else { b"SRC2" }
}

/// Whether a datagram is the console's answer to the arm probe.
pub fn is_arm_reply(is_ps5: bool, datagram: &[u8]) -> bool {
    datagram.starts_with(if is_ps5 { b"RES3" } else { b"RES2" })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn probes_and_replies() {
        assert_eq!(probe(&PS5), b"SRCH * HTTP/1.1\r\ndevice-discovery-protocol-version:00030010\r\n\r\n");
        let awake = parse_reply(b"HTTP/1.1 200 Ok\r\nhost-id:ABC\r\nHost-Type: PS5\r\nhost-name:Living Room\r\nsystem-version:09000000\r\nhost-request-port:997\r\n\r\n").unwrap();
        assert_eq!(
            (awake.host_id.as_str(), awake.host_name.as_str(), awake.request_port, awake.is_awake),
            ("ABC", "Living Room", 997, true)
        );
        let resting = parse_reply(b"HTTP/1.1 620 Server Standby\r\nhost-id:ABC\r\n").unwrap();
        assert!(!resting.is_awake);
        assert!(parse_reply(b"HTTP/1.1 200\r\nhost-id:x\r\n").unwrap().is_awake, "no reason phrase");
        assert!(!parse_reply(b"HTTP/1.1 2000 x\r\nhost-id:x\r\n").unwrap().is_awake, "not a prefix match");
        assert_eq!(parse_reply(b"HTTP/1.1 200 Ok\r\nhost-type:PS4\r\n"), None, "no host-id");
        assert_eq!(parse_reply(b"SRCH * HTTP/1.1\r\nhost-id:x\r\n"), None, "not a reply");
        assert_eq!(parse_reply(b"HTTP/1.1 200 Ok\r\nhost-id:x\r\n").unwrap().host_type, "PS5", "the default");
    }

    #[test]
    fn wake_credentials_are_signed() {
        assert_eq!(wake_credential(b"1a2b3c4d").as_deref(), Some("439041101"));
        assert_eq!(wake_credential(b"ffffffff").as_deref(), Some("-1"), "0x80000000 and up are negative");
        assert_eq!(
            wake_credential(b" 0001a2b3c4d ").as_deref(),
            Some("439041101"),
            "whitespace and leading zeros"
        );
        assert_eq!(wake_credential(b"1ffffffff"), None, "past 32 bits");
        assert_eq!(wake_credential(b"xyz"), None);
        assert_eq!(wake_credential(b""), None);
        let w = String::from_utf8(wake(&PS4, "-1")).unwrap();
        assert!(
            w.starts_with("WAKEUP * HTTP/1.1\nclient-type:vr\n")
                && w.ends_with("user-credential:-1\ndevice-discovery-protocol-version:00020020\n")
        );
    }
}
