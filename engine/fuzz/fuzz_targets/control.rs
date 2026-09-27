//! The control planes (fuzz_control.c): the /sess binary channel as a session, frame by frame, and every
//! Takion control-message parser.
#![no_main]
use libfuzzer_sys::fuzz_target;
use ripcord_fuzz::Records;
use ripcord_proto::halyard::control::ControlField;
use ripcord_proto::sess::session::ControlSession;
use ripcord_proto::takion::control as tc;

fuzz_target!(|data: &[u8]| {
    let field = ControlField::new(&[1; 16], &[2; 16], 2, 1).expect("the bundle is present");
    let mut session = ControlSession::new(field, true, &[]);
    for record in Records::new(data) {
        session.on_bytes(record);
        while session.poll_event().is_some() {}
        while session.poll_transmit().is_some() {}
        let _ = tc::validate(record);
        let _ = tc::peek_type(record);
        let _ = tc::parse_session_reply(record);
        let _ = tc::parse_session_request(record);
        let _ = tc::parse_stream_info(record);
        let _ = tc::parse_disconnect(record);
        let _ = tc::parse_protocol_version_ack(record);
        let _ = tc::parse_bandwidth_probe(record);
    }
});
