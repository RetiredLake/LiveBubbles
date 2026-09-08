# LiveBubbles notifications

## Implemented transport

LiveBubbles uses a dedicated connection to the BlueBubbles server's existing
Socket.IO endpoint and Windows' socket broker. The app configures this connection
from its normal saved sign-in. Users do not register Firebase projects, provision
WNS credentials, deploy a relay, add webhooks, or enter notification keys.

This replaces the earlier WNS relay proposal. The Microsoft developer license is
not an end-user dependency. Normal Windows notification/background permissions
still apply; the app requests access automatically and shows a useful status if
Windows denies it.

The architecture is similar to Unison's documented StreamSocket/socket-broker
approach. The implementation here is original and does not include Unison source.
Unison's current documentation also describes an incomplete handoff in its newer
socket bridge, so it is an architectural reference, not proof of Lumia delivery.

## Components

- `src/LiveBubbles.Notifications`: independent classic UWP runtime component,
  minimum build 15063, packaged with the existing app identity.
- `BrokerConnection` and `WebSocketWire`: authenticated Engine.IO 4 / Socket.IO
  connection over a TLS-validated StreamSocket for HTTPS servers. HTTP follows
  the user's existing server address. Frames are bounded, client-masked, and
  processed without read-ahead so ownership can safely return to Windows.
- `NotificationTask`: out-of-process `SocketActivityTrigger` entry point. Handles
  a packet, replies to heartbeat traffic, and returns socket ownership. Does not
  create App/XAML or access the app's message/media renderer.
- `NotificationRuntime`: notification-only event handling and metadata recovery.
  New-message events alert; outgoing messages, reactions, receipts, and group
  maintenance do not. It never downloads attachment content. Reconnects rotate
  the broker socket ID when Windows still has an older entry, avoiding stale
  ownership collisions.
- `NotificationStorage` and `NotificationPolicy`: cross-process file lock,
  durable GUID deduplication, read and replay watermarks, mute/visible-chat
  suppression, and bounded retention. Payload text and server passwords are not
  stored in the notification ledger.
- `NotificationPresenter`: native toast, unread badge, primary Live Tile, and
  `chat=<escaped-guid>` activation. Message previews are enabled by default and
  can be switched off for private notifications.
- `MainPage.Notifications.cs`: settings and foreground visibility integration.
  Existing message fetching, sending, sharing, and media rendering remain intact.

The socket belongs to the broker between packets even while the app is open.
There is no fragile foreground-to-background handoff of the media/client socket.
The UI keeps its existing polling behavior.

## Setup and lifecycle

1. After normal successful sign-in, request Windows background access and
   automatically register socket, network-change, and 15-minute recovery tasks.
2. Connect using the saved server URL and PasswordVault credential. Socket.IO's
   query authentication matches the existing BlueBubbles client transport.
3. Accept the authenticated Socket.IO namespace, transfer ownership, and check
   recent chat metadata to reconcile events missed while disconnected.
4. On socket closure/idle expiry, reconnect. Network changes and periodic recovery
   offer further retries when the network is unavailable.
5. Disabling notifications/sign-out changes a generation token before unregistering
   tasks and closing the socket. Stale work cannot alert for a previous session.
6. Re-register background access after package-version changes. Preserve the
   WpBlueBubbles/CN=retiredlake installation identity and vault resource.

## User controls

- Message notifications: automatically enabled on first successful sign-in;
  turning them off persists across launches.
- Show names and message previews: enabled by default; turn the switch off for private notifications.
- Test connection and notification plus reconnect controls: available only with
  Developer mode enabled. The test performs a fresh authenticated socket
  handshake and broker handoff before showing the local toast.
- Connection status and diagnostics: available only with Developer mode enabled.
- Mute/unmute notifications: available in each conversation's chat-actions menu.

Tapping a notification opens its conversation, including chats outside the selected
history filter. Missing/offline conversations show an error rather than silently
ignoring activation. Opening a conversation clears its notification and advances
its notification read watermark without enabling server read receipts.

## Delivery limits and acceptance checks

This is a direct socket notification implementation, not FCM or hosted WNS push.
Windows controls background scheduling, power limits, and whether the device can
wake. The code selects wake support where Windows exposes it; ordinary sleeping
PCs may not wake. Force-termination, network/tunnel changes, and OS task quotas can
interrupt delivery. A live connection does not guarantee an OS-displayed toast.

BlueBubbles' Engine.IO heartbeat can wake the task frequently; measure battery
impact on the real Lumia. Recovery polling is best-effort, not a realtime
substitute. Its chat-summary endpoint recovers the latest message in each chat,
not every intermediate message from a long offline interval. Several alerts from
the same chat replace that chat's Action Center entry.

Deduplication retains 256 IDs per chat and up to 2,000 chat cursors. Eviction
advances replay watermarks to avoid re-alerting old history; exceptionally late
messages older than these watermarks are suppressed. Notification badge state is
separate from the app's pre-existing local unread decoration and tracks incoming
notification activity after enablement. Initial historical messages do not alert.

Before calling this ready for release, verify on a real Lumia and desktop:

- Sign in with no additional notification configuration; receive a message with
  screen on, app minimized, screen off, and app suspended.
- Compare socket plus metadata recovery: one incoming message alerts at most once.
- Test heartbeat, fragmented packets, network changes, tunnel reconnects, and
  battery-saver/task-revocation recovery over an extended session.
- Suppress active-chat, muted, outgoing, already-read, reaction, and history alerts.
- Test cold/warm toast activation, archived/out-of-filter chats, disable/re-enable,
  and reset during a pending connection.
- Check primary Live Tile/badge updates and clearing, private previews, and
  notification permission denial.
- Upgrade from 0.3.1.2 without losing credentials, settings, or pinned chat tiles.
- Retest existing media playback/share flows on-device even though their source
  is unchanged, since a new background component can affect process resources.

## Automated validation

Run `tests/Notifications/Run-Tests.ps1` for notification-policy and RFC 6455 tests
using in-memory streams, with no live-server traffic. Run
`python tests/Notifications/Verify-Media-Unchanged.py` to compare the protected
multimedia source against b51f46d. Build the solution in VS 2019 for Release ARM,
x86, and x64. Compilation does not verify broker wake or notification delivery.

## References

- [Windows socket broker](https://learn.microsoft.com/en-us/windows/uwp/networking/network-communications-in-the-background)
- [SocketActivityTrigger](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.background.socketactivitytrigger)
- [Unison background-broker design](https://github.com/MaskNinjaSquared/Unison/blob/main/docs/wiki/Background-Broker.md)
- BlueBubbles server reference: `work/upstream-bluebubbles-server`, event constants,
  message serializer, and Socket.IO routes. Existing credentials remain local.

## Verification of this development revision

- Release ARM, x64, and x86: passed, including .NET Native compilation.
- 31 offline notification-policy and WebSocket checks: passed.
- Source preservation: BlueBubblesClient.cs, MessageItem.cs, 29 pre-existing
  message/media methods, and the XAML resources/message templates match b51f46d.
- Generated manifests retain WpBlueBubbles / CN=retiredlake, show LiveBubbles
  0.3.2.4, and register the new background runtime component.
- No app was installed and no live BlueBubbles server was contacted for testing.
  Device delivery remains unverified and requires real Lumia validation.
