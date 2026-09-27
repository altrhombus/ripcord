//! /sess (the rest of fuzz_rendezvous.c and fuzz_account.c): HTTP responses, the init reply's nonce, and
//! the registration reply opened with a real exchange's key.
#![no_main]
use libfuzzer_sys::fuzz_target;
use ripcord_proto::sess::http::Response;
use ripcord_proto::sess::{regist, requests};

fuzz_target!(|data: &[u8]| {
    if let Some(r) = Response::parse(data) {
        let _ = r.header("RP-Nonce");
        let _ = requests::open_init(true, &r, &[3; 16]);
    }
    let _ = regist::split_response(data);
    let _ = regist::parse_pairing_record(data);
    let (exchange, _) = regist::Exchange::pin(true, 12_345_678, "1234567890123456789", "192.0.2.1", &[5; 0x1e0], &[6; 16])
        .expect("the bundle is present");
    let _ = exchange.open(data);
});
