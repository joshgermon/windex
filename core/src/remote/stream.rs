//! The terminal data stream's byte level (docs/protocol.md §4.4): a Ghostty snapshot, then PTY
//! output with Rex metadata APCs (`ESC _ rex1 ; <nonce> ; <json> ESC \`) mixed in.
//!
//! Message boundaries mean nothing, so both the snapshot boundary and partial APC prefixes are
//! carried across messages (and across resumes). Ported from Rex Rune's `src/wire.rs`
//! `SnapshotBuffer` and `src/terminal.rs` `ApcParser` (MIT), reworked to scan instead of
//! shifting byte by byte, and to hand the snapshot to the pane worker undecoded (a libghostty
//! `Terminal` must be created on the thread that owns it).
use anyhow::{Result, ensure};
use serde_json::Value;

const ENVELOPE: &[u8; 10] = b"GHOSTSNP\x01\x00";
const FINISH_TAG: u16 = 6;
const SNAPSHOT_LIMIT: usize = 256 * 1024 * 1024;
const APC_LIMIT: usize = 1024 * 1024;

#[derive(Debug, Clone, PartialEq)]
pub enum Chunk {
    /// A complete snapshot, envelope through the FINISH record.
    Snapshot(Vec<u8>),
    /// PTY output for the terminal.
    Output(Vec<u8>),
    /// A Rex metadata APC (`resize`, `theme`, `child_exited`, ...).
    Meta(Value),
}

/// Splits one stream (from its WELCOME on) into chunks.
pub struct StreamDecoder {
    /// Bytes of a snapshot not yet complete; `None` once it has been delivered.
    snapshot: Option<Vec<u8>>,
    apc: ApcFilter,
}

impl StreamDecoder {
    /// A stream that starts with a snapshot (after HELLO).
    pub fn new(nonce: u64) -> Self {
        Self { snapshot: Some(Vec::new()), apc: ApcFilter::new(nonce) }
    }

    pub fn feed(&mut self, bytes: &[u8]) -> Result<Vec<Chunk>> {
        let mut chunks = Vec::new();
        let rest = match &mut self.snapshot {
            Some(buffer) => {
                ensure!(buffer.len() + bytes.len() <= SNAPSHOT_LIMIT, "snapshot exceeds 256 MiB");
                buffer.extend_from_slice(bytes);
                let Some(end) = snapshot_end(buffer)? else { return Ok(chunks) };
                let mut snapshot = self.snapshot.take().unwrap_or_default();
                let tail = snapshot.split_off(end);
                chunks.push(Chunk::Snapshot(snapshot));
                tail
            }
            None => bytes.to_vec(),
        };
        if !rest.is_empty() {
            self.apc.feed(&rest, &mut chunks);
        }
        Ok(chunks)
    }
}

/// The length of the snapshot at the start of `bytes` once its FINISH record is there.
/// Ghostty validates the records and their CRCs when it decodes them.
fn snapshot_end(bytes: &[u8]) -> Result<Option<usize>> {
    if bytes.len() < ENVELOPE.len() {
        ensure!(ENVELOPE.starts_with(bytes), "the stream did not start with a Ghostty snapshot");
        return Ok(None);
    }
    ensure!(&bytes[..ENVELOPE.len()] == ENVELOPE, "unsupported Ghostty snapshot envelope");
    let mut at = ENVELOPE.len();
    while bytes.len() >= at + 10 {
        let tag = u16::from_le_bytes([bytes[at], bytes[at + 1]]);
        let len = u32::from_le_bytes([bytes[at + 2], bytes[at + 3], bytes[at + 4], bytes[at + 5]]) as usize;
        ensure!(len <= SNAPSHOT_LIMIT, "snapshot record exceeds the limit");
        let end = at + 10 + len;
        if end > bytes.len() {
            return Ok(None);
        }
        if tag == FINISH_TAG {
            return Ok(Some(end));
        }
        at = end;
    }
    Ok(None)
}

/// Pulls Rex APCs carrying this stream's nonce out of PTY output. Anything else, including an
/// APC a program printed with a guessed nonce, stays output.
pub struct ApcFilter {
    prefix: Vec<u8>,
    /// A possible start of the prefix held back from the previous feed.
    partial: Vec<u8>,
    /// The body of an APC being read (after the prefix), while inside one.
    body: Option<Vec<u8>>,
}

impl ApcFilter {
    pub fn new(nonce: u64) -> Self {
        Self { prefix: format!("\x1b_rex1;{nonce};").into_bytes(), partial: Vec::new(), body: None }
    }

    pub fn feed(&mut self, bytes: &[u8], chunks: &mut Vec<Chunk>) {
        let joined;
        let mut input: &[u8] = if self.partial.is_empty() {
            bytes
        } else {
            let mut held = std::mem::take(&mut self.partial);
            held.extend_from_slice(bytes);
            joined = held;
            &joined
        };
        let mut output = Vec::new();
        while !input.is_empty() {
            if let Some(body) = &mut self.body {
                // Inside an APC: read up to ESC '\'.
                match find_st(body.last().copied(), input) {
                    Some(end) => {
                        body.extend_from_slice(&input[..end]);
                        input = &input[end..];
                        let body = self.body.take().unwrap_or_default();
                        let json = &body[..body.len() - 2];
                        match serde_json::from_slice::<Value>(json) {
                            Ok(value) => {
                                flush(&mut output, chunks);
                                chunks.push(Chunk::Meta(value));
                            }
                            Err(_) => {
                                // Ours by nonce but not JSON: drop it, it was never output.
                            }
                        }
                    }
                    None => {
                        body.extend_from_slice(input);
                        input = &[];
                        if body.len() > APC_LIMIT {
                            // Not a real Rex APC; give the bytes back to the terminal.
                            output.extend_from_slice(&self.prefix);
                            output.append(body);
                            self.body = None;
                        }
                    }
                }
                continue;
            }
            let Some(esc) = input.iter().position(|&b| b == 0x1b) else {
                output.extend_from_slice(input);
                break;
            };
            output.extend_from_slice(&input[..esc]);
            input = &input[esc..];
            let n = input.len().min(self.prefix.len());
            if input[..n] != self.prefix[..n] {
                output.push(0x1b);
                input = &input[1..];
            } else if n < self.prefix.len() {
                // The end of this feed could start our prefix: wait for more.
                self.partial = input.to_vec();
                input = &[];
            } else {
                input = &input[self.prefix.len()..];
                self.body = Some(Vec::new());
            }
        }
        flush(&mut output, chunks);
    }
}

/// The index just past the first `ESC \` in `bytes`, where `last` is the byte before them.
fn find_st(last: Option<u8>, bytes: &[u8]) -> Option<usize> {
    if last == Some(0x1b) && bytes.first() == Some(&b'\\') {
        return Some(1);
    }
    bytes.windows(2).position(|w| w == b"\x1b\\").map(|i| i + 2)
}

fn flush(output: &mut Vec<u8>, chunks: &mut Vec<Chunk>) {
    if !output.is_empty() {
        chunks.push(Chunk::Output(std::mem::take(output)));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    const NONCE: u64 = 18070555269925590767;

    fn apc(json: &str) -> Vec<u8> {
        format!("\x1b_rex1;{NONCE};{json}\x1b\\").into_bytes()
    }

    /// Feed `bytes` split at every position and check the chunks join to the same result.
    fn split_everywhere(bytes: &[u8]) -> (Vec<u8>, Vec<Value>) {
        let mut reference = None;
        for cut in 0..=bytes.len() {
            let mut filter = ApcFilter::new(NONCE);
            let mut chunks = Vec::new();
            filter.feed(&bytes[..cut], &mut chunks);
            filter.feed(&bytes[cut..], &mut chunks);
            let mut output = Vec::new();
            let mut meta = Vec::new();
            for chunk in chunks {
                match chunk {
                    Chunk::Output(b) => output.extend(b),
                    Chunk::Meta(v) => meta.push(v),
                    Chunk::Snapshot(_) => unreachable!(),
                }
            }
            match &reference {
                None => reference = Some((output, meta)),
                Some(r) => assert_eq!(r, &(output, meta), "split at {cut}"),
            }
        }
        reference.unwrap()
    }

    #[test]
    fn apcs_come_out_anywhere() {
        let mut bytes = b"hello \x1b[1mbold\x1b[0m".to_vec();
        bytes.extend(apc(r#"{"type":"resize","cols":100,"rows":30}"#));
        bytes.extend(b"\x1b\\after\x1b");
        bytes.extend(apc(r#"{"type":"child_exited","exit_code":0}"#));
        let (output, meta) = split_everywhere(&bytes);
        assert_eq!(output, b"hello \x1b[1mbold\x1b[0m\x1b\\after\x1b");
        assert_eq!(meta, vec![json!({"type":"resize","cols":100,"rows":30}), json!({"type":"child_exited","exit_code":0})]);
    }

    #[test]
    fn foreign_nonces_and_lookalikes_are_output() {
        let bytes = b"\x1b_rex1;12345;{\"type\":\"resize\"}\x1b\\ \x1b_rex2;x\x1b\\ \x1b]0;title\x07".to_vec();
        let (output, meta) = split_everywhere(&bytes);
        assert_eq!(output, bytes);
        assert!(meta.is_empty());
    }

    #[test]
    fn snapshot_then_apc_then_output() {
        let mut snapshot = ENVELOPE.to_vec();
        for (tag, payload) in [(1u16, b"abc".as_slice()), (2, b""), (FINISH_TAG, b"")] {
            snapshot.extend(tag.to_le_bytes());
            snapshot.extend((payload.len() as u32).to_le_bytes());
            snapshot.extend(0u32.to_le_bytes());
            snapshot.extend(payload);
        }
        let mut stream = snapshot.clone();
        stream.extend(apc(r#"{"type":"resize","cols":80,"rows":24}"#));
        stream.extend(b"$ ");
        for cut in 0..stream.len() {
            let mut decoder = StreamDecoder::new(NONCE);
            let mut chunks = decoder.feed(&stream[..cut]).unwrap();
            chunks.extend(decoder.feed(&stream[cut..]).unwrap());
            let mut out = Vec::new();
            let mut got_snapshot = None;
            let mut meta = Vec::new();
            for chunk in chunks {
                match chunk {
                    Chunk::Snapshot(s) => got_snapshot = Some(s),
                    Chunk::Output(b) => out.extend(b),
                    Chunk::Meta(v) => meta.push(v),
                }
            }
            assert_eq!(got_snapshot.as_deref(), Some(snapshot.as_slice()), "cut {cut}");
            assert_eq!(meta.len(), 1, "cut {cut}");
            assert_eq!(out, b"$ ", "cut {cut}");
        }
    }

    #[test]
    fn rejects_non_snapshots() {
        assert!(StreamDecoder::new(1).feed(b"plain output").is_err());
        assert!(StreamDecoder::new(1).feed(b"GHOST").unwrap().is_empty());
    }

    #[test]
    fn oversized_apc_is_given_back() {
        let mut filter = ApcFilter::new(NONCE);
        let mut chunks = Vec::new();
        let mut bytes = format!("\x1b_rex1;{NONCE};").into_bytes();
        bytes.extend(vec![b'x'; APC_LIMIT + 10]);
        filter.feed(&bytes, &mut chunks);
        let total: usize = chunks.iter().map(|c| if let Chunk::Output(b) = c { b.len() } else { 0 }).sum();
        assert_eq!(total, bytes.len());
    }
}
