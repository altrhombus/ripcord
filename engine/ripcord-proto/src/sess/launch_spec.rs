//! The launch spec: the JSON the console reads the session's configuration from, including the
//! handshake key that authenticates the ECDH exchange. Ported from
//! `libripcord/session/halyard_launch_spec.c`; `control-proto.kat`'s `launchspec` lines compare it byte for
//! byte with the .NET output. It is sent encrypted with the streaminfo cipher and base64'd, inside
//! SESSION_REQUEST.

use crate::base64;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Params {
    pub width: i32,
    pub height: i32,
    pub fps: i32,
    /// network.bwKbpsSent.
    pub bitrate_kbps: i32,
    /// network.mtu, the declared form.
    pub mtu: i32,
    /// network.rtt; 0 when senkusha produced no sample.
    pub rtt_ms: i32,
    pub hevc: bool,
    /// "HDR" is an inference on the .NET side, never observed.
    pub hdr: bool,
}

const STANDARD_LADDER: [(i32, i32); 4] = [(640, 360), (960, 540), (1280, 720), (1920, 1080)];

fn resolution(width: i32, height: i32, fps: i32, score: usize) -> String {
    format!(r#"{{"resolution":{{"width":{width},"height":{height}}},"maxFps":{fps},"score":{score}}}"#)
}

/// streamResolutions: every standard rung below the requested height, then the requested one, scored
/// 1, 2, ... in that order.
pub fn resolutions(width: i32, height: i32, fps: i32) -> String {
    let mut entries: Vec<String> = STANDARD_LADDER
        .iter()
        .filter(|&&(_, h)| h < height)
        .enumerate()
        .map(|(i, &(w, h))| resolution(w, h, fps, i + 1))
        .collect();
    entries.push(resolution(width, height, fps, entries.len() + 1));
    entries.join(",")
}

pub fn build(p: &Params, handshake_key: &[u8; 16]) -> String {
    format!(
        concat!(
            r#"{{"sessionId":"sessionId4321","streamResolutions":[{ladder}],"network":{{"bwKbpsSent":{bitrate},"#,
            r#""bwLoss":0.001000,"mtu":{mtu},"rtt":{rtt},"ports":[53,2053]}},"slotId":1,"appSpecification":{{"#,
            r#""minFps":{fps},"minBandwidth":0,"extTitleId":"ps3","version":1,"timeLimit":1,"startTimeout":100,"#,
            r#""afkTimeout":100,"afkTimeoutDisconnect":100}},"konan":{{"ps3AccessToken":"accessToken","#,
            r#""ps3RefreshToken":"refreshToken"}},"requestGameSpecification":{{"model":"bravia_tv","#,
            r#""platform":"android","audioChannels":"5.1","language":"sp","acceptButton":"X","#,
            r#""connectedControllers":["xinput","ds3","ds4"],"yuvCoefficient":"bt601","#,
            r#""videoEncoderProfile":"hw4.1","audioEncoderProfile":"audio1"}},"userProfile":{{"#,
            r#""onlineId":"psnId","npId":"npId","region":"US","languagesUsed":["en","jp"]}},"#,
            r#""videoCodec":"{codec}","dynamicRange":"{range}","handshakeKey":"{key}","audioChannels":{{"#,
            r#""name":"default","encoderType":"opus","audioChannelSettings":[{{"audioChannelType":0,"#,
            r#""isSigned":false,"sampleRate":48000,"sampleSize":2,"channels":2,"maxFrameDataSize":1920,"#,
            r#""samplesPerFrame":480,"bitrate":64,"isRawPcm":false,"fecMode":2}}]}}}}"#
        ),
        ladder = resolutions(p.width, p.height, p.fps),
        bitrate = p.bitrate_kbps,
        mtu = p.mtu,
        rtt = p.rtt_ms,
        fps = p.fps,
        codec = if p.hevc { "hevc" } else { "avc" },
        range = if p.hdr { "HDR" } else { "SDR" },
        key = base64::encode(handshake_key),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_ladder_lists_lower_rungs_then_the_request() {
        let l = resolutions(1280, 720, 60);
        assert!(l.starts_with(r#"{"resolution":{"width":640,"height":360},"maxFps":60,"score":1}"#));
        assert!(l.ends_with(r#"{"resolution":{"width":1280,"height":720},"maxFps":60,"score":3}"#));
        assert_eq!(resolutions(640, 360, 30).matches("resolution").count(), 1);
    }
}
