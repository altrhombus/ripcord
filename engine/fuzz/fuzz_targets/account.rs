//! The account route's text inputs (fuzz_account.c): customData1 and a typed account id.
#![no_main]
use libfuzzer_sys::fuzz_target;
use ripcord_proto::halyard::account_seed;
use ripcord_proto::sess::{account_id, regist};

fuzz_target!(|data: &[u8]| {
    let _ = account_seed::decode_custom_data1(data);
    let _ = account_seed::recover_custom_data1(true, &[1; 16], &[2; 16], data);
    let text = String::from_utf8_lossy(data);
    let _ = account_id::normalise(&text);
    let _ = regist::encode_account_id(&text);
});
