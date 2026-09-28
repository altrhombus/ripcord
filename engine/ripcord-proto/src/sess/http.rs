//! The HTTP/1.1 subset the /sess endpoints speak. Ported from `libripcord/session/halyard_sess_request.c`
//! (`SessProtocol.cs`'s `SessRequest` and `SessResponse` on the .NET side).

/// A GET or POST with headers in the order given. `serialize` ends every request with `Content-Length`,
/// written once: a duplicate is a request-smuggling shape the console refuses with a 403.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Request {
    pub method: &'static str,
    pub path: String,
    pub headers: Vec<(String, String)>,
    pub body: Vec<u8>,
}

impl Request {
    pub fn get(path: impl Into<String>) -> Self {
        Self { method: "GET", path: path.into(), headers: Vec::new(), body: Vec::new() }
    }

    pub fn header(mut self, name: impl Into<String>, value: impl Into<String>) -> Self {
        self.headers.push((name.into(), value.into()));
        self
    }

    pub fn serialize(&self) -> Vec<u8> {
        let mut out = format!("{} {} HTTP/1.1\r\n", self.method, self.path);
        for (name, value) in &self.headers {
            out.push_str(&format!("{name}: {value}\r\n"));
        }
        out.push_str(&format!("Content-Length: {}\r\n\r\n", self.body.len()));
        let mut bytes = out.into_bytes();
        bytes.extend_from_slice(&self.body);
        bytes
    }
}

/// `/sie/<ps5|ps4>/rp/sess/<endpoint>`.
pub fn path(is_ps5: bool, endpoint: &str) -> String {
    format!("/sie/{}/rp/sess/{endpoint}", if is_ps5 { "ps5" } else { "ps4" })
}

/// RP-Version per family.
pub fn version(is_ps5: bool) -> &'static str {
    if is_ps5 { "1.0" } else { "10.0" }
}

fn trim(s: &[u8]) -> &[u8] {
    let is_space = |c: &u8| matches!(c, b' ' | b'\t' | b'\r' | b'\n');
    let start = s.iter().position(|c| !is_space(c)).unwrap_or(s.len());
    let end = s.iter().rposition(|c| !is_space(c)).map_or(start, |e| e + 1);
    &s[start..end.max(start)]
}

/// A complete response: the status, the header block (without the blank line), the body, and how many
/// bytes of the input it used, so what follows can be kept (the first bytes of the binary channel, after
/// /sess/ctrl).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Response<'a> {
    pub status: u16,
    head: &'a [u8],
    pub body: &'a [u8],
    pub consumed: usize,
}

fn header_end(data: &[u8]) -> Option<usize> {
    data.windows(4).position(|w| w == b"\r\n\r\n")
}

/// Each header line after the status line, as (name, value) with both trimmed.
fn header_lines(head: &[u8]) -> impl Iterator<Item = (&[u8], &[u8])> {
    let after_status = head.iter().position(|&c| c == b'\n').map_or(head.len(), |n| n + 1);
    head[after_status..].split(|&c| c == b'\n').filter_map(|line| {
        let colon = line.iter().position(|&c| c == b':').filter(|&c| c > 0)?;
        Some((trim(&line[..colon]), trim(&line[colon + 1..])))
    })
}

impl<'a> Response<'a> {
    /// `None` until the header block and the Content-Length worth of body have arrived, or if the status
    /// line carries no code.
    pub fn parse(data: &'a [u8]) -> Option<Self> {
        let end = header_end(data)?;
        let head = &data[..end];
        let status_line = &head[..head.iter().position(|&c| c == b'\n').unwrap_or(head.len())];
        let after_space = status_line.iter().position(|&c| c == b' ')?;
        let digits: Vec<u8> = status_line[after_space..]
            .iter()
            .skip_while(|&&c| c == b' ')
            .take_while(|c| c.is_ascii_digit())
            .copied()
            .collect();
        if digits.is_empty() {
            return None;
        }
        let status = std::str::from_utf8(&digits).ok()?.parse::<u32>().ok()?.min(u32::from(u16::MAX)) as u16;
        // The first Content-Length's leading digits, as the C parser reads it; absent means none.
        let content_length = header_lines(head)
            .find(|(name, _)| name.eq_ignore_ascii_case(b"Content-Length"))
            .map_or(0usize, |(_, v)| {
                v.iter()
                    .take_while(|c| c.is_ascii_digit())
                    .fold(0usize, |n, &d| n.saturating_mul(10).saturating_add(usize::from(d - b'0')))
            });
        let body_start = end + 4;
        let consumed = body_start.checked_add(content_length)?;
        if data.len() < consumed {
            return None;
        }
        Some(Self { status, head, body: &data[body_start..consumed], consumed })
    }

    /// A header by name (case-insensitive), trimmed. A repeated header gives its last value, as .NET's
    /// dictionary does.
    pub fn header(&self, name: &str) -> Option<&'a [u8]> {
        header_lines(self.head)
            .filter(|(n, _)| n.eq_ignore_ascii_case(name.as_bytes()))
            .map(|(_, v)| v)
            .last()
    }

    pub fn is_success(&self) -> bool {
        (200..300).contains(&self.status)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_request_ends_with_one_content_length() {
        let r = Request::get(path(true, "init")).header("Host", "x").serialize();
        assert_eq!(r, b"GET /sie/ps5/rp/sess/init HTTP/1.1\r\nHost: x\r\nContent-Length: 0\r\n\r\n");
    }

    #[test]
    fn responses_wait_for_their_body_and_keep_the_rest() {
        let full = b"HTTP/1.1 200 OK\r\nrp-nonce:  abc \r\nContent-Length: 3\r\n\r\nxyzREST";
        assert_eq!(Response::parse(&full[..40]), None);
        let r = Response::parse(full).unwrap();
        assert_eq!((r.status, r.body, r.consumed), (200, &b"xyz"[..], full.len() - 4));
        assert_eq!(r.header("RP-Nonce"), Some(&b"abc"[..]));
        assert_eq!(r.header("Missing"), None);
        assert_eq!(Response::parse(b"garbage\r\n\r\n"), None);
    }
}
