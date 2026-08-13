/*
 * ripcord-3ds - the provisional pairing-record file.
 *
 * Reads "pairing.txt" from next to the running .3dsx. This is NOT the "import a pairing record from a
 * desktop Ripcord install" backlog item - that is a separate, still-unstarted piece of work with a
 * similar name. This is a plain key=value text file for exercising the on-device probes, and the values
 * are transcribed by hand from `ProtocolLab -- register`'s output:
 *
 *   host=192.168.1.42          (required - the console's LAN address)
 *   platform=ps5               (optional, "ps5" or "ps4"; default ps5)
 *   registkey=1a2b3c4d5e6f0011 (required - hex, at most 8 bytes)
 *   companion=...32 hex chars  (required - hex, exactly 16 bytes)
 *   deviceid=...hex            (optional - up to 16 bytes; defaults to all-zero)
 *   osmajor=10, osminor=0, bitrate=10000, streamingtype=0   (all optional, shown defaults)
 *   streambitrate=2000         (optional - the launch spec's bwKbpsSent, in kbps)
 *   proberesolutions=1         (optional - ask the console for a series of resolutions and report
 *                               what it accepts, instead of streaming; see source/connect/main.c)
 *
 * `bitrate` and `streambitrate` are NOT the same field and default differently on purpose. `bitrate` is
 * the /sess/ctrl RP-StartBitrate header; `streambitrate` is what the launch spec asks the console to
 * actually send, and 2000 is chosen from this port's own Phase 2 link measurements (2.16% loss at 2 Mbps,
 * far worse above ~5) rather than from the vendor default of 10000, which asks a 2.4 GHz link for five
 * times what it was measured to carry.
 *
 * Lives here rather than inside a program's main.c because two on-device probes now read the same file
 * (source/session/main.c and source/connect/main.c) and a second hand-rolled copy of this parser would
 * be the obvious way to end up with two subtly different ideas of what a valid record is.
 *
 * ON THE LENGTH RULES, which have already cost one debugging session: rc_hex_decode returns "bad" for
 * malformed AND for over-long input, and this loader reports its three required fields in one message.
 * So an over-long registkey reports as a *missing* field. The message below names lengths explicitly for
 * that reason - see HARDWARE-PROBES.md, where it is called out as the likeliest silent failure.
 */
#ifndef HALYARD_PAIRING_FILE_H
#define HALYARD_PAIRING_FILE_H

#include <stddef.h>
#include <stdint.h>

#define HALYARD_PAIRING_HOST_MAX 64
#define HALYARD_PAIRING_REGISTKEY_MAX 8
#define HALYARD_PAIRING_COMPANION_LENGTH 16
#define HALYARD_PAIRING_DEVICE_ID_MAX 16

typedef struct {
    char host[HALYARD_PAIRING_HOST_MAX];
    int is_ps5;
    uint8_t registkey[HALYARD_PAIRING_REGISTKEY_MAX];
    size_t registkey_length;
    uint8_t companion[HALYARD_PAIRING_COMPANION_LENGTH];
    uint8_t device_id[HALYARD_PAIRING_DEVICE_ID_MAX];
    size_t device_id_length;
    int os_major;
    int os_minor;
    int start_bitrate;
    int stream_bitrate_kbps;
    int probe_resolutions;   /* 1 = sweep resolutions and exit, instead of streaming */
    int streaming_type;
} halyard_pairing_record;

/*
 * Loads the record from "pairing.txt" beside `argv0`. Returns 1 if host, registkey and companion were
 * all present and well-formed; 0 otherwise, having logged why. Optional fields are defaulted whether or
 * not the load succeeds.
 */
int halyard_pairing_file_load(const char *argv0, halyard_pairing_record *out_record);

#endif /* HALYARD_PAIRING_FILE_H */
