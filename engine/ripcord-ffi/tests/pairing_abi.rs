//! Discovery, wake, the account id, the account seed and PIN registration, through the C ABI.
#![cfg(feature = "test-support")]

use std::ffi::c_void;

use ripcord::*;

extern "C" fn fill(_user: *mut c_void, out: *mut u8, length: usize) -> bool {
    for i in 0..length {
        // SAFETY: `out` valid for `length` bytes.
        unsafe { *out.add(i) = (i as u8).wrapping_mul(13).wrapping_add(5) };
    }
    true
}

extern "C" fn refuse(_user: *mut c_void, _out: *mut u8, _length: usize) -> bool {
    false
}

fn text(b: &[u8]) -> String {
    String::from_utf8_lossy(&b[..b.iter().position(|&c| c == 0).unwrap_or(b.len())]).into_owned()
}

#[test]
fn discovery_and_wake() {
    let (mut buf, mut n, mut port, mut source) = ([0u8; 256], 0usize, 0u16, 0u16);
    // SAFETY: valid buffers throughout.
    unsafe {
        assert_eq!(
            ripcord_discovery_probe(true, buf.as_mut_ptr(), buf.len(), &mut n, &mut port),
            RipcordStatus::Ok
        );
        assert!(buf[..n].starts_with(b"SRCH * HTTP/1.1"));
        assert_eq!(port, 9302);

        let reply = b"HTTP/1.1 620 Server Standby\r\nhost-id:ABCDEF\r\nhost-type:PS5\r\nhost-name:Living Room\r\nsystem-version:09000000\r\nhost-request-port:997\r\n";
        let mut c: RipcordDiscoveredConsole = std::mem::zeroed();
        assert_eq!(ripcord_discovery_parse(reply.as_ptr(), reply.len(), &mut c), RipcordStatus::Ok);
        assert_eq!(text(&c.host_id), "ABCDEF");
        assert_eq!(text(&c.host_name), "Living Room");
        assert!(!c.is_awake);
        assert_eq!(c.request_port, 997);
        assert_eq!(ripcord_discovery_parse(b"nope".as_ptr(), 4, &mut c), RipcordStatus::Rejected);

        let key = b"fffffffe";
        assert_eq!(
            ripcord_wake_payload(
                true,
                key.as_ptr(),
                key.len(),
                buf.as_mut_ptr(),
                buf.len(),
                &mut n,
                &mut port,
                &mut source
            ),
            RipcordStatus::Ok
        );
        let payload = String::from_utf8_lossy(&buf[..n]).into_owned();
        assert!(payload.contains("user-credential:-2\n"), "signed, as C writes it: {payload}");
        assert_eq!(port, 9302);
        assert_eq!(source, ripcord_proto::discovery::PS5.wake_source_port);
        let bad = b"not hex";
        assert_eq!(
            ripcord_wake_payload(
                true,
                bad.as_ptr(),
                bad.len(),
                buf.as_mut_ptr(),
                buf.len(),
                &mut n,
                &mut port,
                &mut source
            ),
            RipcordStatus::Rejected
        );
    }
}

#[test]
fn account_id_and_seed() {
    let (mut out, mut reason) = ([0u8; 24], [0u8; 96]);
    // SAFETY: NUL-terminated inputs and valid buffers.
    unsafe {
        let code = ripcord_account_id_normalise(
            c"0x112210F47DE98115".as_ptr(),
            out.as_mut_ptr(),
            out.len(),
            reason.as_mut_ptr(),
            reason.len(),
        );
        assert_eq!((code, text(&out)), (RIPCORD_ACCOUNT_ID_OK, "1234567890123456789".into()));
        let code = ripcord_account_id_normalise(
            c"FRGJ6fQQIhE=".as_ptr(),
            out.as_mut_ptr(),
            out.len(),
            reason.as_mut_ptr(),
            reason.len(),
        );
        assert_eq!(code, RIPCORD_ACCOUNT_ID_BASE64);
        assert!(text(&reason).contains("base64"));

        let (mut d1, mut d2) = ([0u8; 16], [0u8; 16]);
        let random = RipcordRandom { user: std::ptr::null_mut(), fill: Some(fill) };
        assert_eq!(
            ripcord_account_key_material(&random, d1.as_mut_ptr(), d2.as_mut_ptr()),
            RipcordStatus::Ok
        );
        let refusing = RipcordRandom { user: std::ptr::null_mut(), fill: Some(refuse) };
        assert_eq!(
            ripcord_account_key_material(&refusing, d1.as_mut_ptr(), d2.as_mut_ptr()),
            RipcordStatus::Rejected
        );
        assert_eq!(
            ripcord_account_key_material(&random, d1.as_mut_ptr(), d2.as_mut_ptr()),
            RipcordStatus::Ok
        );

        // The console's side seals a seed; the ABI recovers it from the published double base64.
        use ripcord_proto::halyard::account_seed;
        let seed = [0x42u8; 16];
        let sealed = account_seed::seal(true, &d1, &d2, &seed);
        let published = account_seed::encode_custom_data1(&sealed);
        let mut got = [0u8; 16];
        assert_eq!(
            ripcord_account_seed_recover(
                true,
                d1.as_ptr(),
                d2.as_ptr(),
                published.as_ptr(),
                published.len(),
                got.as_mut_ptr()
            ),
            RipcordStatus::Ok
        );
        assert_eq!(got, seed);
        assert_eq!(
            ripcord_account_seed_recover(true, d1.as_ptr(), d2.as_ptr(), b"!!".as_ptr(), 2, got.as_mut_ptr()),
            RipcordStatus::Rejected
        );
    }
}

#[test]
fn pin_registration_through_the_abi() {
    let (mut control, mut senkusha, mut stream) = (0u16, 0u16, 0u16);
    let random = RipcordRandom { user: std::ptr::null_mut(), fill: Some(fill) };
    // SAFETY: valid pointers throughout.
    unsafe {
        let console =
            ripcord_loopback_console_start(std::ptr::null(), &mut control, &mut senkusha, &mut stream);
        assert!(!console.is_null());
        let mut result: RipcordRegistResult = std::mem::zeroed();
        let host = [127u8, 0, 0, 1];
        let status = ripcord_regist_pin(
            host.as_ptr(),
            true,
            c"1234567890123456789".as_ptr(),
            12_345_678, // the scripted console's default PIN
            c"127.0.0.1".as_ptr(),
            control,
            true,
            &random,
            &mut result,
        );
        assert_eq!(status, RipcordStatus::Ok);
        assert_eq!(result.status, RIPCORD_REGIST_OK);
        assert!(result.saw_arm_reply);
        assert_eq!(&result.record.registration_key[..result.record.registration_key_length], &[0xab; 8]);
        assert!(result.record.is_ps5);

        let refusing = RipcordRandom { user: std::ptr::null_mut(), fill: Some(refuse) };
        ripcord_regist_pin(
            host.as_ptr(),
            true,
            c"1".as_ptr(),
            1,
            c"127.0.0.1".as_ptr(),
            control,
            true,
            &refusing,
            &mut result,
        );
        assert_eq!(result.status, RIPCORD_REGIST_NO_RANDOM, "a refused draw never becomes key material");
        ripcord_loopback_console_stop(console);
    }
}
