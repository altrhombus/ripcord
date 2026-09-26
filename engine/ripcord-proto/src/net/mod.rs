//! Network questions the protocol needs answered, without a socket: STUN, and the candidate decisions of
//! an internet connect. The OS parts (enumerating interfaces, sending) are the host's.

pub mod candidates;
pub mod stun;
