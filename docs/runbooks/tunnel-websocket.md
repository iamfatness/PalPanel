# Runbook: Blazor Server circuit drops through the Cloudflare Tunnel

**Symptom:** `panel.iamfatness.us` loads, but buttons/live updates don't work,
or the "Rejoining the server…" modal appears and never recovers. Locally
(`http://localhost:5080`) everything works.

**Root cause (this stack):** the UI runs over a SignalR WebSocket circuit. When
`cloudflared` proxies to the origin over **HTTP/2**, the WebSocket `Upgrade` is
not negotiated the way Blazor needs and the circuit fails to establish (or drops
under load). The app itself is fine.

## Diagnose

1. Open `panel.iamfatness.us` with DevTools → Network → filter **WS**. Reload.
   - No `_blazor?id=…` WS row, or it shows `101` then closes → upgrade/transport problem.
   - A `negotiate` POST that 4xx/5xx → negotiation problem.
2. Tail the connector: `cloudflared tunnel --loglevel debug run palpanel`
   (or check the service logs). Look for HTTP/2 → origin and WS upgrade errors.

## Fix

Pin the tunnel ingress for this hostname to **HTTP/1.1** origin so the WS upgrade
works. In the tunnel config (`config.yml` or the dashboard ingress rule):

```yaml
ingress:
  - hostname: panel.iamfatness.us
    service: http://localhost:5080
    originRequest:
      http2Origin: false          # force HTTP/1.1 to origin — required for the WS upgrade
      connectTimeout: 30s
      # keepalive so an idle circuit isn't reaped mid-session
      tcpKeepAlive: 30s
  - service: http_status:404
```

Restart `cloudflared`. SignalR still auto-negotiates, so if WebSockets are ever
blocked it falls back to long polling instead of dying.

## App-side hardening (already in code)

- `Program.cs`: `DisconnectedCircuitRetentionPeriod = 3 min` — a reconnecting
  client resumes its exact circuit (state intact).
- `App.razor`: client reconnect extended to 30 retries × 3 s so a blip doesn't
  exhaust the retry budget. The built-in reconnect modal (`ReconnectModal.razor`)
  shows Retry + reload if it ultimately fails.

## Acceptance test

1. Load the panel through `panel.iamfatness.us`; confirm a button works (Start/Stop
   or Announce).
2. In DevTools, right-click the `_blazor` WS row → Close, or briefly stop
   `cloudflared`. The reconnect modal should appear, then recover (buttons work
   again) once the transport returns — **not** stay stuck. Component state (e.g. a
   half-typed announce message) survives the resume.
