# Job Configurator handover: onboarding request concurrency

## Observed failure

On 13 August 2026, Job Configurator at `192.168.0.16:8091` initiated remote
onboarding for endpoint `b0ea92d2-2746-4da4-addb-5d510f01c5d3` through the PC
Agent on TCP 8094. The user approved locally and the elevated client started,
but its configuration GET timed out after ten seconds. The PC displayed:

```text
Remote onboarding did not complete.
The operation was canceled.
```

During and after the attempt, both of these server requests timed out:

```text
GET /api/health
GET /api/pc-onboarding/configuration/{endpointId}
```

The server continued opening short-lived connections to the agent monitoring
API, so the PC agent and selected LAN remained available. No membership was
recorded and the PC network configuration was unchanged.

## Agent-side mitigation in 0.3.1

The PC agent now completes the local approval callback and sends HTTP 202 before
launching the elevated client. It waits one second after approval before calling
the installed utility with `runas`. This breaks the immediate POST-to-GET request
cycle and gives the Configurator time to finish its open-onboarding handler.

The agent installer also launches the resident tray process through the desktop
shell so it runs unelevated. Remote onboarding should therefore show a separate
Windows UAC prompt after local approval. Configuration timeouts now produce an
actionable server-responsiveness message instead of the raw cancellation text.

## 0.3.1 retry result

The updated agent was installed on the same PC and verified with a Windows token
probe as unelevated. Its TCP 8094 and UDP 8093 listeners were healthy and the
status API reported agent version `0.3.1` at `192.168.0.164`.

A second server request at approximately 17:16 local time produced the intended
sequence: local acceptance, HTTP acknowledgement/deferred launch, a distinct
Windows UAC prompt, and an elevated onboarding process. After UAC approval, the
client waited for the configuration GET and displayed the new timeout message.
Independent probes immediately after the retry confirmed that both `/api/health`
and the configuration endpoint still timed out. The PC retained DHCP address
`192.168.0.164/24`, recorded no membership, and the agent stayed online.

This confirms the original missing-UAC and request-ordering defects are fixed on
the agent. The remaining blocker is server responsiveness and requires the
server checks below.

## Pending registration state

The server was still showing **waiting for registration callback** after the
0.3.1 client had already reported its configuration timeout. No registration
will arrive for that attempt: registration occurs only after the client has
successfully fetched and validated configuration, applied the requested network
and NDI settings, and refreshed agent state.

Use this server state machine:

1. `staged`: configuration is stored before the open request;
2. `awaiting-local-approval`: POST to the agent is outstanding;
3. `awaiting-registration`: agent returned HTTP 202;
4. `completed`: registration arrived for the stable endpoint ID;
5. `failed/timed-out`: registration did not arrive within a bounded period.

HTTP 202 must start a finite registration timer, not an indefinite wait. Use a
timeout such as 90 seconds, clear the active operation, keep or restage the
configuration according to its TTL, and show an actionable retry state. If the
configuration endpoint was never read successfully, report that specifically.

The current agent/server contract has no failure callback endpoint. Do not use
the successful registration endpoint to report failure because that would create
a false onboarded membership. If immediate failure reporting is required later,
add a separate authenticated result endpoint such as:

```text
POST /api/pc-onboarding/result
```

with endpoint ID, operation ID, success flag, stable error code, safe message,
and observed time. Until both products implement that extension, timeout plus
server-side request/configuration logs is the correct failure mechanism.

## Required server checks

The agent mitigation should allow a retry, but the server should still satisfy
all of these requirements:

1. Stage the endpoint configuration before sending the POST to the agent.
2. Do not hold an application-wide lock, UI dispatcher, request semaphore, or
   single-threaded event loop while awaiting the agent's POST response.
3. Keep `/api/health` responsive while the POST is pending.
4. After receiving HTTP 202, immediately finish and dispose the POST response.
5. Serve the configuration GET concurrently and return JSON within ten seconds.
6. Keep staged configuration available until registration succeeds or its TTL
   expires; do not consume it on the first GET.
7. Treat HTTP 202 as local approval and scheduled elevation only. Registration
   by stable endpoint ID remains the success signal.

If `/api/health` remains unresponsive after the failed request, restart the
Configurator process before retrying and inspect server logs for a held lock,
blocked dispatcher, or exhausted request worker.

## Retry evidence to capture

- POST response status and elapsed time;
- health response during the approval prompt and immediately after HTTP 202;
- configuration GET start, completion status, and elapsed time;
- registration receipt and stable endpoint ID;
- any server lock/dispatcher diagnostics;
- confirmation that the PC showed local approval followed by a distinct UAC
  prompt.
