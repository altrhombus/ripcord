//! The A/V stream plane: packet framing, the per-direction key schedule, per-packet crypto, Cauchy
//! Reed-Solomon FEC and frame reassembly. Ported from `libripcord/stream/`.

pub mod demux;
pub mod fec;
pub mod header;
pub mod key_schedule;
pub mod packet_crypto;
