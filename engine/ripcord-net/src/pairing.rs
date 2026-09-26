//! Registration over the network: the PIN route on TCP 9295 (the account route rides a rendezvous
//! client's control leg, [`crate::Client::rendezvous_register`]). Ported from
//! `libripcord/session/halyard_regist_flow.c`: arm the console's listener, send the request, and read
//! until the console closes, since the request says `Connection: close` and there is no length to trust
//! on the way back.

use std::io::{ErrorKind, Read, Write};
use std::net::{Ipv4Addr, SocketAddr, SocketAddrV4, TcpStream, UdpSocket};
use std::time::{Duration, Instant};

use ripcord_proto::RandomSource;
use ripcord_proto::discovery;
use ripcord_proto::halyard::registration::CONTEXT_LENGTH;
use ripcord_proto::sess::regist::{self, Exchange, PairingRecord, RegistError};

/// The arm probe's reply window, then the settle before the console is spoken to (C's figures).
const ARM_REPLY_WINDOW: Duration = Duration::from_millis(2000);
const ARM_SETTLE: Duration = Duration::from_millis(200);
const TCP_CONNECT: Duration = Duration::from_secs(4);
/// The console answers at human timescale; a record that has not arrived in this long is not coming.
const REPLY_WINDOW: Duration = Duration::from_secs(5);
const MAX_REPLY: usize = 8192;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PinParams {
    pub console: [u8; 4],
    pub is_ps5: bool,
    /// As the console expects it; see `ripcord_proto::sess::account_id::normalise`.
    pub account_id: String,
    /// The digits shown on the console.
    pub passcode: u32,
    /// This machine's address toward the console: it goes in the HOST header.
    pub client_ip: String,
    /// 9295; only a test changes it.
    pub port: u16,
    /// Also broadcast the arm probe, as both references do. Off in a loopback test.
    pub arm_broadcast: bool,
}

impl PinParams {
    pub fn new(console: [u8; 4], is_ps5: bool, account_id: &str, passcode: u32, client_ip: &str) -> Self {
        Self {
            console,
            is_ps5,
            account_id: account_id.into(),
            passcode,
            client_ip: client_ip.into(),
            port: regist::PORT,
            arm_broadcast: true,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum PinError {
    /// TCP 9295 refused or unreachable.
    Connect,
    Send,
    /// Connected, then silence.
    NoReply,
    Regist(RegistError),
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PinOutcome {
    /// Whether the console answered the arm probe; diagnosis only, since the probe is best-effort.
    pub saw_arm_reply: bool,
    pub result: Result<PairingRecord, PinError>,
}

/// Opens the console's TCP listener, which it does not hold open continuously. Best-effort: sending the
/// probe is what arms it, so a lost reply is not fatal.
pub(crate) fn arm(console: [u8; 4], port: u16, is_ps5: bool, broadcast: bool) -> bool {
    let Ok(s) = UdpSocket::bind((Ipv4Addr::UNSPECIFIED, 0)) else { return false };
    let _ = s.set_broadcast(broadcast);
    let probe = discovery::arm_probe(is_ps5);
    let _ = s.send_to(probe, SocketAddrV4::new(Ipv4Addr::from(console), port));
    if broadcast {
        let _ = s.send_to(probe, SocketAddrV4::new(Ipv4Addr::BROADCAST, port));
    }
    let _ = s.set_read_timeout(Some(Duration::from_millis(20)));
    let start = Instant::now();
    let mut buf = [0u8; 16];
    let mut saw = false;
    while start.elapsed() < ARM_REPLY_WINDOW {
        if let Ok((n, _)) = s.recv_from(&mut buf)
            && discovery::is_arm_reply(is_ps5, &buf[..n])
        {
            saw = true;
            break;
        }
    }
    std::thread::sleep(ARM_SETTLE);
    saw
}

/// Registers with the PIN the console shows. Blocking: about 2 s arming, then the exchange.
pub fn register_pin(params: &PinParams, random: &mut RandomSource) -> PinOutcome {
    let mut context = [0u8; CONTEXT_LENGTH];
    let mut material = [0u8; 16];
    random(&mut context);
    random(&mut material);
    let built = Exchange::pin(
        params.is_ps5,
        params.passcode,
        &params.account_id,
        &params.client_ip,
        &context,
        &material,
    );
    material.fill(0);
    let (exchange, request) = match built {
        Ok(b) => b,
        Err(e) => return PinOutcome { saw_arm_reply: false, result: Err(PinError::Regist(e)) },
    };
    let saw_arm_reply = arm(params.console, params.port, params.is_ps5, params.arm_broadcast);
    let result = exchange_over_tcp(params.console, params.port, &request)
        .and_then(|reply| exchange.open(&reply).map_err(PinError::Regist));
    PinOutcome { saw_arm_reply, result }
}

fn exchange_over_tcp(console: [u8; 4], port: u16, request: &[u8]) -> Result<Vec<u8>, PinError> {
    let to = SocketAddr::V4(SocketAddrV4::new(Ipv4Addr::from(console), port));
    let mut t = TcpStream::connect_timeout(&to, TCP_CONNECT).map_err(|_| PinError::Connect)?;
    let _ = t.set_nodelay(true);
    t.write_all(request).map_err(|_| PinError::Send)?;
    let _ = t.set_read_timeout(Some(Duration::from_millis(50)));
    let start = Instant::now();
    let mut reply = Vec::new();
    let mut buf = [0u8; 2048];
    while reply.len() < MAX_REPLY && start.elapsed() < REPLY_WINDOW {
        match t.read(&mut buf) {
            Ok(0) => break, // the console closed: the message is complete
            Ok(n) => reply.extend_from_slice(&buf[..n]),
            Err(e)
                if matches!(
                    e.kind(),
                    ErrorKind::WouldBlock | ErrorKind::TimedOut | ErrorKind::Interrupted
                ) => {}
            Err(_) => break,
        }
    }
    if reply.is_empty() { Err(PinError::NoReply) } else { Ok(reply) }
}
