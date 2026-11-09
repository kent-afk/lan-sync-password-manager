# Architecture

## Scope

This note explains how Pm is put together and why: module boundaries, the trust
model, the state on disk, the runtime shape, and the sync rules. The
[README](../README.md) is the product description and module inventory. This
note is authoritative for the mechanisms it describes; the code is authoritative
for the exact wire fields and cryptographic byte layout.

## Problem and context

Pm is a password manager for one person on a local network: a desktop, a phone,
and occasionally the CLI on the same vault. `Vault` is the list of entries;
`store.bin` is that list encrypted on disk; `keys.bin` is the signed device list.
Nothing leaves the LAN. There is no cloud, account, or relay. The expected vault
is a few hundred entries; the only enforced size limit is the 8 MiB sync request
cap described under Durability and limits.

The main cost of a save is rewriting and re-encrypting the whole vault. That cost
is acceptable at this size. Merge is O(n log n) and is dominated by the full-file
read, decrypt, encrypt, and write.

### Goals

- Keep the vault readable only by paired devices.
- Sync directly between devices while the applications on those devices are open
  and unlocked.
- Keep the first-party code small enough for one maintainer to audit.

### Non-goals

- Multi-user support, cloud sync, a relay, browser autofill, import/export, and
  TOTP (time-based one-time passwords).
- Background sync on iOS.
- A remote or relay server that can read plaintext. The local HTTP listener runs
  inside an unlocked client and necessarily decrypts the store to merge it.

## Trust model

### Core terms

- **Pm home** is the data directory: `Paths.Home`, by default
  `ApplicationData/Pm`, overridden by `PM_HOME`. It is not the OS user's home
  directory.
- **Vault key** is a random 32-byte key. It encrypts and authenticates
  `store.bin`.
- **Keyset** is the signed device list in `keys.bin`: `Rev`, `Devices` (`Id`,
  `Label`, `PubKey`, `Wrap`), `Sig`, and the monotonic `Removed` list.
- **Key wrap** is the vault key encrypted to a device's RSA public key
  (RSA-OAEP-SHA256), so only that device's private key can unwrap it.
- **`Rev`** is the keyset generation, a monotonically increasing integer.
- **Sealed** means encrypted and authenticated with AES-256-GCM.
- A **paired device** is a device in the current keyset.

### Adversaries and boundaries

- A passive LAN eavesdropper sees HTTP metadata, request sizes, device IDs,
  labels, public keys, and pairing attempts. `store.bin` files are encrypted and
  requests are signed, so this does not reveal entries. There is no forward
  secrecy: recorded traffic can be decrypted if a device key is later
  compromised, because the vault key is static until a revocation rekey.
- An active LAN attacker can drop, alter, or replay requests. Signatures reject
  altered requests. The five-minute timestamp window bounds replay; it does not
  reject a captured request replayed inside the window. Devices must keep their
  clocks within five minutes. The attacker can still race a pairing attempt; see
  the six-digit code limit below.
- A revoked device keeps the copy it already downloaded. It cannot read a
  `store.bin` sealed after its removal. On a peer that has not adopted the
  revocation it is still in that peer's keyset, so it can read, write entries
  under a forged `Device` value, and sign a higher-`Rev` keyset that removes
  another device. There is no revocation epoch or acknowledgement; see Risks and
  limits.
- A malicious paired device has full administrator privileges. It can add or
  remove third devices and sign a new keyset. It cannot remove the receiver
  through sync, because the receiver rejects a keyset that omits its own device.
  It can read everything that was synced to it. Keyset changes are not confirmed
  on other devices.
- A local process under the same OS account can read the vault while it is
  unlocked. A passphrase protects the key at rest, not a running process. The
  passphrase minimum is 12 characters; a shorter one would not resist an offline
  attack on a readable keychain blob. When a passphrase is set, the UI locks
  after five minutes without input and on resume from suspend; locking stops the
  HTTP listener and disposes the session. Without a passphrase, the stored device
  key opens the vault immediately.
- A physical thief has the same access as a local process if the OS account is
  unlocked. The OS keychain and disk encryption are the boundary there.

### Invariants

- `store.bin` is encrypted and authenticated with AES-256-GCM under a vault key.
- `keys.bin` is signed by a device in the keyset. At-rest integrity relies on
  file permissions; any device listed in the keyset can sign a new one.
- A sync request is signed by the sender's device key and is rejected outside a
  five-minute window. Replay inside the window is accepted and is idempotent.
- Revoking a device rotates the vault key, removes the device from the keyset,
  and excludes that device from future stores.
- A removed device returns only through explicit pairing. Because `Removed` is
  monotonic across branches, a single peer's re-pairing can be undone by a peer
  that still has the device in `Removed`; see Revocation and rekey.
- A tombstone wins over a live entry. A deleted secret is not reintroduced by a
  stale edit or by a conflict copy.
- No remote or relay server reads plaintext.

## Shape and dependencies

Pm is a modular monolith: one codebase split into modules. Project references
point one way:

| Project | References | Responsibility |
|---|---|---|
| `Pm.Core` | none | Vault model, crypto, file format, merge, `VaultSession` |
| `Pm.Sync` | `Pm.Core` | Sync client, pairing client, UDP discovery, `IAppHost` port |
| `Pm.Net` | `Pm.Core`, `Pm.Sync` | Kestrel HTTP adapter |
| `Pm.Secrets` | `Pm.Core` | GNOME Keyring, Windows DPAPI (Data Protection API), 0600 file store |
| `Pm.App` | `Pm.Core`, `Pm.Sync` | Shared Avalonia UI |
| `Pm.Desktop` | `Pm.App`, `Pm.Cli`, `Pm.Net`, `Pm.Secrets`, `Pm.Sync`, `Pm.Core` | Unified entry point: GUI, CLI, and HTTP listener |
| `Pm.App.iOS` | `Pm.App`, `Pm.Core`, `Pm.Sync` | iOS process and iOS Keychain store |
| `Pm.Cli` | `Pm.Core`, `Pm.Net`, `Pm.Secrets`, `Pm.Sync` | CLI commands and self-check; linked into `Pm.Desktop` |

`Pm.Core` has no project references and no third-party packages, and is not a
pure domain layer. It contains the domain model, cryptography, the application
service `VaultSession`, file locking, and rekey recovery. `VaultSession` is
intentionally one facade over several responsibilities: vault CRUD, keyset
reload and reconciliation, rekey, crash recovery, peer bookkeeping, and sealing.
Splitting it is known debt, not a current requirement.

`Pm.Net` is an HTTP adapter, but `LanServer` also owns pairing orchestration and
part of sync orchestration. The `PairingTicket` state object lives in `Pm.Sync`.
That is a known deviation from a strict adapter boundary; extracting application
services is listed under Known debt.

`Pm.Desktop` is the single shipped desktop binary. With no arguments it opens the
Avalonia window; with a command it runs the `Pm.Cli` command layer. The iOS app
is shipped separately as an `.ipa`. The CLI project remains separate for
development and self-check, but no separate CLI artifact is released.

`MainView` is the screen composition root, split by feature into partial files.
The partial files are only file organization; they do not separate concerns.

### Ports

| Port | Defined in | Implementations | Contract |
|---|---|---|---|
| `ISecretStore` | `Pm.Core` | `LibsecretStore`, `DpapiStore`, `FileStore`; iOS `KeychainStore` | `Get`, `Set`, `Delete`; `Name`; `Secure` |
| `IAppHost` | `Pm.Sync` | `DesktopHost`, `IosHost` | Start and stop, discover peers, offer or join pairing, sync; `CanOfferPairing` |

`IAppHost` lives in `Pm.Sync`, not in the UI project, so a headless or
alternative UI host does not need to reference Avalonia. The CLI calls
`LanServer` and `SyncClient` directly instead of using the port.

No interface is added for a single implementation.

## Persistent state

All paths below are relative to the Pm home unless stated otherwise.

| Artifact | Contents | Writer | Consistency rule |
|---|---|---|---|
| `store.bin` | Encrypted vault: `PMV1` magic, 12-byte nonce, AES-256-GCM payload with a 16-byte tag | `VaultSession.SaveStoreCore` | Atomic replace; full-file rewrite |
| `keys.bin` | Signed keyset: `Rev`, `Devices` (`Id`, `Label`, `PubKey`, `Wrap`), `Sig`, `Removed` | `VaultSession.SaveKeyset` | Atomic replace |
| `keys.bin.prev`, `store.bin.prev` | Previous pair during a rekey | `VaultSession.SaveRekeyed` | Atomic copies; deleted after both new files are written |
| `config.json` | Device ID, label, port, `TourShown`, peers (`Id`, `Label`, `Address`, `Confirmed`) | `VaultSession.SaveConfig` | Atomic replace; 0600; content is not secret |
| `ui.json` | Theme, language, `PassphraseAsked` | `UiSettings.Save` | Per device; atomic replace; content is not secret |
| `.lock` | Cross-process write lock | `Files.Lock` | Advisory `FileShare.None`; 3-second wait; local filesystems only |
| `.create` | First-run creation lock | `VaultOpener` | Left on disk after creation |
| `device.key`, `device.key.migrated` | Legacy passphrase-encrypted device key and its migrated remainder | `VaultSession.Init`, `VaultOpener` | 0600; migrated to the platform store on open |
| Platform secret store | Device private key, optionally passphrase-wrapped | `VaultOpener`, `VaultSecurity` | Platform store; `FileStore` fallback writes 0600 under `ApplicationData/Pm/secrets` |

The device key is normally in the platform secret store. `SecretStores.Create`
selects `LibsecretStore` when its probe succeeds on Linux, `DpapiStore` on
Windows, and `FileStore` otherwise. The file fallback is outside `PM_HOME` when
`PM_HOME` points elsewhere. If the keychain is unavailable, a CLI or `pm serve`
process may select `FileStore`, find no key, and fail with "хранилище есть, но
ключ устройства не найден"; `pm keychain-status` reports on the store that
process selected, not on every possible store.

Each device has an RSA-4096 key pair. `keys.bin` holds each device's public key
and a copy of the vault key wrapped to that public key. The vault key encrypts
`store.bin`. `Rev` is a monotonically increasing keyset generation: adding a
device, removing a device, or merging keysets increments it. `store.bin` has no
`Rev` field; the keyset and store are consistent when the store opens under the
vault key wrapped in the current keyset. A mismatch shows up as an AES-GCM
failure and is handled by `.prev` recovery.

On Unix the Pm home is `0700`; `store.bin`, `keys.bin`, `config.json`, the
`.prev` files, and the legacy key files are `0600` (owner read/write only).
Permissions left by older builds are tightened when the vault is opened.

A rekey writes `keys.bin` first, then `store.bin`. If the process dies between
the two, the `.prev` pair is used to recover: the previous keyset must be signed
by a device in the current keyset, by the local device, or by a locally removed
device; the local device unwraps the previous vault key, opens the previous store, reseals it under
the current key, and keeps the current keyset. The rekey is completed, not rolled
back. `.prev` files are written atomically and flushed before the new keyset is
made durable.

Writes use a temporary file in the same directory, flush it to disk, then rename
over the target. A reader sees the old file or the new file, never a partially
written file. The parent directory is not fsynced, so a power loss can lose the
rename on some filesystems.

## Runtime and topology

| Mode | UI | HTTP listener | Discovery | Notes |
|---|---|---|---|---|
| `Pm.Desktop` (GUI) | Avalonia | `LanServer` on `0.0.0.0`, port from `Config.Port` (`0` picks a free port) | Advertises and discovers | One listener per home/port; if the configured port is busy, the GUI falls back to a free port and saves it |
| `Pm.Desktop <command>` (one-shot CLI) | None | None | None | Reloads the vault at the start of the operation; no background process |
| `pm serve` | None | `LanServer` | None | Same home as other CLI commands; fails if the configured port is busy |
| `pm pair-wait` | None | `LanServer` | None | Waits for one pairing; no discovery |
| `Pm.App.iOS` | Avalonia | None | Discovers only (UDP) | Syncs while the app is open; can only initiate |

There is one HTTP listener per home and port. Desktop and CLI processes on the
same home share `.lock`; each process has its own in-process `Gate`. Writes take
the `Gate` and then `.lock`. `SaveConfig` can run outside `WithWriteLock`, so
concurrent config writes are atomic but last-writer-wins. A write from one
process becomes visible to the next mutating operation in the other process
through `ReloadKeyset` and `ReloadStore`; there is no file watcher, and an open
UI with no peers may not refresh until the next operation or sync.

iOS holds a full data replica but never runs `LanServer`, so it cannot receive a
sync request. A sync requires the receiving device to be listening and both
devices to be online; iOS can only initiate. There is no relay, queue, or
background listener.

`LanServer` keeps no durable state beyond the unlocked session. The pairing
ticket is per-process ephemeral state. `store.bin` is already shared by
processes on the same home, so a second listener adds no availability; one-shot
CLI commands are the supported concurrent case.

## Flows

### Pairing

1. A listening peer opens a pairing ticket: six digits, five minutes, at most
   five failed attempts.
2. The joiner fetches `/info`: `DeviceId`, `Label`, `PubKey`, and `Format`. The
   joiner's screen shows the responder fingerprint; the user compares it with
   the responder's screen. The responder does not have the joiner's key yet.
3. The joiner sends `/pair` with an HMAC (hash-based message authentication code)
   proof keyed by the six-digit code over its public key. The responder checks
   the code, then asks its user to confirm the joiner's fingerprint. If the ID
   was previously removed, the prompt says so. On approval the responder:
   - rejects its own device ID or an existing ID with a different public key;
   - clears `Removed` for this explicit pairing;
   - adds the device, wraps the vault key for it, signs the keyset, and returns
     the keyset, `store.bin`, and an HMAC proof keyed by the same code over both
     public keys, the keyset, and `store.bin`.
4. The joiner verifies the responder proof, adopts the keyset, and merges
   `store.bin`.

The code is the HMAC key, not the message. A captured pairing request can be
brute-forced offline because the code space is small. The online attempt limit
only stops guesses against a live ticket. A PAKE (password-authenticated key
exchange) is the upgrade path if the LAN is untrusted. `PM_CONFIRM=1` bypasses
the CLI fingerprint confirmation for scripted use.

### Sync

1. The client fetches `/info` and refuses a different protocol format.
2. The client sends a signed `SyncRequest` with `Format`, `DeviceId`, `SentAt`,
   `Body` (the sealed store), `Signature`, `Keyset`, and `KeysetSignature`.
3. The server checks the format, the five-minute timestamp window, device
   membership, the request signature, and the keyset signature. It has no
   replay cache, so a captured request inside the window is processed again;
   merge is idempotent.
4. The server reloads its keyset and `store.bin`, resolves the keysets, opens
   the remote `store.bin`, merges entries, saves what changed, and returns its
   keyset and `store.bin`.
5. A `409` means the client must adopt the returned keyset, reseal its store, and
   retry once. It covers a stale keyset, an unknown keyset signature, a sender
   absent from the keyset, a key change, and a store that does not open under the
   resolved key.
6. Entries with the same version but different live content are a conflict. The
   lower device ID wins; the loser is stored as a conflict copy. A tombstone
   wins over a live entry, so a delete-vs-edit conflict does not resurrect the
   secret. See Merge and tombstones.

### Revocation and rekey

1. `RemoveDevice` removes the device from the keyset, adds its ID to `Removed`,
   generates a new vault key, rewraps it for the remaining devices, increments
   `Rev`, and signs the keyset. It refuses to revoke the local device.
2. The new keyset is saved first, then `store.bin` is resealed under the new key.
   `.prev` files are written atomically and cover a crash between the two writes.
3. Sync and `AdoptKeyset` reject a lower `Rev` with `409`. A cross-process reload
   ignores a lower `Rev`. Pairing merges and may rotate the key. A higher-`Rev`
   branch that still contains a locally removed device is not adopted verbatim:
   the receiver rotates the vault key and filters the device, and the sender
   adopts the receiver's resolved keyset.
4. A revoked device can still reach a peer that has not adopted the revocation.
   That peer can return data to the revoked device and accept its writes; the
   revoked device can also sign a higher-`Rev` keyset that removes a third
   device. Until every remaining peer syncs, the revocation is not complete.
   Revoke on every peer before syncing, or accept the window. There is no epoch
   or acknowledgement.
5. `Removed` is monotonic across branches. Equal-`Rev` branches are merged, not
   resolved by a hash winner: the union keeps every non-removed device, keeps
   every `Removed` entry, and never resurrects a revoked device. If the branches
   have different vault keys, or if a branch still contains a locally removed
   device, a fresh vault key is generated and every remaining device is
   rewrapped. A re-paired device is kept out by any peer that still has it in
   `Removed`; re-pair every peer while isolated, or pair the device as a new
   device with a new ID.

A **branch** is a keyset produced by a divergent keyset history.

### Merge and tombstones

An entry carries a Lamport counter (`MaxVersion() + 1` when written) and the ID
of the device that wrote it.

- For two live entries, a higher version wins.
- Equal versions with different live content produce a conflict. The lower
  device ID wins. The loser becomes a conflict copy with a derived ID (the first
  24 hex characters of `SHA-256(loser.Id:loser.Version:loser.Device)`), version
  `max(current.Version, incoming.Version) + 1`, `Device = "conflict"`, and a
  title suffix `" (конфликт)"`. Equal versions with the same content do not
  conflict.
- A tombstone wins over a live entry regardless of version. Two tombstones are
  reconciled by higher version, then by lower device ID. No conflict copy of a
  live edit is created, because it would reintroduce the deleted secret.
- Delete writes a tombstone with `Deleted`, `DeletedAt` (Unix milliseconds), and
  a new version. `Login`, `Password`, `Url`, and `Notes` are cleared; `Title` is
  retained. Tombstones from older builds are scrubbed in memory when `store.bin`
  is opened; the scrub is persisted on the next save.
- Tombstones are kept forever so a device that was offline cannot resurrect a
  deleted entry by syncing a stale copy. There is no compaction horizon;
  tombstone growth is accepted at this size.

## Durability and limits

- The in-process `Gate` and the cross-process `.lock` serialize writes. `.lock`
  is a `FileShare.None` stream, waits three seconds, then throws. It works on
  local filesystems, not SMB or NFS.
- `WriteAtomic` writes a same-directory temporary file, flushes it to disk, and
  renames it over the target. A process crash leaves the old file or the new
  file, never a partially written file. The parent directory is not fsynced, so
  a power loss can lose the rename on some filesystems.
- The HTTP request body is capped at 8 MiB (8,388,608 bytes), including the JSON
  envelope, keyset, signature, and base64-encoded store. The effective raw-store
  ceiling is about 6 MiB. There is no per-entry limit.
- The decrypted vault and vault key reside in process memory while unlocked.
  `Dispose` does not zero the key or strings; they remain until GC.
- There is no built-in backup or export. A second paired device is a full
  replica. A manual copy of the Pm home works only for the same OS user, machine,
  and path, and only if the device key is also available: the keychain entry is
  named from the home path, DPAPI is bound to the Windows user, and the
  `FileStore` fallback lives outside `PM_HOME`. `pm recover` removes a passphrase
  only when the old plain key remains; it cannot recover a forgotten passphrase.
- The passphrase is 12–30 characters and is stretched with PBKDF2-HMAC-SHA256 at
  600,000 iterations. It protects the key at rest, not a running process.

## Versioning and compatibility

`/info` reports `Protocol.Format` (currently `1`). Sync and pairing reject a
different format with `426 Upgrade Required`. The client checks the format in
`/info` and in successful sync responses. The `409` path checks the keyset, not
the format; the preflight makes a format mismatch there mostly theoretical.

`store.bin` starts with the `PMV1` magic. `keys.bin` is signed over canonical
bytes: `Rev`, `Devices` sorted by `Id` with ordinal comparison, base64 `PubKey`
and `Wrap`, then `Removed` distinct and sorted and omitted when empty; `Sig` is
excluded. `Removed` is omitted only from the signed bytes; the file can still
serialize an empty `Removed: []` array. Any schema change that affects the signed
keyset bytes or the store payload requires a format increment.

Mixed versions are not supported. Update every device before pairing or syncing.
JSON unknown fields are dropped on save by `System.Text.Json`, so copying
`store.bin` or `keys.bin` between builds is unsupported.

### Protocol and formats

The complete wire contract is in `src/Pm.Sync/Protocol.cs`, `SyncClient.cs`, and
`src/Pm.Net/LanServer.cs`; the canonical byte layout is in
`src/Pm.Core/Vault/Models.cs` and `src/Pm.Core/Storage/VaultFile.cs`. The
contract summary:

- `GET /info` → `{ Format, DeviceId, Label, PubKey }`.
- `POST /pair` → `{ Format?, DeviceId, Label, PubKey, Proof, Port }`; response
  `{ Format, Ok, Error, Keyset, Store, Proof }`.
- `POST /sync` → `{ Format?, DeviceId, SentAt, Body, Signature, Keyset?,
  KeysetSignature }`; response `{ Format, Keyset, Store }`.
- Status codes: `400` malformed body or clock skew (`X-Pm-Error: clock-skew`),
  `403` unknown device or bad signature, `409` stale keyset or key change, `426`
  format mismatch (`X-Pm-Error: version-mismatch`).
- Request signature: RSA-PSS-SHA256 over
  `UTF8(deviceId + "\n" + sentAt + "\n") || SHA256(body)`.
- Pairing request proof: `HMAC-SHA256(code, joinerPubKey)`. Pairing response
  proof: `HMAC-SHA256(code, SHA256(responderPubKey || joinerPubKey ||
  keyset.BodyBytes() || store))`.
- `store.bin`: `PMV1` + 12-byte nonce + AES-256-GCM ciphertext with a 16-byte
  tag. Vault key: 32 bytes. Device keys: RSA-4096. Passphrase KDF:
  PBKDF2-HMAC-SHA256, 600,000 iterations, 16-byte salt.

## Risks and limits

| Risk | Impact | Mitigation or accepted limit |
|---|---|---|
| No backup or export | A lost or corrupted vault is unrecoverable | Accepted; use a second paired device, or copy the Pm home and keychain entry for the same user/machine/path |
| Revocation propagation window | A revoked device can read, write, and sign a keyset on a stale peer | Accepted; revoke on every peer before syncing, or add an epoch/confirmation |
| Six-digit pairing code | A captured pairing request can be brute-forced offline | Trusted LAN; PAKE is the upgrade path |
| HTTP without TLS | Metadata, sizes, device IDs, and availability are visible; the pairing HMAC is exposed; desktop and CLI bind all interfaces | Payloads are encrypted and signed; accepted for a trusted LAN |
| Key file fallback (0600) | Any process under the same account can read the device key | Platform store is preferred; accepted |
| Keychain unavailable | CLI may select `FileStore`, find no key, and report "key not found" | Documented failure mode; reads do not try every store |
| No passphrase | No prompt and no auto-lock; the stored key opens the vault | Optional passphrase; accepted |
| Lost passphrase | No recovery for a forgotten passphrase | `pm recover` only removes a passphrase when the old plain key remains |
| Local malware while unlocked | Can read the decrypted vault | Passphrase protects the key at rest only; accepted |
| All paired devices are administrators | A compromised device can add or remove devices and sign a new keyset | Re-pair after compromise; keyset changes are not confirmed on other devices |
| iOS has no HTTP listener | Sync only while both apps are open | Accepted; documented in the README |
| SMB or NFS home directory | `.lock` is not reliable; atomic rename may not be atomic | Local filesystems only |
| Power loss after rename | The rename may be lost because the directory is not flushed | `.prev` helps only during a rekey |
| Tombstones grow forever | Unbounded file growth across many deletions | Accepted at a few hundred entries; compaction needs an all-peers-seen horizon |
| No revocation epoch or acknowledgement | No proof that every peer applied a revocation | Accepted; sync every peer after revoking and do not rely on a per-device confirmation |
| Replay inside the window | A captured request is processed again | Accepted; sync is idempotent; add a nonce cache if replay becomes a threat |
| No forward secrecy | Recorded traffic decrypts after a later device-key compromise | Accepted; periodic rekey or ephemeral exchange would be a protocol change |
| Clock skew beyond five minutes | Sync is rejected with `clock-skew` | Keep device clocks synchronized (NTP) |
| Locked passphrase-protected desktop | The listener is stopped and the vault is disposed while locked | Unlock before syncing; accepted |
| Vault above the 8 MiB request cap | The store cannot sync | No per-entry limit; split the vault or raise the cap deliberately |

## Alternatives considered

| Choice | Rejected option | Reason |
|---|---|---|
| Direct peer-to-peer sync | Cloud relay or hub | Adds an account, a server, and a plaintext boundary; the product is LAN-only. A ciphertext relay would still expose metadata and availability. |
| Modular monolith | Microservices | Adds network calls, auth, deployment, and partial-failure modes without reducing the work. |
| Encrypted and signed payloads over HTTP | TLS (Transport Layer Security) or the Noise Protocol Framework | The payload is already encrypted and signed. TLS would hide metadata and the pairing HMAC and would add certificate handling; it does not remove fingerprint confirmation. The current choice accepts a trusted LAN. |
| Keychain-first key storage | Passphrase-first on every start | Keychain removes a password prompt; passphrase remains opt-in. |
| Full-store sync | Delta sync | The vault is small; delta sync adds protocol state and failure modes. |
| One encrypted file | Database | A single file can be copied while the app is closed and replaced atomically; a database adds migration and locking. |
| RSA-4096 | A KEM (key-encapsulation mechanism) or a different signature scheme | The current format works at this size; changing it is a format break, not a bug fix. |
| Revocation-aware keyset union | Hash winner between equal-`Rev` keysets | A hash winner can resurrect a removed device or exclude a newly paired one. The union keeps a re-paired device out until every peer re-pairs it. |
| Tombstones with cleared secret fields | Keep full entry contents in tombstones | A deleted secret should not stay in every replica and every future `store.bin`. Tombstones also win over live edits. |
| Require matching protocol versions | Best-effort mixed-version sync | `System.Text.Json` drops unknown fields on save, so mixed versions can lose data silently. |
| Custom UDP discovery | mDNS or SSDP | No dependency, LAN-only, and a manual address fallback. The protocol is unauthenticated and best-effort. |

## Scaling

One user, a vault below the 8 MiB request cap, and a full-file rewrite are not a
scaling problem. The vault is small enough that the simplicity of one file and
one process is worth more than incremental sync or a database.

The first justified service split, if Pm ever grows beyond one user, is a sync
relay that forwards signed ciphertext. The vault, crypto, merge, and keyset stay
client-side. No relay exists today, and adding one must not move those parts to
the server.

## Known debt

- `LanServer` contains pairing orchestration and part of sync orchestration.
  Extract them into application services if a second transport appears.
- `VaultSession` is a facade over several responsibilities. Split it when a
  second consumer needs only part of it.
- `MainView` is one composition root across partial files. Split it into smaller
  controls when the UI grows.
- CLI mode bypasses `IAppHost` and calls `LanServer` and `SyncClient` directly.
- The wire protocol and byte formats are documented in code, not in a separate
  protocol note.
- No keyset-change confirmation on other devices, no revocation epoch or
  acknowledgement, and no PAKE for pairing.
