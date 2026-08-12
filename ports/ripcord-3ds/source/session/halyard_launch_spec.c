/* See halyard_launch_spec.h - especially the note on the OFB counter and the adaptiveStreamMode
 * divergence, both of which are the first suspects if a console goes quiet after /sess/ctrl. */

#include "halyard_launch_spec.h"

#include "../util/rc_base64.h"

#include <stdio.h>
#include <string.h>

/* The standard ladder, in ascending order (HalyardStreamingSession.StandardLadder). */
static const struct { int width; int height; } kStandardLadder[] = {
    { 640, 360 },
    { 960, 540 },
    { 1280, 720 },
    { 1920, 1080 },
};
#define LADDER_COUNT (sizeof(kStandardLadder) / sizeof(kStandardLadder[0]))

/* Appends to a bounded buffer. Returns 0 and leaves *offset past the end if it did not fit, which the
 * callers check once at the end rather than after every field. */
static int append(char *out, size_t out_size, size_t *offset, const char *text)
{
    size_t length = strlen(text);

    if (*offset + length + 1 > out_size) {
        *offset = out_size + 1; /* poison, so the final check fails */
        return 0;
    }
    memcpy(out + *offset, text, length);
    *offset += length;
    out[*offset] = '\0';
    return 1;
}

static int append_int(char *out, size_t out_size, size_t *offset, int value)
{
    char text[16];
    snprintf(text, sizeof(text), "%d", value);
    return append(out, out_size, offset, text);
}

size_t halyard_launch_spec_resolutions(int width, int height, int fps, char *out, size_t out_size)
{
    size_t offset = 0;
    size_t i;
    int written = 0;

    if (out == NULL || out_size == 0)
        return 0;
    out[0] = '\0';

    for (i = 0; i < LADDER_COUNT; i++) {
        /* Strictly below the request: the request itself is appended last so it scores highest even
         * when it is not one of the standard rungs. */
        if (kStandardLadder[i].height >= height)
            continue;
        if (written > 0)
            append(out, out_size, &offset, ",");
        append(out, out_size, &offset, "{\"resolution\":{\"width\":");
        append_int(out, out_size, &offset, kStandardLadder[i].width);
        append(out, out_size, &offset, ",\"height\":");
        append_int(out, out_size, &offset, kStandardLadder[i].height);
        append(out, out_size, &offset, "},\"maxFps\":");
        append_int(out, out_size, &offset, fps);
        append(out, out_size, &offset, ",\"score\":");
        append_int(out, out_size, &offset, written + 1);
        append(out, out_size, &offset, "}");
        written++;
    }

    if (written > 0)
        append(out, out_size, &offset, ",");
    append(out, out_size, &offset, "{\"resolution\":{\"width\":");
    append_int(out, out_size, &offset, width);
    append(out, out_size, &offset, ",\"height\":");
    append_int(out, out_size, &offset, height);
    append(out, out_size, &offset, "},\"maxFps\":");
    append_int(out, out_size, &offset, fps);
    append(out, out_size, &offset, ",\"score\":");
    append_int(out, out_size, &offset, written + 1);
    append(out, out_size, &offset, "}");

    if (offset > out_size)
        return 0;
    return offset;
}

size_t halyard_launch_spec_build(const halyard_launch_spec_params *params,
                                 const uint8_t handshake_key[16],
                                 char *out, size_t out_size)
{
    size_t offset = 0;
    char handshake_b64[32];
    char ladder[512];

    if (params == NULL || handshake_key == NULL || out == NULL || out_size == 0)
        return 0;
    out[0] = '\0';

    if (halyard_launch_spec_resolutions(params->width, params->height, params->fps,
                                        ladder, sizeof(ladder)) == 0) {
        return 0;
    }
    if (rc_base64_encode(handshake_key, 16, handshake_b64, sizeof(handshake_b64)) == 0)
        return 0;

    /* Key order is fixed and matches the capture. Nothing here is whitespace-formatted: the document
     * goes on the wire as one line. */
    append(out, out_size, &offset, "{\"sessionId\":\"sessionId4321\",\"streamResolutions\":[");
    append(out, out_size, &offset, ladder);
    append(out, out_size, &offset, "],\"network\":{\"bwKbpsSent\":");
    append_int(out, out_size, &offset, params->bitrate_kbps);
    /* bwLoss is the literal string 0.001000 - six decimals, hardcoded on the .NET side too. Printing a
     * float here would risk a locale-dependent decimal comma, which newlib can do. */
    append(out, out_size, &offset, ",\"bwLoss\":0.001000,\"mtu\":");
    append_int(out, out_size, &offset, params->mtu);
    append(out, out_size, &offset, ",\"rtt\":");
    append_int(out, out_size, &offset, params->rtt_ms);
    append(out, out_size, &offset, ",\"ports\":[53,2053]},\"slotId\":1,\"appSpecification\":{\"minFps\":");
    append_int(out, out_size, &offset, params->fps);
    append(out, out_size, &offset,
        ",\"minBandwidth\":0,\"extTitleId\":\"ps3\",\"version\":1,\"timeLimit\":1,\"startTimeout\":100,"
        "\"afkTimeout\":100,\"afkTimeoutDisconnect\":100},"
        "\"konan\":{\"ps3AccessToken\":\"accessToken\",\"ps3RefreshToken\":\"refreshToken\"},"
        "\"requestGameSpecification\":{\"model\":\"bravia_tv\",\"platform\":\"android\","
        "\"audioChannels\":\"5.1\",\"language\":\"sp\",\"acceptButton\":\"X\","
        "\"connectedControllers\":[\"xinput\",\"ds3\",\"ds4\"],\"yuvCoefficient\":\"bt601\","
        "\"videoEncoderProfile\":\"hw4.1\",\"audioEncoderProfile\":\"audio1\"},"
        "\"userProfile\":{\"onlineId\":\"psnId\",\"npId\":\"npId\",\"region\":\"US\","
        "\"languagesUsed\":[\"en\",\"jp\"]},\"videoCodec\":\"");
    append(out, out_size, &offset, params->is_hevc ? "hevc" : "avc");
    append(out, out_size, &offset, "\",\"dynamicRange\":\"");
    append(out, out_size, &offset, params->is_hdr ? "HDR" : "SDR");
    append(out, out_size, &offset, "\",\"handshakeKey\":\"");
    append(out, out_size, &offset, handshake_b64);
    append(out, out_size, &offset,
        "\",\"audioChannels\":{\"name\":\"default\",\"encoderType\":\"opus\","
        "\"audioChannelSettings\":[{\"audioChannelType\":0,\"isSigned\":false,\"sampleRate\":48000,"
        "\"sampleSize\":2,\"channels\":2,\"maxFrameDataSize\":1920,\"samplesPerFrame\":480,"
        "\"bitrate\":64,\"isRawPcm\":false,\"fecMode\":2}]}}");

    if (offset > out_size)
        return 0;
    return offset;
}
