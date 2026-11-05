# WaTenancy

A .NET 10 / ASP.NET Core / Blazor Server application that turns **wacli** into a company-scoped WhatsApp messaging service. Companies manage multiple numbers, their team, API keys and their own webhook endpoint.

## Run locally

Requirements: .NET SDK 10 and wacli **0.18.2 or a compatible version** on PATH. The installed 0.18.2 CLI was inspected during implementation. Windows is supported; wacli and its platform dependencies must also be available on a Linux host.

From this solution directory:

```powershell
dotnet restore WaTenancy.sln
dotnet run --project src/WaTenancy.Web
```

Open **http://127.0.0.1:5187/setup**. Create your platform administrator with your own password. There are **no default credentials**, public registration, or seeded live WhatsApp accounts. Setup accepts local connections only and closes after the first user exists.

1. Create a company.
2. Add a number label under **Numbers**.
3. Click **Connect number**, and scan the QR from WhatsApp → Linked devices → Link a device. Phone-number pairing is also available.
4. Successful pairing starts its persistent watcher automatically.
5. Add company users under **Team**. Add at least one **Owner** for each company.
6. Configure the company's **Webhooks** profile and **API keys**.

Your existing `~/.wacli` sessions are not imported, changed or reused. Every application account has a new isolated store. Adding a label does not automatically authenticate a phone.

## Implemented behavior

- Company isolation enforced in application services, API authentication and composite database relationships.
- ASP.NET Core Identity login, strong passwords, lockout, antiforgery-protected login/logout/setup, cookie revalidation and password changes.
- Platform administrator creates/enables/disables companies and manages global users. Company administrators provision company members. Existing users retain their original password.
- Viewer, Agent, Admin and Owner roles. Owners control owner access; the last owner cannot be removed.
- Separate persistent WhatsApp store and supervised `sync --follow` process for **every connected number**.
- QR and phone pairing with transient codes in memory, bounded pairing time, connection status and automatic restart backoff.
- Pause/resume watching and explicit logout. A remote logout marks the account as needing pairing instead of endlessly reconnecting.
- Tenant-bound, hashed, expiring/revocable API keys. Read, send and account-management permissions.
- Durable outbound jobs with idempotency, text and file attachments, cancellation before dispatch, and delivery/read receipts.
- Searchable, paginated company inbox; read-only mirror reconciliation recovers missed messages and honors local tombstones/purge records.
- Per-company public HTTPS webhook profile, signed deliveries, stable event IDs, retries, delivery history, manual retry, test events and secret rotation.
- Audit trail, API rate limiting, account/worker/upload limits and configurable application retention.
- Generated OpenAPI at `/openapi/v1.json`, a web integration guide and a runnable .NET webhook receiver with a local chat interface.

## Company authorization

| Role | Read inbox / jobs | Send / upload | Manage numbers | Team / API keys / webhook | Manage owners |
|---|---|---|---|---|---|
| Viewer | Yes | No | No | No | No |
| Agent | Yes | Yes | No | No | No |
| Admin | Yes | Yes | Yes | Yes | No |
| Owner | Yes | Yes | Yes | Yes | Yes |

Platform administrators can manage all companies. Application services recheck membership, enabled state and security stamp on every operation, including requests from an already-open Blazor circuit. Global user disable and password resets expire old sessions. Company membership removal does not disable the user in other companies.

API endpoints accept **X-Api-Key**, not browser cookies. The key determines the tenant; there is no caller-selected store path or tenant override. Account-management keys can pair, watch and unlink company numbers, including retrieving private pairing information. Give that permission only to trusted backend applications.

## API

The dashboard's **API & webhooks** guide contains the route list. OpenAPI can be imported into an API client. Use the `X-Api-Key` header on every `/api/v1` request.

```http
GET /api/v1/accounts
X-Api-Key: wat_YOUR_KEY
```

Queue a message:

```http
POST /api/v1/messages
X-Api-Key: wat_YOUR_KEY
Idempotency-Key: invoice-1078-notification
Content-Type: application/json

{
  "accountId": "YOUR_ACCOUNT_UUID",
  "recipient": "+27821234567",
  "text": "Your invoice is ready.",
  "fileId": null
}
```

HTTP 202 returns a job and its status URL. Repeat the **same payload and key** after a request failure to retrieve the same job. Different content with the same key returns 409. Idempotency records are retained with jobs for the configured retention period (30 days by default), not forever.

Read status with `GET /api/v1/jobs/{id}`. List jobs and inbox with `page=1` (50 items). Search inbox using `search` and optionally `accountId`.

Upload files using a raw body, `X-Filename`, `Content-Type` and `Content-Length` at `POST /api/v1/files`. Send the returned file ID in `fileId`, with the message text used as its caption. Callers cannot reference server filesystem paths or another company's files. Unsupported MIME types are treated as generic documents. Downloads are served as attachments.

### Job outcomes

| State | Meaning |
|---|---|
| Queued | Persisted, waiting for dispatch from a connected watched account |
| Dispatching | A worker is attempting the send |
| Sent | wacli returned successful acceptance and a message ID |
| Delivered / Read | Receipt observed; in groups this does not imply every member acknowledged |
| Failed | A failure was confirmed before sending, or the CLI explicitly rejected it |
| Unknown | Timeout, interrupted send or unconfirmed CLI response; **verify before sending again** |
| Cancelled | Cancelled before dispatch |

WaTenancy owns the outgoing queue in `application.db`; the receiver only submits and displays jobs. Messages are accepted while a number is disconnected or paused, remain Queued, and dispatch when that number is connected and watched. Jobs retain order within each number, while different numbers dispatch independently. Queued jobs survive restarts and are excluded from history retention until completed or cancelled.

Confirmed pre-send failures (a locked store, missing authentication, an explicit "it was not sent" result, or failure to launch wacli) retry with exponential backoff starting at five seconds and capped at five minutes, up to `MaxSendAttempts` (default eight). The job exposes its attempts, next attempt time and reason. Unsupported operations and missing uploads fail permanently. `Retry send` in the outbox or receiver requeues a confirmed Failed job; the API is `POST /api/v1/jobs/{id}/retry`.

The dispatcher never automatically repeats Unknown messages. On restart, interrupted Dispatching jobs become Unknown. A timeout after dispatch can mean WhatsApp accepted the message, so verify uncertain delivery before creating another send. This avoids pretending exactly-once delivery is possible across a CLI/WhatsApp timeout.

## Tenant webhooks

Each company administrator configures a URL in **Webhooks**. Generate a signing secret, copy it into your consumer's secret configuration, enable delivery and send a test event. Secrets are shown only when generated/rotated. Credentials do not belong in webhook URLs.

Endpoints must be public HTTPS on port 443. DNS addresses are checked both at configuration and connection time. Private/reserved IPs, redirects, URL credentials and proxy routing are rejected to prevent a tenant-controlled URL reaching internal services. Development receivers must be exposed at a public HTTPS URL; local-only receivers are not valid tenant endpoints.

Every POST has this envelope:

```json
{
  "version": "1",
  "eventId": "a-stable-uuid",
  "eventType": "message.received",
  "occurredAt": "2026-10-09T12:00:00Z",
  "tenantId": "company-uuid",
  "accountId": "whatsapp-account-uuid",
  "data": {
    "messageId": "WHATSAPP_MESSAGE_ID",
    "chatJid": "27821234567@s.whatsapp.net",
    "chatName": "Customer",
    "senderJid": "27821234567@s.whatsapp.net",
    "text": "Hello",
    "fromMe": false,
    "timestamp": "2026-10-09T11:59:59Z",
    "mediaType": null
  }
}
```

`occurredAt` is when the application enqueued the event; message `data.timestamp` is the source message time. IDs are real UUIDs in delivered payloads. `accountId` may be null for a test event.

| Event type | Data |
|---|---|
| `message.received` | Normalized incoming message fields shown above |
| `message.sent` | Synced outgoing message, same normalized fields |
| `message.updated` | Message text changed, same normalized fields |
| `message.deleted` | `messageId`, `chatJid`; consumers should delete retained content |
| `message.receipt` | Original wacli receipt: `EventType`, `Chat`, `Sender`, `MessageIDs`, `Timestamp`, `Type`, `IsFromMe` |
| `chat.presence` | Original wacli typing event: `EventType`, `Chat`, `Sender`, `State`, `Media` |
| `account.status` | `state`, account name/phone, optional error |
| `message.dispatch` | `jobId`, `messageId`, `state`, optional error |
| `webhook.test` | A test message |

Pairing QR codes, phone pairing codes, device keys, media decryption keys and session database contents are **never forwarded**. Incoming audio (including voice notes), images, videos, documents and stickers are downloaded into private tenant storage. Uploaded outbound attachments remain separate application files.

### Incoming voice notes and media processing

Requires wacli **0.20.0 or newer**. Downloads use `--read-only media download --output` so each number can continue `sync --follow` without a store-lock conflict or a second WhatsApp session. A durable download queue survives restarts, retries up to eight times with backoff, and processes up to four companies concurrently, one download per company. One company's failed or slow download does not stop other companies or outgoing sends.

`message.received` includes `data.attachment` with `id`, `state`, `mediaType`, `contentType`, `name`, `length`, `downloadUrl` and `metadataUrl`. The initial state normally is `Pending`. Process the separate **`message.media.ready`** event to retrieve the actual audio; `message.media.failed` reports exhausted retries. Events can arrive out of order: match `accountId`, `data.chatJid`, `data.messageId` and attachment ID. Deduplicate webhook event IDs before processing. Your app owns transcription and business processing.

```json
{
  "eventType": "message.media.ready",
  "tenantId": "COMPANY-UUID",
  "accountId": "NUMBER-UUID",
  "data": {
    "messageId": "WHATSAPP-MESSAGE-ID",
    "chatJid": "27821234567@s.whatsapp.net",
    "attachment": {
      "id": "ATTACHMENT-UUID",
      "state": "Ready",
      "mediaType": "audio",
      "contentType": "audio/ogg",
      "name": "voice-note.ogg",
      "length": 48216,
      "downloadUrl": "/api/v1/media/ATTACHMENT-UUID",
      "metadataUrl": "/api/v1/media/ATTACHMENT-UUID/metadata"
    }
  }
}
```

Download with `GET /api/v1/media/{id}` and the company's **`X-Api-Key`** header. HTTP range requests are supported. The same company's read-only API key can download media; another company's key receives 404. A pending/failed download returns 409, and deleted/expired media returns 404. `GET /api/v1/media/{id}/metadata` returns its current state. `POST /api/v1/media/{id}/retry` requeues a failed download and requires send permission. `/api/v1/inbox` also includes attachment metadata.

Set **`WaTenancy:PublicBaseUrl`** to your external service URL (for example `https://whatsapp.example.com`) to make webhook download URLs absolute. Otherwise resolve the relative paths against your configured WaTenancy service URL. Never send your API key to arbitrary URLs received in a webhook; only download from your configured service origin.

Defaults: `MaxIncomingMediaBytes=104857600` (100 MiB), `MaxTenantIncomingMediaBytes=1073741824` (1 GiB per company), `MaxConcurrentMediaDownloads=4`, `MaxMediaAttempts=8`. Files follow the application's retention period and are revoked when the wacli mirror reports deletion/purge. The migration resets mirror cursors once to discover media in existing retained history. Older files may have expired from WhatsApp's CDN; failed downloads are reported rather than interrupting the application. Phone-assisted expired-media recovery is not performed automatically while a watcher owns the session.

The WebhookReceiver chat displays an audio player for ready voice notes and download links for other attachments. Configure `Receiver:ApiKey` and `Receiver:ServiceBaseUrl`; its server proxies authenticated media requests without exposing the API key to the browser.

### Signature verification

Headers:

```text
X-WaTenancy-Event-Id: <event UUID>
X-WaTenancy-Timestamp: <Unix timestamp for this attempt>
X-WaTenancy-Signature: sha256=<lowercase hex HMAC>
```

Compute HMAC-SHA256 with the UTF-8 signing secret over:

```text
timestamp + "." + exactRawBody
```

Reject timestamps outside a five-minute window, compare in constant time, validate the header event ID matches the payload, and persist/deduplicate `eventId` before acknowledging **HTTP 2xx**. Retries use the same event ID and body but a new timestamp/signature.

Deliveries have a 15-second request timeout. Non-2xx responses and network failures retry up to eight attempts at increasing intervals: 10, 20, 40, 80, 160, 320 and 640 seconds. Failed events remain visible and can be retried manually. A restarted in-flight delivery can be repeated. **Ordering is not guaranteed.** Pending deliveries pause while the company or webhook is disabled.

URL and signing secret are captured at enqueue time. Rotation affects new events; keep the old secret during the retry window. Manual retry of a Failed event uses the current profile URL and secret. Expired events are removed by retention.

The application forwards wacli's message, receipt and typing surfaces plus account/dispatch events. It does **not** promise every WhatsApp protocol event: live webhook forwarding is best-effort, receipts/presence may be lost while offline, and WhatsApp decides which history it provides. A 15-second read-only mirror scan recovers retained messages. Historical bootstrap messages can therefore generate message events; use their source timestamps if your consumer should ignore old conversations. In-place message edits are forwarded when observed live; the insert cursor alone cannot replay every missed edit. Payload purges/deletions are reconciled separately.

### Runnable .NET consumer

See [samples/WebhookReceiver](samples/WebhookReceiver). It verifies timestamps and signatures, persists events in SQLite, deduplicates the event ID and acknowledges only after storage. It deliberately does not send any WhatsApp messages.

```powershell
$env:Receiver__SigningSecret = '<secret copied from your company profile>'
dotnet run --project samples/WebhookReceiver
```

The receiver listens locally on port 5190 by default. Deploy it behind public HTTPS before using its URL in a tenant profile. Customize processing of stored events for your application; do not perform slow business work before acknowledging.

For local testing, the web application's Development profile permits the two explicitly configured receiver URLs: `http://127.0.0.1:5190/whatsapp/events` and `http://localhost:5190/whatsapp/events`. Run both projects in Development, configure the same webhook signing secret in the receiver, save the profile and send a test event. `WaTenancy:DevelopmentWebhookUrls` controls this exact URL allowlist; the exception accepts only loopback addresses and is disabled outside Development. Production continues to require public HTTPS.

## Configuration and storage

`src/WaTenancy.Web/appsettings.json` contains non-secret defaults. Environment variables use `WaTenancy__Setting` names.

| Setting | Default |
|---|---|
| `DataRoot` | `../../data` relative to application content root (solution `data/` during local run) |
| `WacliExecutable` | `wacli` from PATH; use an absolute executable path for a service |
| `SessionLinkRoot` | `%LOCALAPPDATA%/wt-sessions` when a store needs a short path; optionally set a shorter private directory |
| `InternalBaseUrl` | `http://127.0.0.1:5187` — must match the application's HTTP loopback listener |
| `EnableWorkers` | true |
| `MaxAccountsPerTenant` / `MaxTotalWorkers` | 20 / 100 |
| `MaxConcurrentSends` | 4 |
| `MaxSendAttempts` | 8 for sends confirmed not to have been dispatched |
| `MaxConcurrentWebhooks` | 4 tenant deliveries; at most one active delivery per company |
| `MaxMessagesPerAccount` / `MaxDatabaseSize` | 25,000 / 500MB per wacli mirror |
| `SendSpacingMilliseconds` | 1,500 per account |
| `MaxUploadBytes` / `MaxTenantUploadBytes` | 16MB / 256MB |
| `RetentionDays` | 30 for application history/jobs/events/files |
| `RequireHttps` | false for loopback development; enable on a configured HTTPS deployment |

The persistent root holds `application.db`, data-protection keys, `sessions/{tenantUUID}/{accountUUID}` and `uploads/{tenantUUID}`. UUID store/file paths are generated by the server. Secrets are not stored in source or public web assets. API keys are SHA-256 hashes; webhook secrets use ASP.NET Core Data Protection. WhatsApp owns `session.db`; the application never modifies its internals or writes to `wacli.db`.

Application retention does **not** prune wacli's database. The configured wacli caps stop growth; reaching a cap requires reviewing that account's store and settings. Media auto-download is not enabled. Monitor both the application root and its account stores.

wacli 0.18.2 uses a `.send.sock` Unix-domain socket in each store for delegating sends to its watcher. Long session paths can exceed the socket path limit and prevent that endpoint from starting. The application gives long stores stable short aliases (Windows junctions or Unix symlinks); authentication and message files stay in the original data root. Windows creates junctions using its built-in PowerShell. Keep the alias root private and outside `wwwroot`; links can be regenerated after restore and do not replace the primary data backup.

## Deployment

This version intentionally runs as **one application instance per data root**. A filesystem lease prevents a second instance from owning its SQLite database and session stores. Each number runs simultaneously in its own process, with capacity controlled by settings; the configured maximum is a limit, not a benchmark of this computer.

Publish:

```powershell
dotnet publish src/WaTenancy.Web -c Release -o artifacts/publish
```

Set a stable absolute `DataRoot` outside the published directory and outside `wwwroot`, an absolute `WacliExecutable`, and the server's allowed hostnames. Run under a dedicated OS account with exclusive access to its data root. Keep that account/session storage private; WhatsApp keys necessarily remain on the worker host. Persist and protect the data-protection key ring along with your backups.

For external access, configure a trusted HTTPS certificate and an HTTPS listener, preserving the HTTP **loopback** listener for internal callbacks. Example environment settings (certificate password belongs in a service secret store):

```powershell
$env:WaTenancy__DataRoot = 'C:\ProgramData\WaTenancy'
$env:WaTenancy__WacliExecutable = 'C:\Tools\wacli\wacli.exe'
$env:AllowedHosts = 'whatsapp.your-company.com;localhost;127.0.0.1'
$env:ASPNETCORE_URLS = 'http://127.0.0.1:5187;https://0.0.0.0:7187'
$env:WaTenancy__RequireHttps = 'true'
$env:Kestrel__Certificates__Default__Path = 'C:\Certificates\server.pfx'
$env:Kestrel__Certificates__Default__Password = '<from your secret store>'
```

Do not expose the internal HTTP listener or use wildcard public HTTP binding for production. Complete first-run setup locally before routing external traffic. This app does not trust arbitrary forwarded headers; terminating TLS at a reverse proxy requires configuring trusted proxy addresses and forwarded-header handling to match your deployment. Never blindly trust every proxy.

Use Windows Service/systemd/process supervision for the app process itself. wacli child processes are supervised by the application. Graceful application shutdown stops owned child processes. Deployments must keep the executable available on the service account's PATH or use an absolute path. Do not run separate `wacli sync` commands against application-owned account stores.

Windows encrypts newly generated data-protection keys with DPAPI for the running OS account. Keep the service identity stable; changing accounts or restoring onto a different machine requires a deliberate key migration. On Linux, restrict data-root permissions and configure an appropriate key encryption provider for your deployment.

Stop the app before making a consistent backup of **the entire persistent data root** (database, uploads, account stores and protection keys). Restoring only the database can leave secrets/session files unusable. Do not delete the key ring while retaining encrypted data. Review your data retention and disk limits for the number of companies you host.

For horizontal scale, replace the local queue/lease with distributed ownership and shared durable storage, then benchmark per-number resource usage. This implementation does not pretend SQLite plus local subprocesses provides a distributed cluster.

## Validation

```powershell
dotnet test WaTenancy.sln
```

Tests use temporary isolated stores and fake wacli/HTTP transports; they never send WhatsApp messages. They cover tenant isolation, revoked credentials, owner protection, antiforgery, idempotency, signatures, SSRF boundaries, concurrent watchers, restart backoff, receipts, timeout uncertainty, webhook retry and secret capture. UI smoke testing uses `tests/browser/ui_smoke.py` and a separate server on 5188 with workers disabled and `DataRoot=artifacts/browser-data`.

Validated: 84 passing .NET tests, a Release publish with no warnings/errors, desktop/mobile Edge smoke checks, and six HTTP checks against the receiver sample (valid signature, duplicate acknowledgment, invalid signature, expired timestamp and malformed payloads). Failure isolation tests cover connection exceptions, timeouts, unexpected delivery exceptions and a stalled tenant while another tenant receives a new event. The receiver smoke script is `tests/browser/receiver_smoke.py`; run it only against a disposable receiver on 5190 configured with `Receiver__SigningSecret=DisposableReceiverTestSecret` and an isolated `Receiver__DataRoot`.

Webhook attempts run concurrently across companies, with at most one active attempt per company and `MaxConcurrentWebhooks` total attempts. An unavailable endpoint records an error against its delivery and schedules a retry; it does not stop the host, watchers, or other company deliveries. After eight unsuccessful attempts the delivery is marked Failed for manual retry. Visual Studio may pause on a handled network exception depending on Exception Settings; Continue resumes the worker's failure handling.

Real QR pairing, live send/delivery, and real tenant endpoint compatibility still require testing with your own authorized numbers and endpoints. wacli uses a linked-device WhatsApp connection through whatsmeow, rather than Meta's Cloud API. Its current CLI has no outbound interactive menu/list command, and this wrapper does not invent one.

## Layout

- `src/WaTenancy.Web` — Blazor interface, API, identity, EF migrations, workers and services.
- `tests/WaTenancy.Tests` — xUnit security, API, authentication and worker integration tests.
- `tests/browser` — local headless browser smoke checks.
- `samples/WebhookReceiver` — signed-event consumer in .NET.

Protocol references: [wacli accounts](https://wacli.sh/accounts.html), [authentication](https://wacli.sh/auth.html), [sync/webhooks](https://wacli.sh/sync.html), [read-only integrations](https://wacli.sh/integrations.html).

