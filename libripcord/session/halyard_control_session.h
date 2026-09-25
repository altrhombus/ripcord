/*
 * ripcord-3ds - the control-plane session, as a reusable object.
 *
 * This is the orchestration that source/session/main.c proved against a real PS5 on 2026-08-12 (arm ->
 * /sess/init -> /sess/ctrl -> the persistent binary channel), lifted out so the connect flow in
 * source/connect/ can run the same sequence and then keep going into the stream plane.
 *
 * WHY EXTRACT RATHER THAN COPY. This sequence is the only part of the port with hardware evidence behind
 * it. A second hand-maintained copy would drift from the validated one silently, and the drift would
 * only ever show up as a console rejecting a session for no visible reason. source/session/main.c
 * remains the known-good reference program; if it and source/connect/ ever disagree about the control
 * plane, that one is right.
 *
 * SINGLE-THREADED AND NON-BLOCKING BY DESIGN. Everything here polls: the socket is non-blocking from the
 * moment it connects, and service() does at most one unit of work per call. That is what lets one thread
 * hold this channel open - answering HEARTBEAT_REQ, which a console requires or it resets the session
 * ~15-30 s in - while simultaneously driving the UDP senkusha/Takion work that follows. The 3DS has no
 * spare core to put a blocking read on, and this port creates no threads anywhere.
 *
 * THE COUNTER IS THE SUBTLE PART. The control-field cipher runs one counter per CONNECTION, not per
 * field or per message type: the five /sess/ctrl headers consume 0-4 (halyard_sess_fields.h), and
 * anything encrypted later on this same connection - a login passcode, the launch spec - continues from
 * `next_counter` below. Restarting it reuses an IV, which is a real key-recovery bug and looks like
 * nothing at all on the wire. Take the value from here; never hardcode one.
 */
#ifndef HALYARD_CONTROL_SESSION_H
#define HALYARD_CONTROL_SESSION_H

#include "../halyard/halyard_v1.h"
#include "halyard_pairing_file.h"

#include <stddef.h>
#include <stdint.h>

#define HALYARD_CONTROL_SESSION_BUFFER 4096

/*
 * How much of a frame's payload service() will decrypt for the caller.
 *
 * Every payload-carrying frame SPENDS a counter whether or not this decrypts it - the console's sequence
 * does not care what we can hold - so a payload larger than this advances the counter and arrives with
 * plaintext_length 0 rather than desynchronising everything after it. The frames anyone reads today are
 * far smaller than this: the login verdict is one byte.
 */
#define HALYARD_CONTROL_PLAINTEXT_MAX 256

/* How many parsed frames of history the resync diagnostic keeps - see `recent` below. */
#define HALYARD_CONTROL_RECENT 4

/*
 * THE BYTE PIPE: what the control session asks of the transport underneath it, and nothing else.
 *
 * WHY A SEAM, AND WHY THIS NARROW. A LAN console serves /sess/init, /sess/ctrl and the binary frames on
 * TCP 9295. A console reached through the cloud rendezvous serves the same three things over the UDP 9303
 * association (halyard_dgram_channel.h) and refuses 9295 with a generic 403. Everything that knows about
 * HTTP, the frame format and the counters is identical on both, and lives in this file; the only thing
 * that differs is what fills the buffer. So the seam is a byte pipe - the same cut the .NET side made with
 * IHalyardControlChannel and its two implementations, HalyardTcpControlChannel and
 * HalyardDatagramSessionControlChannel - and duplicating the HTTP or frame knowledge per transport is how
 * two codecs would drift apart.
 *
 *   open      ONE REQUEST CONNECTION. /sess/init and /sess/ctrl each get a fresh one (init is served
 *             Connection: close). TCP: a connect to host:port, made non-blocking. Datagram: a fresh chunk
 *             connection over the existing prelude (HalyardDatagramSessionControlChannel.ConnectAsync).
 *             Returns a handle >= 0 - for TCP the socket, for the datagram pipe the UDP socket a host can
 *             wait on - or -1. May block, bounded by the transport's own deadline.
 *   send_all  every byte, or failure: 0 on success, -1 on failure (rc_tcp_send_all's contract).
 *   recv      MUST NOT BLOCK: > 0 bytes read into `buffer`; 0 nothing available right now; -1 an error;
 *             -2 the peer closed the connection.
 *   close     end the connection. `polite` is 0 for /sess/init, which the console closes itself, and 1
 *             otherwise: on TCP closing the socket says everything, but on datagrams nothing says it
 *             implicitly, and a console never told keeps the session live and refuses further cloud
 *             sessions until it is rebooted (HalyardDatagramSessionControlChannel.DisposeAsync).
 *   name      for log lines only ("TCP", "datagram").
 *
 * The TCP pipe below is not a new implementation: its four functions are the four call sites this file
 * always had (rc_tcp_connect + rc_socket_set_nonblocking, rc_tcp_send_all, rc_tcp_recv, close), moved
 * behind the pointer unchanged, so the LAN path's syscalls are the same in the same order.
 */
typedef struct {
    const char *name;
    int (*open)(void *ctx, const char *host, unsigned short port);
    int (*send_all)(void *ctx, int handle, const uint8_t *data, size_t length);
    long (*recv)(void *ctx, int handle, uint8_t *buffer, size_t capacity);
    void (*close)(void *ctx, int handle, int polite);
    void *ctx;
} halyard_control_pipe;

/* TCP to the console, the LAN default and the hardware-validated path. `ctx` is unused (NULL). */
extern const halyard_control_pipe halyard_control_pipe_tcp;

/*
 * How to open, beyond the pairing record. A zeroed struct - or passing NULL - is exactly
 * halyard_control_session_open(): TCP 9295, the ARM probe first, RP-ConPath 1, the record's host as the
 * Host header, and "RP-Version" on /sess/init.
 *
 * The datagram route differs in four places, each taken from HalyardStreamingSession.ConnectAsync and its
 * request builders rather than decided here:
 *
 *   skip_arm_probe       the probe arms a TCP listener; on the rendezvous route the control plane rides
 *                        the 9303 association instead, and off-network the probe fires at an address that
 *                        may not route. .NET sends it only when ConnectionPath is Local.
 *   connection_path      RP-ConPath: 1 LOCAL, 3 RENDEZVOUS ([W] both seen on the wire; 2 unclaimed). The
 *                        console runs a different A/V bring-up for each, so this is not decoration.
 *   host_header          .NET's HostHeader(): the address right-aligned in three columns with the port
 *                        (halyard_dgram_host_header). A LAN console accepts the plain form this file has
 *                        always sent, so the TCP path keeps it.
 *   init_version_header  .NET sends "Rp-Version" on /sess/init (as the captured client does) and
 *                        "RP-Version" on /sess/ctrl. The TCP path's "RP-Version" is hardware-validated and
 *                        stays; the datagram route follows the capture.
 *
 * `port` exists so the host suite can run the TCP pipe against a loopback console on a port it owns.
 */
typedef struct {
    const halyard_control_pipe *pipe;   /* NULL: halyard_control_pipe_tcp */
    unsigned short port;                /* 0: HALYARD_CONTROL_ARM_PORT (9295) */
    int skip_arm_probe;
    int connection_path;                /* 0: 1 */
    const char *host_header;            /* NULL: record->host */
    const char *init_version_header;    /* NULL: "RP-Version" */
} halyard_control_open_options;

/* What service() observed on this call. */
typedef enum {
    HALYARD_CONTROL_EVENT_NONE = 0,     /* nothing happened; the common case, call again */
    HALYARD_CONTROL_EVENT_MESSAGE,      /* a frame arrived; type/payload are set */
    HALYARD_CONTROL_EVENT_SESSION_READY,/* SESSION_ID seen - the console is willing to stream */
    HALYARD_CONTROL_EVENT_CLOSED,       /* the console closed the connection */
    HALYARD_CONTROL_EVENT_ERROR         /* socket or protocol error; the session is unusable */
} halyard_control_event_kind;

typedef struct {
    halyard_control_event_kind kind;
    unsigned type;                 /* the frame's message type, for MESSAGE/SESSION_READY */
    const uint8_t *payload;        /* points INTO the session's buffer; valid until the next service() */
    size_t payload_length;

    /*
     * The payload decrypted, for frames that carry one. Points into the session; same lifetime as
     * `payload`. Zero-length means there was nothing to decrypt, the payload was larger than
     * HALYARD_CONTROL_PLAINTEXT_MAX, or the field cipher is not established - all three are ordinary,
     * and none of them is distinguishable here on purpose, because the caller's response to each is the
     * same: it has no plaintext to read.
     */
    const uint8_t *plaintext;
    size_t plaintext_length;
    uint64_t counter;              /* the console-direction counter this frame was decrypted at */
} halyard_control_event;

typedef struct {
    int sock;                      /* the /sess/ctrl connection, non-blocking, kept open */
    halyard_control_field ctrl;    /* the field/streaminfo cipher for this connection */
    uint64_t next_counter;         /* the next unused cipher counter - see the header note */

    /*
     * The console's direction has its own counter, and it is NOT next_counter. See
     * HALYARD_SESS_COUNTER_CONSOLE_START for why it starts at 1 while ours starts at 0.
     */
    uint64_t recv_counter;
    uint8_t recv_plain[HALYARD_CONTROL_PLAINTEXT_MAX];

    int session_ready;

    /*
     * Heartbeat replies that FAILED to send. The reply is fire-and-forget - rc_tcp_send_all retries a
     * full non-blocking send buffer but eventually returns -1, and the caller cannot see it. A console
     * that stops hearing our replies stops asking and closes the session ~37 s later, which is
     * indistinguishable from "the console lost interest" unless this is counted.
     */
    long heartbeat_send_failures;

    /*
     * TCP receive activity, to separate "the console sent nothing" from "we failed to read it".
     *
     * Those look identical from outside and want opposite fixes. Hardware shows A/V flowing normally
     * while the TCP control channel goes silent for exactly ~38 s and is then closed - so either the
     * console stopped writing to a socket it was still reading, or we stopped draining one it was still
     * writing to. Counting the reads and the bytes settles it.
     */
    long recv_calls;
    long recv_bytes;
    long recv_would_block;

    /* Control-frame resynchronisations, and how many bytes they threw away. Should be zero; a non-zero
     * count means the desync described in the .c is still happening and has merely been survived. */
    long resyncs;
    long resync_discarded;             /* set once SESSION_ID has been seen */

    /*
     * THE LAST FEW FRAMES PARSED, so a resync can name what preceded it.
     *
     * An 80-minute session resynced 14 times and discarded EXACTLY 8 bytes each time - never 7, never 9.
     * A constant is a field, not corruption: some message's real length exceeds its declared length by
     * one header's worth, and the next frame starts 8 bytes late. Which message is the whole question,
     * and it is answerable without a packet capture, because the culprit is simply whatever we parsed
     * immediately before. Recording a few frames of history rather than one gives the surrounding
     * sequence too - a rare message type is only suspicious if it is rare in the same way each time.
     *
     * Newest at index 0. Kept small: this sits in a struct the session owns, and 4 entries is enough to
     * see a pattern in 14 samples.
     */
    struct {
        unsigned type;
        size_t payload_length;         /* as DECLARED by the header */
        size_t consumed;               /* header + declared payload - what we actually advanced by */
    } recent[HALYARD_CONTROL_RECENT];
    int recent_count;
    uint8_t buffer[HALYARD_CONTROL_SESSION_BUFFER];
    size_t buffered;

    /* The transport `sock` belongs to - see halyard_control_pipe. Set by open; NULL reads as TCP, so a
     * session some older caller zeroed by hand behaves as it always did. Last, so the fields above keep
     * their offsets. */
    const halyard_control_pipe *pipe;
} halyard_control_session;

/*
 * Runs arm -> /sess/init -> /sess/ctrl and leaves the binary channel open and ready to service().
 * Returns 1 on success, 0 on failure (having logged where it stopped). On failure nothing is left open.
 *
 * On success `ctrl` is initialised from the console's RP-Nonce and the record's companion, and
 * `next_counter` is positioned after the five /sess/ctrl fields.
 */
int halyard_control_session_open(const halyard_pairing_record *record, halyard_control_session *out);

/*
 * halyard_control_session_open over a chosen pipe and with the route's request differences - see
 * halyard_control_open_options. NULL options is halyard_control_session_open exactly. The pipe must
 * outlive the session.
 */
int halyard_control_session_open_with(const halyard_pairing_record *record,
                                      const halyard_control_open_options *options,
                                      halyard_control_session *out);

/*
 * One non-blocking step. Answers HEARTBEAT_REQ automatically (the caller never has to remember to), and
 * reports anything else through `out_event`. Returns 1 if the session is still healthy, 0 if it has
 * ended - in which case out_event says whether that was a clean close or an error.
 *
 * Call this in the caller's main loop, including while it is busy with the UDP stream work; the console
 * does not stop expecting heartbeats just because the stream is coming up.
 */
int halyard_control_session_service(halyard_control_session *session, halyard_control_event *out_event);

/*
 * ANSWER THE SIGN-IN GATE: encrypt `pin` as a control field and send it as TYPE_LOGIN_SUBMIT.
 *
 * A console whose account is locked sends TYPE_LOGIN_PROMPT and then, per halyard_ctrl_message.h,
 * "silently drops every stream handshake" until this is answered. Nothing about that is visible except a
 * session that opens, stays healthy, and never reaches SESSION_ID - which is precisely the state
 * ports/ripcord-ps3 reached against a real console, and could not distinguish from a slow one.
 *
 * PORTED FROM THE REFERENCE, NOT FROM ANOTHER PORT. src/Ripcord.Protocol.Halyard/Session/
 * HalyardStreamingSession.cs is what this follows; ports/ripcord-3ds treats the prompt as fatal, which
 * is that port's limitation rather than the protocol's.
 *
 * THE COUNTER ADVANCES ON EVERY CALL, INCLUDING RETRIES, and that is the part to be careful with. It is
 * one running per-connection value: the five /sess/ctrl headers consumed 0-4, so the first submit is 5,
 * and the reference is explicit that "each retry MUST advance it - a fresh IV per submit, never reused".
 * Reusing a counter reuses an IV under the same key, which is a real key-recovery bug and looks like
 * nothing at all on the wire. This function takes the value from the session and advances it there; no
 * caller should be passing one in.
 *
 * The caller is expected to be watching for the console's answer already - TYPE_LOGIN carries its
 * verdict and SESSION_ID means outright success. The reference arms that wait BEFORE submitting,
 * because on a LAN the console replies in milliseconds and a result that arrives while the listener is
 * still being set up is simply lost.
 *
 * `pin` is ASCII digits. Returns 1 if the frame was sent, 0 on a bad passcode, a session without
 * established control crypto, or a send failure.
 */
int halyard_control_session_submit_login(halyard_control_session *session,
                                         const char *pin, size_t pin_length);

/* Sends a binary control frame. Returns 1 on success, 0 on failure. */
int halyard_control_session_send(halyard_control_session *session, unsigned type,
                                 const uint8_t *payload, size_t payload_length);

/*
 * Sends a frame whose payload is a control FIELD: `plaintext` encrypted at the session's next counter,
 * which is taken and advanced here, as submit_login does and for the same reason - a counter reused is
 * an IV reused. The rendezvous route's PROBE_REPORT is the caller (HalyardStreamingSession.
 * SendProbeReportAsync: EncryptControlField(_clientFieldCounter++, ...)). At most
 * HALYARD_CONTROL_PLAINTEXT_MAX bytes. Returns 1 if the frame was sent.
 */
int halyard_control_session_send_field(halyard_control_session *session, unsigned type,
                                       const uint8_t *plaintext, size_t length);

/* Closes the connection and wipes the key material. Safe on an already-closed session. */
void halyard_control_session_close(halyard_control_session *session);

#endif /* HALYARD_CONTROL_SESSION_H */
