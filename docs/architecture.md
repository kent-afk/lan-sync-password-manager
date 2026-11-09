# Architecture

## Scope

This note explains how Pm is put together and why: module boundaries, the trust
model, the state on disk, the runtime shape, and the sync rules. The
[README](../README.md) is the canonical product description and module
inventory. This file is not a build or installation guide.

## Problem and context

Pm is a password manager for one person on a local network: a desktop, a phone,
and occasionally the CLI on the same vault. `Vault` is the list of entries;
`store.bin` is that list encrypted on disk; `keys.bin` is the signed device list.
Nothing leaves the LAN. There is no cloud, account, or relay. A few hundred
entries is the expected size.

The real cost of a save is rewriting and re-encrypting the whole vault. That cost
is acceptable at this size. The merge itself is linear in the number of entries.

Goals:

- Keep the vault readable only by paired devices.
- Sync directly between devices while both applications are open.
- Keep the code small enough for one person to audit and maintain.

Non-goals:

- Multi-user, cloud sync, a relay, browser autofill, import or export, TOTP.
- Background sync on iOS.
- A server that can read plaintext.

## Trust model

Assets: vault entries, the vault key, device private keys, pairing codes, and
device metadata.

Adversaries and boundaries:

- A passive LAN eavesdropper sees HTTP metadata, request sizes, device IDs,
  labels, public keys, and pairing attempts. `store.bin` files are encrypted and
  requests are signed, so this does not reveal entries.
- An active LAN attacker can drop, alter, or replay requests. Signatures and the
  five-minute request window reject altered or replayed requests. The attacker
  can still race a pairing attempt; see the six-digit code limit below.
- A revoked device keeps the copy it already downloaded. It cannot read a
  `store.bin` sealed after it is removed once every remaining peer has adopted
  the new key. A peer that has not synced yet can still serve it; see Risks and
  limits.
- A malicious paired device is an equal administrator. It can add or remove any
  device and sign a new keyset. It can read everything that was synced to it.
- A local process under the same OS account can read the vault while it is
  unlocked. A passphrase protects the key at rest, not a running process. When a
  passphrase is set, the UI locks after five minutes without input and on resume
  from suspend; without one, the keychain opens the vault immediately.
- A physical thief has the same access as a local process if the OS account is
  unlocked. The OS keychain and disk encryption are the boundary there.

Invariants:

- `store.bin` is encrypted and authenticated with AES-GCM (authenticated
  encryption) under a vault key.
- `keys.bin` is signed by a device key. A device is trusted only while it is in
  the current keyset.
- A sync request is signed by the sender's device key and is rejected outside a
  five-minute window.
- Revoking a device rotates the vault key and excludes that device from future
  `store.bin` files.
- A removed device never returns to the keyset without explicit re-pairing.

## Shape and dependencies

Pm is a modular monolith. Project dependencies point one way:

| Project | References | Responsibility |
|---|---|---|
| `Pm.Core` | none | Vault model, crypto, file format, merge, `VaultSession` |
| `Pm.Sync` | `Pm.Core` | Sync client, pairing client, UDP discovery, `IAppHost` port |
| `Pm.Net` | `Pm.Core`, `Pm.Sync` | Kestrel HTTP adapter |
| `Pm.Secrets` | `Pm.Core` | GNOME Keyring, Windows DPAPI (Data Protection API), 0600 file store |
| `Pm.App` | `Pm.Core`, `Pm.Sync` | Shared Avalonia UI |
| `Pm.Desktop` | `Pm.App`, `Pm.Cli`, `Pm.Net`, `Pm.Secrets`, `Pm.Sync`, `Pm.Core` | Unified entry point: GUI, CLI, and HTTP node |
| `Pm.App.iOS` | `Pm.App`, `Pm.Core`, `Pm.Sync` | iOS process and iOS Keychain store |
| `Pm.Cli` | `Pm.Core`, `Pm.Net`, `Pm.Secrets`, `Pm.Sync` | CLI commands and self-check; linked into `Pm.Desktop` |

`Pm.Core` is a dependency-free core library, not a pure domain layer. It contains
the domain model and crypto, and also the application service `VaultSession`,
file locking, and rekey recovery. `VaultSession` is intentionally one facade
over several jobs: vault CRUD, keyset reload and resolution, rekey, crash
recovery, peer bookkeeping, and sealing. Splitting it is debt, not a current
requirement.

`Pm.Net` is an HTTP adapter, but `LanServer` also owns the pairing state machine
and part of sync orchestration. That is a known deviation from a strict adapter
boundary; extracting application services is listed under Known debt.

`Pm.Desktop` is the single shipped binary. With no arguments it opens the
Avalonia window; with a command it runs the `Pm.Cli` command layer. The CLI
project remains separate for development and self-check, but no separate CLI
artifact is released.

`MainView` is the screen composition root, split by feature into partial files.
The partials are file organization, not a separation-of-concerns claim.

### Ports

| Port | Defined in | Implementations | Contract |
|---|---|---|---|
| `ISecretStore` | `Pm.Core` | `Pm.Secrets` stores, iOS `KeychainStore` | Get, set, and delete key material by name |
| `IAppHost` | `Pm.Sync` | `DesktopHost`, `IosHost` | Start and stop, discover peers, offer or join pairing, sync |

`IAppHost` lives in `Pm.Sync`, not in the UI project, so a headless or
alternative UI host does not need to reference Avalonia. The CLI still calls
`LanServer` and `SyncClient` directly instead of using the port.

No interface is added for a single implementation. No abstraction exists only to
make a diagram symmetrical.

## Persistent state

| Artifact | Contents | Writer | Consistency rule |
|---|---|---|---|
| `store.bin` | Encrypted vault, `PMV1` magic, nonce, AES-GCM payload | `VaultSession.SaveStoreCore` | Atomic replace; full-file rewrite |
| `keys.bin` | Signed keyset: `Rev`, devices, key wraps, `Removed` | `VaultSession.SaveKeyset` | Atomic replace |
| `keys.bin.prev`, `store.bin.prev` | Previous pair during a rekey | `VaultSession.SaveRekeyed` | Deleted after both new files are written |
| `config.json` | Device ID, label, port, peers | `VaultSession.SaveConfig` | Atomic replace; not secret |
| `ui.json` | Theme, language, zoom | `Pm.App` settings | Per device; not secret |
| `.lock` | Cross-process write lock | `Files.Lock` | Advisory `FileShare.None`; local filesystems only |
| OS keychain entry | Device private key, optionally passphrase-wrapped | `VaultOpener`, `VaultSecurity` | Platform store |

Each device has an RSA key pair. `keys.bin` holds each device's public key and a
copy of the vault key wrapped to that public key. The vault key encrypts
`store.bin`. `Rev` is a monotonically increasing keyset generation: adding,
removing, or merging devices bumps it, and a peer rejects a lower `Rev`.

On Unix the home directory is `0700`; `store.bin`, `keys.bin`, `config.json`,
and the `.prev` files are `0600`. Permissions left by older builds are tightened
when the vault is opened.

`store.bin` and `keys.bin` must describe the same rekey generation. A rekey
writes `keys.bin` first, then `store.bin`. If the process dies between the two,
the `.prev` pair is used to recover the previous keyset and store, and the vault
is resealed under the current key. There is no transaction across the two files;
the recovery path covers the window.

Writes use a temporary file in the same directory, flush it to disk, then rename
over the target. A reader sees the old file or the new file, never a half file.

## Runtime and topology

| Process | UI | HTTP listener | Discovery | Notes |
|---|---|---|---|---|
| `Pm.Desktop` | Avalonia or CLI | `LanServer` on `0.0.0.0:port` | Advertises and asks | Can offer pairing; UI and HTTP share one `VaultSession`; CLI mode skips the UI |
| `pm serve` | None | `LanServer` | Asks | Same home directory as other CLI commands |
| `Pm.App.iOS` | Avalonia | None | Asks only | Syncs while the app is open |

Desktop and CLI processes on the same home directory share `.lock` and the
in-process `Gate`. A write from one process is picked up by the other through
`ReloadKeyset` and `ReloadStore`.

iOS holds a full data replica but is not a full sync node. It never listens. A
sync needs at least one listening peer and the initiating device online at the
same time. There is no relay, queue, or background listener.

`LanServer` keeps no shared state beyond the unlocked session. Replicating it
only helps if `store.bin` itself becomes shared.

## Flows

### Pairing

1. A listening peer opens a pairing ticket: six digits, five minutes, at most
   five failed attempts.
2. The joiner fetches `/info`: device ID, label, public key, and protocol format.
   Both users compare the fingerprints shown on the two screens.
3. The joiner sends `/pair` with an HMAC (keyed hash) proof over its public key. The responder
   checks the code, asks its user to confirm the joiner's fingerprint, then adds
   the device, wraps the vault key for it, signs the keyset, and returns the
   keyset, `store.bin`, and a proof.
4. The joiner verifies the responder proof, adopts the keyset, and merges
   `store.bin`.

The code proof is an HMAC over a six-digit code. A captured pairing request can
be brute-forced offline. The online attempt limit only stops guesses against a
live ticket. A PAKE (password-authenticated key exchange) is the upgrade path if
the LAN is hostile.

### Sync

1. The client fetches `/info` and refuses a different protocol format.
2. The client sends a signed `SyncRequest` with its device ID, timestamp,
   `store.bin`, keyset, and keyset signature.
3. The server checks the format, the five-minute window, device membership, the
   request signature, and the keyset signature.
4. The server reloads its keyset and `store.bin`, resolves the keysets, opens
   the remote `store.bin`, merges entries, saves what changed, and returns its
   keyset and `store.bin`.
5. If the server needs a different key, it returns `409`; the client adopts the
   returned keyset, reseals its store, and retries once.
6. Entries with the same version but different content are a conflict. A
   deterministic winner is kept and the loser is stored as a conflict copy.

### Revocation and rekey

1. `RemoveDevice` removes the device from the keyset, adds its ID to `Removed`,
   generates a new vault key, rewraps it for the remaining devices, bumps `Rev`,
   and signs the keyset.
2. The new keyset is saved first, then `store.bin` is resealed under the new key.
   `.prev` covers a crash between the two writes.
3. Other devices adopt the newer keyset on their next sync. A stale device cannot
   push an older keyset because the server returns `409` when its `Rev` is lower.
4. A stale peer that has not adopted the new keyset can still return data to the
   revoked device. Until every remaining peer syncs, the revoked device may read
   data written to that stale peer. There is no epoch or acknowledgement; this is
   a known residual window.
5. Equal-rev branches are unioned instead of resolved by a hash winner. The union
   keeps every non-removed device, keeps every `Removed` entry, and never
   resurrects a revoked device. If the branches have different vault keys, or if
   a branch still contains a locally removed device, a fresh vault key is
   generated and every remaining device is rewrapped. A stale branch with a
   higher `Rev` is filtered the same way: local `Removed` entries still apply,
   and the key is rotated if that branch still contains a locally removed
   device. Re-pairing clears the ID only in the keyset of the device that
   performs it. A peer that previously revoked the device keeps it out until
   that peer also re-pairs it.

### Merge and tombstones

An entry carries a Lamport version and the ID of the device that wrote it. A
higher version wins. Equal versions with different content produce a conflict
copy, so neither password is lost.

Delete writes a tombstone with `Deleted`, `DeletedAt`, and a new version. The
secret fields are cleared. Tombstones are kept forever so a device that was
offline cannot resurrect a deleted entry. Older builds may have tombstones with
secret fields; those fields are cleared when `store.bin` is opened. There is no
compaction horizon; tombstone growth is accepted at this size.

## Durability and limits

- The in-process `Gate` and the cross-process `.lock` serialize writes. The lock
  is a `FileShare.None` stream and works on local filesystems. It is not a
  reliable lock on network file systems (SMB or NFS).
- `WriteAtomic` writes a same-directory temporary file, flushes it to disk, and
  renames it over the target. A process crash leaves the old file or the new
  file, never a half file. The parent directory is not fsynced, so a power loss
  can lose the rename on some filesystems.
- The HTTP request body is capped at 8 MB. A vault larger than that cannot sync.
  There is no per-entry limit.
- The decrypted vault and vault key live in process memory while unlocked.
- There is no backup or export. A lost or corrupted vault is unrecoverable.

## Versioning and compatibility

`/info` reports `Protocol.Format`. Sync and pairing reject a different format
with `426 Upgrade Required`. The client also checks the format in responses.

`store.bin` starts with the `PMV1` magic. `keys.bin` is signed; the `Removed`
field is omitted when empty so existing signatures remain valid. The `store.bin`
payload has no separate schema version; the protocol format is the version gate.
Any schema change that affects the signed keyset bytes or the store payload
requires a format bump.

Mixed versions are not supported. Update both devices before pairing or syncing.
JSON unknown fields are dropped on save by `System.Text.Json`, so manually
replacing `store.bin` with one from a newer build is unsupported.

## Risks and limits

| Risk | Impact | Mitigation or accepted limit |
|---|---|---|
| No backup or export | Lost or corrupted vault is unrecoverable | Accepted; copy the home directory while the app is closed |
| Revocation propagation window | Revoked device can read data from a stale peer | Accepted; sync all remaining peers after revoking |
| Six-digit pairing code | Captured pairing request can be brute-forced offline | Trusted LAN; PAKE is the upgrade |
| HTTP without TLS | Metadata, sizes, device IDs, and availability are visible; desktop and CLI bind all interfaces | Payloads are encrypted and signed; accepted |
| Key file fallback (0600) | Any process under the same account can read the device key | Keychain is preferred; accepted |
| No passphrase | No prompt and no auto-lock; the keychain opens the vault | Optional passphrase; accepted |
| Lost passphrase | No recovery path | Accepted; documented in the README |
| Local malware while unlocked | Can read the decrypted vault | Passphrase protects the key at rest only; accepted |
| All paired devices are administrators | A compromised device can add or remove devices and sign a new keyset | Re-pair after compromise; keyset changes are not confirmed on other devices |
| iOS never listens | Sync only while both apps are open | Accepted; documented in the README |
| SMB or NFS home directory | `.lock` is not reliable; atomic rename may not be | Local filesystems only |
| Power loss after rename | New name may be lost because the directory is not fsynced | `.prev` helps only during a rekey |
| Tombstones grow forever | Unbounded file growth across many deletions | Accepted at a few hundred entries; compaction needs an all-peers-seen horizon |
| No revocation epoch or acknowledgement | No proof that every peer applied a revocation | Accepted; manual sync is the check |

## Alternatives considered

| Choice | Rejected option | Reason |
|---|---|---|
| Direct peer-to-peer sync | Cloud relay or hub | Adds an account, a server, and a plaintext boundary; the product is LAN-only |
| Modular monolith | Microservices | Adds network calls, auth, deployment, and partial-failure modes without removing load |
| Encrypted and signed payloads over HTTP | TLS (transport encryption) or Noise | The payload is already encrypted and signed; TLS adds certificate handling and does not remove the need for fingerprint confirmation |
| Keychain-first key storage | Passphrase-first on every start | Keychain removes a password prompt; passphrase remains opt-in |
| Full-store sync | Delta sync | The vault is small; delta sync adds protocol state and failure modes |
| One encrypted file | Database | A file keeps backup and atomic replace simple; a database adds migration and locking |
| RSA-4096 | A modern KEM (key-encapsulation mechanism) or signature scheme | The current format works at this size; changing it is a format break, not a bug fix |
| Revocation-aware keyset union | Hash winner between equal-rev keysets | A hash winner can resurrect a removed device or drop a newly paired one |
| Scrubbed tombstones | Keep full entry contents in tombstones | A deleted secret should not stay in every replica and every future `store.bin` |
| Match protocol versions | Best-effort mixed-version sync | `System.Text.Json` drops unknown fields on save, so mixed versions can lose data silently |

## Scaling

One user, a few hundred entries, and a full-file rewrite are not a scaling
problem. The vault is small enough that the simplicity of one file and one
process is worth more than incremental sync or a database.

The first justified service split, if Pm ever grows beyond one user, is a sync
relay that forwards signed ciphertext. The vault, crypto, merge, and keyset stay
client-side. No relay exists today, and adding one must not move those parts to
the server.

## Known debt

- `LanServer` contains the pairing state machine and sync orchestration. Extract
  them into application services if a second transport appears.
- `VaultSession` is a facade over several jobs. Split it when a second consumer
  needs only part of it.
- `MainView` is one composition root across partial files. Split it into
  presenters when the UI grows.
- CLI mode bypasses `IAppHost` and calls `LanServer` and `SyncClient` directly.
- No backup or export, no tombstone compaction, no revocation epoch or
  acknowledgement, and no PAKE for pairing.
- Remote keyset changes are applied without a confirmation on other devices.
