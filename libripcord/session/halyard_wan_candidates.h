/*
 * libripcord - the candidates of an internet (account-route) connect: which ones we offer, and which of
 * the console's we talk to.
 *
 * THE SPLIT. The signaling itself - the OFFER/ACCEPT/RESULT POSTs, the push WebSocket that carries the
 * console's OFFER back - is HTTPS and JSON, and on the Mac it is Swift's (docs/macos-plan.md, "The
 * split"). What is here is the two DECISIONS inside it that every client has to make identically and that
 * decide whether a distant console can reach us at all. Getting either wrong fails silently: our packets
 * still leave, and the symptom is a peer that was never told where to send.
 *
 * Ported from src/Ripcord.Protocol.Halyard/Discovery/HalyardAccountPairing.cs (OurCandidates,
 * PreferredCandidate) and src/Ripcord.Presentation.Halyard/Sessions/HalyardAccountConsoleSession.cs
 * (CandidateEndpoint), with HalyardDatagramRegistrationTransport.SharesSubnetWithLocalInterface.
 *
 * NO JSON, NO OS. A candidate arrives as plain data the cloud tier filled in from the console's OFFER, and
 * this host's interfaces arrive as a list the front end enumerated (getifaddrs on the Mac). Enumerating
 * interfaces and asking the routing table which address reaches the console are OS questions, not
 * protocol ones, and the core has no seam for either.
 */
#ifndef HALYARD_WAN_CANDIDATES_H
#define HALYARD_WAN_CANDIDATES_H

#include <stddef.h>
#include <stdint.h>

#define HALYARD_WAN_TYPE_MAX 16    /* "STUN", "STATIC", "LOCAL", or whatever a console sends */
#define HALYARD_WAN_ADDRESS_MAX 64 /* text: room for any IPv6 form, although only IPv4 is ever used */

/* The account route's control port, where a console advertises its candidates. A required on-wire value. */
#define HALYARD_WAN_CONTROL_PORT 9303u

/*
 * One candidate, as signaling carries it. The type stays a string, as in HalyardSignalingCandidate: an
 * unknown kind must round-trip and be ignored rather than fail, since a console may add kinds we have not
 * seen. The address stays text, exactly as the OFFER carried it; nothing here trusts it to parse.
 */
typedef struct {
    char type[HALYARD_WAN_TYPE_MAX];
    char address[HALYARD_WAN_ADDRESS_MAX];
    uint16_t port;
} halyard_wan_candidate;

/* One of this host's up IPv4 interfaces, both fields in network order. */
typedef struct {
    uint8_t address[4];
    uint8_t netmask[4];
} halyard_wan_interface;

/*
 * STRICT dotted-quad IPv4: four decimal parts of 1-3 digits, each <= 255, and nothing else. Returns 1 and
 * fills `out` (network order), or 0.
 *
 * DELIBERATELY NARROWER than .NET's IPAddress.TryParse, which also takes "10" as 0.0.0.10, octal- and
 * hex-looking parts, and IPv6. A candidate address comes from the network by way of the cloud; the C side
 * reads only the one form a console has ever sent, and anything else is treated as .NET treats an address
 * that does not parse at all - skipped for the subnet test.
 */
int halyard_wan_parse_ipv4(const char *text, uint8_t out[4]);

/* Formats `address` (network order) as a dotted quad into `out`. Returns the length, or 0 if it does not fit. */
size_t halyard_wan_format_ipv4(const uint8_t address[4], char *out, size_t out_size);

/*
 * SharesSubnetWithLocalInterface: whether `address` sits on a network this host is attached to, asked of
 * each interface's own mask rather than guessed from private ranges - a private address on someone else's
 * network is exactly as unreachable as a public one. An interface with a zero mask never matches.
 */
int halyard_wan_shares_subnet(const uint8_t address[4], const halyard_wan_interface *interfaces,
                              size_t interface_count);

#define HALYARD_WAN_OUR_CANDIDATES_MAX 3

/*
 * OurCandidates: what we offer, in the captured client's order -
 *
 *   STUN    the reflexive mapping the NAT actually assigned (when one was gathered)
 *   STATIC  that public address with OUR port, a port-preserving guess - only when the mapped port
 *           differs, because otherwise it is byte-identical to the STUN one and says nothing new
 *   LOCAL   our own address and port (when known)
 *
 * Either address may be NULL for "not known". With nothing known it offers nothing rather than something
 * wrong. Returns the count (0..3), or -1 for a NULL `out`, a capacity under
 * HALYARD_WAN_OUR_CANDIDATES_MAX, or an address too long for a candidate.
 *
 * THE PORTS MUST BE THOSE OF THE SOCKET THAT WILL CARRY THE TRAFFIC. A NAT maps per source port, so the
 * reflexive half has to come from rc_stun_gather on that same socket - and the control association and
 * the A/V leg are two sockets, two mappings, and two separate calls here.
 */
int halyard_wan_our_candidates(const char *local_address, uint16_t local_port, const char *reflexive_address,
                               uint16_t reflexive_port, halyard_wan_candidate *out, size_t out_capacity);

/*
 * PreferredCandidate: which of the console's offered candidates to talk to, and to name back in our
 * ACCEPT. Returns its index, or -1 for an empty list.
 *
 *   1. the first whose address parses and shares a subnet with one of our interfaces - on the same
 *      network the local address wins and traffic stays on the LAN;
 *   2. else the first whose address parses (the reflexive one, off-network);
 *   3. else the first whose address equals `console_host` verbatim (NULL skips this step);
 *   4. else the first candidate at all - an offer we cannot parse is still better answered than ignored.
 *
 * The transport and the ACCEPT must use the SAME rule, or we talk to one address while telling the
 * console we chose another. .NET has two copies of it (CandidateEndpoint for the transport); they agree
 * wherever the chosen address parses, and CandidateEndpoint falls back to console_host:9303 where
 * nothing does. So a caller whose chosen candidate does not parse with halyard_wan_parse_ipv4 should
 * reach the console at console_host on HALYARD_WAN_CONTROL_PORT, which is exactly that fallback.
 */
int halyard_wan_choose_candidate(const halyard_wan_candidate *candidates, size_t candidate_count,
                                 const halyard_wan_interface *interfaces, size_t interface_count,
                                 const char *console_host);

#endif /* HALYARD_WAN_CANDIDATES_H */
