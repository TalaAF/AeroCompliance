# Authentication session policy (2026-11-01)

## Current transport and risks

The browser receives an access JWT and an opaque refresh token in JSON. It stores both in
`sessionStorage` for that tab and sends the access token as a Bearer header. The server
stores only the refresh token's SHA-256 hash. Access JWTs are stateless and carry role and
municipality claims. A script running in the page's origin can read both browser tokens;
HttpOnly cookie protection is not present. Closing a tab clears its `sessionStorage`, but
does not revoke its server-side refresh token. If logout cannot reach the API, the browser
still clears local tokens and the server-side token remains valid until expiry.

Before this change, refresh revoked only the presented token, issued an unrelated
successor row, and returned 401 on old-token reuse without revoking that successor.
Logout revoked only the presented row. The frontend also cleared valid tokens after a
temporary refresh failure or after an unrelated failure in the retried API request.

## Refresh-token family policy

Every new login gets a random family ID; rotations retain it. Refresh and logout
operations for a family serialize on a PostgreSQL transaction advisory lock. A rotated
token is always rejected with 401. During the first 10 seconds after rotation, a
concurrent or retried request gets 401 without revoking the successful request's child.
After that window, the first reuse of an unexpired rotated token marks the replay and
revokes every still-active token in that family. Other logins for the same user, and
other users' sessions, remain valid. Expired tokens and tokens revoked by logout or
administration return 401 without being treated as rotation reuse. Logout with any
known token in a family revokes the whole family and remains idempotent with 204. The
grace calculation uses PostgreSQL time after the family lock, across API instances.

The grace window is a deliberate availability tradeoff: a stolen old token replayed
inside it is rejected but cannot be distinguished from an in-flight duplicate, so it
does not trigger family revocation. A stolen *current* token can still be used before
the legitimate client rotates it. A family revocation stops future refreshes, but
previously issued access JWTs remain valid until their configured expiry (30 minutes
in the checked-in configuration, plus validation clock skew). Role, tenant, and active
user changes can likewise leave an already-issued JWT valid until expiry. Immediate
access-token invalidation would require a separate server-side validation/versioning
design and performance decision.

The frontend now clears tokens on a rejected refresh (400/401). A temporary network or
server failure leaves the session in storage so it can be retried. A 403/other failure
on the retried API request no longer signs the user out after a successful rotation.

## Migration and rollout

Apply `20261101130202_RefreshTokenFamilies` before running the updated API. It adds
`family_id`, `rotated_at`, and `reuse_detected_at`; it backfills each existing token's
family ID from that row's own ID and preserves expiry/revocation state. Historical
parent-child links were never stored, so a token rotated before this migration cannot
be reliably linked to its old parent. New rotations from surviving tokens are linked.

Coordinate API startup with the migration: the previous API does not populate the new
non-null `family_id` column and cannot issue tokens after the migration. Stop old API
writers, apply the migration, then start updated API instances. This change has not
been applied to the running installation. No frontend storage migration or forced
logout is required. Keep a database backup for rollback; reversing the migration
would discard family and replay metadata.

## HttpOnly cookie decision

A complete browser transport change needs a deployment and compatibility decision:

1. Keep JSON Bearer tokens for non-browser clients and add distinct browser login,
   refresh, and logout endpoints. Put the refresh token in a `Secure`, `HttpOnly`,
   appropriately `SameSite` cookie; keep the access token in browser memory. Require
   an origin check and a session-bound CSRF token/header on cookie-backed refresh and
   logout. Decide how existing `sessionStorage` sessions transition or require login.
2. Use cookies for all API authorization. This requires CSRF protection on every
   state-changing endpoint, plus an explicit CORS, origin, and client compatibility
   policy.

The checked-in nginx configurations proxy `/api` on the same origin, while local
Compose and single-container entrypoints also support plain HTTP. Secure-cookie
behavior and TLS termination must be specified for those deployments. Both options
change the public auth contract and need a client rollout plan. No cookie, CSRF, or
partial dual-transport behavior is introduced in this group.
