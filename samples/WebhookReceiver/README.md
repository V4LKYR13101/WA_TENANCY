# Signed webhook receiver (.NET 10)

The receiver now includes a WhatsApp-style local chat interface at **http://127.0.0.1:5190/**: contact threads, search, incoming/outgoing message bubbles, separate scrolling areas, a fixed reply composer, mobile navigation and an event payload viewer. It projects signed `message.received`, `message.sent`, `message.updated` and `message.deleted` events into conversations, preserving company and WhatsApp-account boundaries. Existing stored events are projected on the next startup.

Set `Receiver__SigningSecret` from your company's Webhooks profile, then run `dotnet run --project samples/WebhookReceiver` from the solution directory. The listener is `http://127.0.0.1:5190/whatsapp/events`. `Receiver__DataRoot` controls its private SQLite storage.

For Visual Studio, right-click the **WebhookReceiver** project → **Manage User Secrets**, and save:

```json
{
  "Receiver": {
    "SigningSecret": "PASTE_THE_WEBHOOK_SIGNING_SECRET_HERE"
  }
}
```

To enable replies, add these settings inside the same `Receiver` object:

```json
"ApiKey": "PASTE_THIS_COMPANYS_WAT_API_KEY",
"ServiceBaseUrl": "http://127.0.0.1:5187"
```

The signing secret validates incoming webhooks; the separate API key authorizes outgoing replies and incoming media downloads through WaTenancy. Replies use idempotency keys and are shown as queued until the service reports their actual state. The receiver verifies the API key's company before sending or retrieving audio. Without an API key the composer remains visible but read-only. The UI does not send automatic replies.

Incoming voice notes show **Downloading…** until WaTenancy sends `message.media.ready`, then display an audio player. Other ready attachments have download links. The browser uses a local receiver endpoint; the receiver fetches the audio with the configured API key and supports HTTP range requests. The API key is never placed in a browser URL. Ready events may arrive before the original message; the projection links them by company, number, chat and message ID. Message deletion removes playback and download access. Configure `Receiver:ServiceBaseUrl` to the WaTenancy service's reachable URL. This sample provides playback and durable event storage; add your own background transcription/business worker to process ready events.

Outgoing queueing and retries belong to **WaTenancy**, not this receiver. A disconnected number accepts a reply as Queued and sends when connected/watched again. Confirmed pre-send failures retry with backoff on the service. A Failed reply has a **Retry send** button, which requeues the same service job; uncertain timeout results remain Unknown and must be checked before creating another message. Closing this browser does not stop the server's queue.

Run the project with its Development launch profile (the default). User Secrets are loaded in Development and stored outside the repository. Use the signing secret from the company's **Webhooks** page; a `wat_` token from **API keys** is a different credential and cannot verify webhook signatures. Keep the source lookup as `builder.Configuration["Receiver:SigningSecret"]`—the secret is the configuration value, never the key name.

For local testing, run **WaTenancy.Web** in Development too. Its Development settings permit exactly `http://127.0.0.1:5190/whatsapp/events` and `http://localhost:5190/whatsapp/events`. Save either URL in the company webhook profile, enable forwarding, then send a test event. Production still requires public HTTPS. To change the local receiver port/path, update `WaTenancy:DevelopmentWebhookUrls` in the web project's Development settings; only loopback URLs can use this exception.

This sample verifies the raw-body signature and timestamp, deduplicates event IDs and persists the envelope and inbox projection in one transaction before returning HTTP 200. It records events for your business worker to consume; it does not implement your company's business logic. Deploy the webhook endpoint behind public HTTPS before using it in production. The chat interface and `/inbox` endpoints are restricted to direct loopback connections in Development and disabled in Production; add your application's own login/authorization before exposing a client interface remotely.

HTTP 401 on webhook delivery means signature/timestamp validation failed. Copy the current webhook signing secret into User Secrets and restart the receiver. Previously queued events retain the secret captured when queued; in WaTenancy's delivery history, **Retry with current profile** requeues a failed or waiting retry event using the current URL and secret. A delivered event is not resent by this action.

Run `python tests/browser/receiver_chat_smoke.py` after a Release build to validate the chat interface against disposable signed events and a fake API. It verifies account/company separation, reply routing, edits/deletes, deduplication, CSRF protection, search, desktop/mobile layout, thread scrolling, conversation scrolling and composer visibility with a large inbox. It never sends real WhatsApp messages.

Secrets come from environment/service configuration, never from checked-in defaults. Keep the event database private and implement retention appropriate for your application. Add a background consumer for `events WHERE processed_at IS NULL`, with idempotent business processing and an atomic processed marker.
