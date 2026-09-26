//! A whole LAN session through the C ABI, exactly as a host drives it: extern "C" callbacks, a user
//! pointer, connect, then pump until the session ends. The console is the loopback one, so nothing
//! leaves the machine.
#![cfg(feature = "test-support")]

use std::ffi::{c_char, c_void};
use std::sync::atomic::{AtomicU8, Ordering};
use std::time::{Duration, Instant};

use ripcord::*;

#[derive(Default)]
struct Recorder {
    stages: Vec<RipcordClientStage>,
    video: usize,
    keyframes: usize,
    info: Option<(u32, u32)>,
    stats: usize,
    passcodes_asked: usize,
    lines: usize,
}

fn rec(user: *mut c_void) -> &'static mut Recorder {
    // SAFETY: the test passes a live &mut Recorder as `user` for the client's whole life.
    unsafe { &mut *(user as *mut Recorder) }
}

extern "C" fn on_log(user: *mut c_void, _level: u32, _line: *const c_char) {
    rec(user).lines += 1;
}
extern "C" fn on_stage(user: *mut c_void, stage: RipcordClientStage) {
    rec(user).stages.push(stage);
}
extern "C" fn on_info(user: *mut c_void, info: *const RipcordStreamInfo) {
    // SAFETY: borrowed for the call.
    let i = unsafe { &*info };
    rec(user).info = Some((i.width, i.height));
}
extern "C" fn on_video(user: *mut c_void, _data: *const u8, _length: usize, keyframe: bool) {
    if user.is_null() {
        return;
    }
    let r = rec(user);
    r.video += 1;
    r.keyframes += usize::from(keyframe);
}
extern "C" fn on_stats(user: *mut c_void, _stats: *const RipcordClientStats) {
    rec(user).stats += 1;
}
extern "C" fn on_input(_user: *mut c_void, out: *mut RipcordInputState) -> bool {
    // SAFETY: valid for a write for the call.
    unsafe { (*out).left_x = 1200 };
    true
}
extern "C" fn on_passcode(user: *mut c_void, _retry: u32, out: *mut c_char, size: usize) -> i32 {
    rec(user).passcodes_asked += 1;
    let digits = b"2468\0";
    assert!(size >= digits.len());
    // SAFETY: `out` valid for `size` bytes.
    unsafe { std::ptr::copy_nonoverlapping(digits.as_ptr() as *const c_char, out, digits.len()) };
    1
}
extern "C" fn on_commands(user: *mut c_void) -> u32 {
    if rec(user).video >= 15 { RIPCORD_CMD_DISCONNECT } else { 0 }
}
extern "C" fn fill(_user: *mut c_void, out: *mut u8, length: usize) -> bool {
    // The loopback console is the only peer and nothing leaves the machine; a counter is enough here.
    static N: AtomicU8 = AtomicU8::new(0);
    for i in 0..length {
        let n = N.fetch_add(1, Ordering::Relaxed);
        // SAFETY: `out` valid for `length` bytes.
        unsafe { *out.add(i) = n.wrapping_mul(31).wrapping_add(17) };
    }
    true
}

fn config(ports: (u16, u16, u16), key: &[u8], device: &[u8], companion: [u8; 16]) -> RipcordClientConfig {
    RipcordClientConfig {
        route: RIPCORD_ROUTE_LOCAL,
        console: [127, 0, 0, 1],
        is_ps5: true,
        registration_key: key.as_ptr(),
        registration_key_length: key.len(),
        companion,
        login_pin: std::ptr::null(),
        device_id: device.as_ptr(),
        device_id_length: device.len(),
        os_major: 0,
        os_minor: 0,
        width: 0,
        height: 0,
        fps: 0,
        bitrate_kbps: 0,
        allow_hevc: false,
        hdr: false,
        interface_mtu: 0,
        signin_prompt_window_ms: 0,
        senkusha_attempts: 0,
        stream_attempts: 0,
        attempt_interval_ms: 0,
        rcvbuf_bytes: 0,
        require_session_ready: 0,
        stun_servers: std::ptr::null(),
        stun_server_count: 0,
        stun_attempts: 0,
        stun_timeout_ms: 0,
        control_local_port: 0,
        media_local_port: 0,
        bind_address: [0; 4],
        media_offer_timeout_ms: 0,
        dgram_stage_timeout_ms: 0,
        dgram_receive_timeout_ms: 0,
        control_port: ports.0,
        senkusha_port: ports.1,
        stream_port: ports.2,
        no_arm_broadcast: true,
    }
}

#[test]
fn a_session_through_the_c_abi() {
    let (mut control, mut senkusha, mut stream) = (0u16, 0u16, 0u16);
    let pin = c"2468";
    // SAFETY: valid out pointers and a NUL-terminated passcode.
    let console =
        unsafe { ripcord_loopback_console_start(pin.as_ptr(), &mut control, &mut senkusha, &mut stream) };
    assert!(!console.is_null());
    let mut companion = [0u8; 16];
    // SAFETY: 16 bytes.
    assert_eq!(unsafe { ripcord_loopback_console_companion(companion.as_mut_ptr()) }, RipcordStatus::Ok);

    let (key, device) = ([0xab; 8], [0x22; 32]);
    let cfg = config((control, senkusha, stream), &key, &device, companion);
    let mut recorder = Recorder::default();
    let callbacks = RipcordClientCallbacks {
        user: &mut recorder as *mut Recorder as *mut c_void,
        log: Some(on_log),
        stage: Some(on_stage),
        stream_info: Some(on_info),
        video_frame: Some(on_video),
        audio_frame: None,
        poll_input: Some(on_input),
        poll_passcode: Some(on_passcode),
        poll_commands: Some(on_commands),
        stats: Some(on_stats),
        poll_media: None,
    };
    let random = RipcordRandom { user: std::ptr::null_mut(), fill: Some(fill) };
    // SAFETY: every pointer valid for the client's life; a null backend takes RustCrypto.
    let client = unsafe { ripcord_client_new(&cfg, &callbacks, std::ptr::null(), &random) };
    assert!(!client.is_null());

    let mut stage = RipcordClientStage::Idle;
    // SAFETY: a live client and a valid out pointer.
    assert_eq!(unsafe { ripcord_client_connect(client, &mut stage) }, RipcordStatus::Ok);
    assert_eq!(stage, RipcordClientStage::StreamReady);
    let mut alive = true;
    let start = Instant::now();
    while alive && start.elapsed() < Duration::from_secs(10) {
        // SAFETY: as above.
        assert_eq!(unsafe { ripcord_client_pump(client, 2, &mut alive) }, RipcordStatus::Ok);
    }
    assert!(!alive, "the disconnect command ended the session");

    // SAFETY: a zeroed result is a valid bit pattern for every field, and the client is live.
    let mut result: RipcordClientResult = unsafe { std::mem::zeroed() };
    assert_eq!(unsafe { ripcord_client_result(client, &mut result) }, RipcordStatus::Ok);
    assert_eq!(result.end_reason, RipcordClientEnd::UserDisconnect);
    assert_eq!(result.stage, RipcordClientStage::Streaming);
    assert!(result.login_prompted);
    assert_eq!(result.login_verdict, 1);
    assert!(result.senkusha_ok);
    assert!(result.disconnect_sent);
    assert!(result.input_state_sent >= 1);
    assert_eq!(result.curve, RIPCORD_CURVE_P521);
    assert_eq!(recorder.passcodes_asked, 1);
    assert_eq!(recorder.info, Some((1280, 720)));
    assert!(recorder.keyframes >= 15 && recorder.stats >= 1 && recorder.lines > 0);
    assert_eq!(recorder.stages.last(), Some(&RipcordClientStage::Ended));

    let deadline = Instant::now() + Duration::from_secs(2);
    // SAFETY: a live console.
    while !unsafe { ripcord_loopback_console_saw_goodbye(console) } && Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(5));
    }
    // SAFETY: as above, then each freed once.
    unsafe {
        assert!(ripcord_loopback_console_saw_goodbye(console));
        ripcord_client_free(client);
        ripcord_loopback_console_stop(console);
    }
}

#[test]
fn bad_configs_are_refused_and_layouts_are_reported() {
    let (key, device) = ([0xab; 8], [0x22; 32]);
    let random = RipcordRandom { user: std::ptr::null_mut(), fill: Some(fill) };
    let mut callbacks = RipcordClientCallbacks {
        user: std::ptr::null_mut(),
        log: None,
        stage: None,
        stream_info: None,
        video_frame: Some(on_video),
        audio_frame: None,
        poll_input: None,
        poll_passcode: None,
        poll_commands: None,
        stats: None,
        poll_media: None,
    };
    let mut cfg = config((0, 0, 0), &key, &device, [0; 16]);
    cfg.route = RIPCORD_ROUTE_RENDEZVOUS;
    // SAFETY: valid pointers throughout; each call is refused before anything opens.
    unsafe {
        assert!(ripcord_client_new(&cfg, &callbacks, std::ptr::null(), &random).is_null(), "no poll_media");
        cfg.route = 7;
        assert!(ripcord_client_new(&cfg, &callbacks, std::ptr::null(), &random).is_null(), "unknown route");
        cfg.route = RIPCORD_ROUTE_LOCAL;
        cfg.registration_key_length = 0;
        assert!(ripcord_client_new(&cfg, &callbacks, std::ptr::null(), &random).is_null(), "no key");
        cfg.registration_key_length = key.len();
        callbacks.video_frame = None;
        assert!(ripcord_client_new(&cfg, &callbacks, std::ptr::null(), &random).is_null(), "no video sink");
        assert_eq!(
            ripcord_client_connect(std::ptr::null_mut(), std::ptr::null_mut()),
            RipcordStatus::InvalidArgument
        );
    }
    assert_eq!(ripcord_struct_size(RipcordStructId::ClientConfig as u32), size_of::<RipcordClientConfig>());
    assert_eq!(ripcord_struct_size(RipcordStructId::ClientResult as u32), size_of::<RipcordClientResult>());
    assert_eq!(ripcord_api_version(), 4);
}
