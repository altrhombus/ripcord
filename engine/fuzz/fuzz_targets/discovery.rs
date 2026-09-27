//! Discovery and wake (fuzz_discovery.c): the SRCH reply, the arm reply and the wake credential.
#![no_main]
use libfuzzer_sys::fuzz_target;
use ripcord_proto::discovery;

fuzz_target!(|data: &[u8]| {
    let _ = discovery::parse_reply(data);
    let _ = discovery::is_arm_reply(true, data);
    if let Some(credential) = discovery::wake_credential(data) {
        let _ = discovery::wake(&discovery::PS5, &credential);
    }
});
