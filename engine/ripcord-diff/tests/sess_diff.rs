//! Discovery, wake and the /sess message layer: the Rust engine against the C core, where the two are
//! meant to agree. Inputs avoid the differences chosen on purpose (engine/README.md): the awake token's
//! strictness, duplicate headers, and lenient credential input.

use ripcord_diff::*;
use ripcord_proto::discovery;
use ripcord_proto::sess::{ctrl, fields, http};

fn pick<'a>(rng: &mut Rng, xs: &[&'a str]) -> &'a str {
    xs[rng.below(xs.len() as u64) as usize]
}

#[test]
fn discovery_replies() {
    let mut rng = Rng::new(0x5eed_0201);
    let mut parsed = 0;
    for iteration in 0..10_000 {
        let status = pick(
            &mut rng,
            &[
                "HTTP/1.1 200 Ok",
                "HTTP/1.1 620 Server Standby",
                "http/1.1 200 OK",
                "HTTP/1.1  200 Ok",
                "HTTP/1.0 200 Ok",
                "SRCH * HTTP/1.1",
            ],
        );
        let mut text = format!("{status}\r\n");
        let mut names =
            ["host-id", "host-type", "host-name", "system-version", "host-request-port", "x-other"];
        for i in (1..names.len()).rev() {
            names.swap(i, rng.below(i as u64 + 1) as usize);
        }
        for name in names.iter().take(rng.below(6) as usize + 1) {
            // Under C's smallest field buffer (15 characters): its truncation is a limit .NET does not have.
            let value: String =
                (0..rng.below(14)).map(|_| pick(&mut rng, &["a", "B", "0", " ", "-", "9"])).collect();
            let name = if rng.below(4) == 0 { name.to_uppercase() } else { name.to_string() };
            text.push_str(&format!("{name}:{value}\r\n"));
        }
        let rust = discovery::parse_reply(text.as_bytes())
            .map(|c| (c.host_id, c.host_type, c.host_name, c.system_version, c.is_awake));
        assert_eq!(rust, c_discovery_parse(text.as_bytes()), "iteration {iteration}: {text:?}");
        parsed += usize::from(rust.is_some());
    }
    assert!(parsed > 3_000, "{parsed} replies parsed");
}

#[test]
fn wake_credentials_and_payloads() {
    let mut rng = Rng::new(0x5eed_0202);
    for iteration in 0..10_000 {
        // Strictly formed keys, 1 to 8 hex digits, where both parsers accept the same inputs.
        let len = 1 + rng.below(8) as usize;
        let key: String =
            (0..len).map(|_| pick(&mut rng, &["0", "1", "7", "8", "a", "F", "f", "c"])).collect();
        let rust = discovery::wake_credential(key.as_bytes());
        assert_eq!(rust, c_wake_credential(key.as_bytes()), "credential, iteration {iteration}: {key}");
        let credential = rust.unwrap();
        for (ps5, profile) in [(true, discovery::PS5), (false, discovery::PS4)] {
            assert_eq!(
                discovery::wake(&profile, &credential),
                c_wake_payload(ps5, &credential),
                "payload, iteration {iteration}"
            );
        }
    }
    for bad in [&b"xyz"[..], b"123456789", b"", b"12 34"] {
        assert_eq!(discovery::wake_credential(bad), c_wake_credential(bad), "{bad:?}");
    }
}

#[test]
fn sess_field_plaintexts() {
    let mut rng = Rng::new(0x5eed_0203);
    for iteration in 0..5_000 {
        let key = rng.vec_of(0, 24);
        let id = rng.vec_of(0, 24);
        let (major, minor) = (rng.below(20) as i32, rng.below(20) as i32);
        let rust = (
            fields::auth_plaintext(&key),
            fields::did_plaintext(&id),
            fields::os_type_plaintext(major, minor),
        );
        assert_eq!(rust, c_sess_fields(&key, &id, major, minor), "iteration {iteration}");
    }
}

#[test]
fn control_frames() {
    let mut rng = Rng::new(0x5eed_0204);
    for iteration in 0..20_000 {
        let mut d = match rng.below(3) {
            0 => ctrl::build(rng.next_u64() as u16, &rng.vec_of(0, 40)),
            1 => {
                let mut f = ctrl::build(ctrl::HEARTBEAT_REQ, &[]);
                f.extend(ctrl::build(ctrl::SESSION_ID, &rng.vec_of(0, 20)));
                f
            }
            _ => rng.vec_of(0, 30),
        };
        if rng.below(3) == 0 && !d.is_empty() {
            let cut = rng.below(d.len() as u64) as usize;
            d.truncate(cut);
        }
        let rust = ctrl::parse(&d).map(|(k, p, used)| (k, p.len(), used));
        assert_eq!(rust, c_ctrl_parse(&d), "iteration {iteration}");
    }
}

#[test]
fn sess_responses() {
    let mut rng = Rng::new(0x5eed_0205);
    let mut complete = 0;
    for iteration in 0..10_000 {
        let status = pick(
            &mut rng,
            &["HTTP/1.1 200 OK", "HTTP/1.1 403 Forbidden", "HTTP/1.1  404", "HTTP/1.1 x", "garbage"],
        );
        let body = "b".repeat(rng.below(6) as usize);
        let declared = if rng.below(4) == 0 { body.len() + 2 } else { body.len() };
        let nonce = pick(&mut rng, &["AAAA", " spaced value ", "", "x:y"]);
        let name = pick(&mut rng, &["RP-Nonce", "rp-nonce", "RP-NONCE"]);
        let mut text = format!("{status}\r\n{name}:{nonce}\r\nContent-Length: {declared}\r\n\r\n{body}");
        if rng.below(5) == 0 {
            text.truncate(rng.below(text.len() as u64) as usize);
        }
        let rust = http::Response::parse(text.as_bytes()).map(|r| {
            (
                i32::from(r.status),
                r.consumed,
                r.header("RP-Nonce").map(|v| String::from_utf8_lossy(v).into_owned()),
            )
        });
        assert_eq!(rust, c_sess_response(text.as_bytes(), "RP-Nonce"), "iteration {iteration}: {text:?}");
        complete += usize::from(rust.is_some());
    }
    assert!(complete > 3_000, "{complete} responses complete");
}
