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
} halyard_control_event;

typedef struct {
    int sock;                      /* the /sess/ctrl connection, non-blocking, kept open */
    halyard_control_field ctrl;    /* the field/streaminfo cipher for this connection */
    uint64_t next_counter;         /* the next unused cipher counter - see the header note */
    int session_ready;             /* set once SESSION_ID has been seen */
    uint8_t buffer[HALYARD_CONTROL_SESSION_BUFFER];
    size_t buffered;
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
 * One non-blocking step. Answers HEARTBEAT_REQ automatically (the caller never has to remember to), and
 * reports anything else through `out_event`. Returns 1 if the session is still healthy, 0 if it has
 * ended - in which case out_event says whether that was a clean close or an error.
 *
 * Call this in the caller's main loop, including while it is busy with the UDP stream work; the console
 * does not stop expecting heartbeats just because the stream is coming up.
 */
int halyard_control_session_service(halyard_control_session *session, halyard_control_event *out_event);

/* Sends a binary control frame. Returns 1 on success, 0 on failure. */
int halyard_control_session_send(halyard_control_session *session, unsigned type,
                                 const uint8_t *payload, size_t payload_length);

/* Closes the connection and wipes the key material. Safe on an already-closed session. */
void halyard_control_session_close(halyard_control_session *session);

#endif /* HALYARD_CONTROL_SESSION_H */
