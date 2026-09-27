//! What every target shares: one fuzzer input read as a sequence of records, each a 2-byte big-endian
//! length and that many bytes, as `libripcord/tests/fuzz/fuzz_input.h` reads it. Most of what the targets
//! exercise is stateful across packets, and a single-buffer harness never reaches the state a second packet
//! finds. A length past the end is truncated rather than rejected, so no input is wasted on framing.

pub struct Records<'a> {
    data: &'a [u8],
}

impl<'a> Records<'a> {
    pub fn new(data: &'a [u8]) -> Self {
        Self { data }
    }
}

impl<'a> Iterator for Records<'a> {
    type Item = &'a [u8];

    fn next(&mut self) -> Option<&'a [u8]> {
        if self.data.len() < 2 {
            return None;
        }
        let length = (usize::from(self.data[0]) << 8 | usize::from(self.data[1])).min(self.data.len() - 2);
        let (record, rest) = self.data[2..].split_at(length);
        self.data = rest;
        Some(record)
    }
}

/// A counting random source: fuzzing needs determinism, not secrecy.
pub fn counting() -> ripcord_proto::RandomSource {
    let mut n = 0u8;
    Box::new(move |b: &mut [u8]| {
        b.iter_mut().for_each(|x| {
            *x = n.wrapping_mul(31).wrapping_add(17);
            n = n.wrapping_add(1);
        })
    })
}
