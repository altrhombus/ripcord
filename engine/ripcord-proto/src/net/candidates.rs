//! The candidates of an internet (account-route) connect: which of ours we offer, and which of the
//! console's we talk to. Ported from `libripcord/session/halyard_wan_candidates.c`, whose references are
//! `HalyardAccountPairing.OurCandidates`/`PreferredCandidate` and
//! `HalyardDatagramRegistrationTransport.SharesSubnetWithLocalInterface`. Getting either wrong fails
//! silently: packets still leave, and the peer was never told where to send.

/// The account route's control port, where a console advertises its candidates.
pub const CONTROL_PORT: u16 = 9303;

/// One candidate as signaling carries it. The type stays a string so an unknown kind round-trips and is
/// ignored; the address stays text, trusted to parse only when it is used.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Candidate {
    pub kind: String,
    pub address: String,
    pub port: u16,
}

/// One of this host's up IPv4 interfaces.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Interface {
    pub address: [u8; 4],
    pub netmask: [u8; 4],
}

/// Strict dotted-quad IPv4: four decimal parts of one to three digits, each at most 255, nothing else.
/// Deliberately narrower than .NET's `IPAddress.TryParse` (which reads "10" as 0.0.0.10 and accepts
/// octal- and hex-looking parts): a candidate address arrives from the network by way of the cloud, and
/// only the form a console has ever sent is read. Anything else is skipped, as .NET skips an address that
/// does not parse.
pub fn parse_ipv4(text: &str) -> Option<[u8; 4]> {
    let mut out = [0u8; 4];
    let mut parts = text.split('.');
    for slot in &mut out {
        let part = parts.next()?;
        if part.is_empty() || part.len() > 3 || !part.bytes().all(|b| b.is_ascii_digit()) {
            return None;
        }
        *slot = part.parse::<u16>().ok().filter(|&v| v <= 255)? as u8;
    }
    parts.next().is_none().then_some(out)
}

pub fn format_ipv4(a: [u8; 4]) -> String {
    format!("{}.{}.{}.{}", a[0], a[1], a[2], a[3])
}

/// Whether `address` sits on a network this host is attached to, asked of each interface's own mask
/// rather than guessed from private ranges. A zero mask never matches.
pub fn shares_subnet(address: [u8; 4], interfaces: &[Interface]) -> bool {
    interfaces.iter().any(|nic| {
        nic.netmask != [0; 4]
            && (0..4).all(|b| nic.address[b] & nic.netmask[b] == address[b] & nic.netmask[b])
    })
}

/// What we offer, in the captured client's order: the STUN-reflexive endpoint, then the reflexive address
/// with our local port when the NAT changed the port (a guess that a port-preserving path exists), then the
/// local endpoint.
pub fn ours(local: Option<(&str, u16)>, reflexive: Option<(&str, u16)>) -> Vec<Candidate> {
    let candidate =
        |kind: &str, address: &str, port| Candidate { kind: kind.into(), address: address.into(), port };
    let mut out = Vec::new();
    if let Some((address, port)) = reflexive {
        out.push(candidate("STUN", address, port));
        if let Some((_, local_port)) = local.filter(|&(_, lp)| lp != port) {
            out.push(candidate("STATIC", address, local_port));
        }
    }
    if let Some((address, port)) = local {
        out.push(candidate("LOCAL", address, port));
    }
    out
}

/// Which of the console's candidates to talk to: the first on a subnet we share, else the first that
/// parses, else one whose address is the console's known host, else the first. `None` only for none.
pub fn choose(
    candidates: &[Candidate],
    interfaces: &[Interface],
    console_host: Option<&str>,
) -> Option<usize> {
    if candidates.is_empty() {
        return None;
    }
    let mut first_parsed = None;
    for (i, c) in candidates.iter().enumerate() {
        let Some(address) = parse_ipv4(&c.address) else { continue };
        if shares_subnet(address, interfaces) {
            return Some(i);
        }
        first_parsed.get_or_insert(i);
    }
    first_parsed
        .or_else(|| console_host.and_then(|h| candidates.iter().position(|c| c.address == h)))
        .or(Some(0))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn c(kind: &str, address: &str) -> Candidate {
        Candidate { kind: kind.into(), address: address.into(), port: CONTROL_PORT }
    }

    #[test]
    fn ipv4_is_strict() {
        assert_eq!(parse_ipv4("192.168.1.20"), Some([192, 168, 1, 20]));
        for bad in
            ["10", "1.2.3", "1.2.3.4.5", "256.1.1.1", "01234.1.1.1", "1..2.3", "1.2.3.4 ", "0x1.2.3.4", ""]
        {
            assert_eq!(parse_ipv4(bad), None, "{bad:?}");
        }
        assert_eq!(format_ipv4([10, 0, 0, 7]), "10.0.0.7");
    }

    #[test]
    fn we_offer_in_the_captured_order() {
        let offered = ours(Some(("192.168.1.5", 50000)), Some(("203.0.113.9", 61000)));
        let kinds: Vec<_> = offered.iter().map(|c| (c.kind.as_str(), c.address.as_str(), c.port)).collect();
        assert_eq!(
            kinds,
            [
                ("STUN", "203.0.113.9", 61000),
                ("STATIC", "203.0.113.9", 50000),
                ("LOCAL", "192.168.1.5", 50000)
            ]
        );
        assert_eq!(
            ours(Some(("192.168.1.5", 61000)), Some(("203.0.113.9", 61000))).len(),
            2,
            "no STATIC when the port survived"
        );
        assert_eq!(ours(Some(("192.168.1.5", 1)), None).len(), 1);
    }

    #[test]
    fn a_shared_subnet_wins_then_the_first_that_parses() {
        let home = [Interface { address: [192, 168, 1, 5], netmask: [255, 255, 255, 0] }];
        let offered = [c("STUN", "203.0.113.9"), c("LOCAL", "192.168.1.20")];
        assert_eq!(choose(&offered, &home, None), Some(1));
        assert_eq!(choose(&offered, &[], None), Some(0));
        let unparseable = [c("X", "not-an-address"), c("Y", "console.local")];
        assert_eq!(choose(&unparseable, &home, Some("console.local")), Some(1));
        assert_eq!(choose(&unparseable, &home, None), Some(0));
        assert_eq!(choose(&[], &home, None), None);
        assert!(
            !shares_subnet([10, 0, 0, 1], &[Interface { address: [10, 0, 0, 2], netmask: [0; 4] }]),
            "a zero mask never matches"
        );
    }
}
