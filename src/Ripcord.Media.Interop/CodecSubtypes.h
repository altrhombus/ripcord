#pragma once

// Shared codec -> Media Foundation subtype mapping.
//
// Two callers need it and must agree: VideoRenderer::CreateDecoderForCodec, which activates a decoder, and
// VideoCapabilities::IsCodecDecodeAvailable, which reports whether one exists. If those two disagreed the UI
// would offer a codec that then fails at connect, so the list lives in one place rather than being written
// twice.
//
// Both the Annex B and the "_ES" (elementary stream) subtype are listed because they are distinct GUIDs and a
// given MFT registers for one or the other; callers try them in order.
//
// The namespace is deliberately NOT Ripcord::... — the implementation code lives inside
// winrt::Ripcord::Media::Interop, where the name "Ripcord" resolves to winrt::Ripcord first and would shadow a
// global Ripcord namespace. A distinct top-level name avoids that trap entirely.

namespace RipcordCodecSupport
{
    struct SubtypePair
    {
        GUID Primary;
        GUID ElementaryStream;
    };

    inline SubtypePair SubtypesFor(winrt::Ripcord::Media::Interop::VideoCodecKind codec)
    {
        if (codec == winrt::Ripcord::Media::Interop::VideoCodecKind::Hevc)
        {
            return { MFVideoFormat_HEVC, MFVideoFormat_HEVC_ES };
        }

        return { MFVideoFormat_H264, MFVideoFormat_H264_ES };
    }
}
