# Patch Notes

All releases are beta software. Versions before v0.2.0.0 are retained as legacy
builds and may contain bugs fixed by later releases.

## v0.3.2.4 LiveBubbles

- Rebuilds notification task registration without calling the failing
  `BackgroundExecutionManager.RemoveAccess` path during sideloaded updates.
- Separates background access, task registration, and reconnect failures in
  Developer details so registration failures identify the failing operation.

## v0.3.2.3 LiveBubbles

- Rotates stale Windows socket-broker IDs during notification reconnects to
  avoid invalid ownership state after an interrupted handoff.
- Retries socket ownership setup without low-power wake when the device rejects
  the wake mode.
- Makes the Developer-mode test perform an authenticated connection and broker
  handoff before showing a local toast.
- Adds operation-specific diagnostics for background notification failures.

## v0.3.2.2 LiveBubbles

- Returns broker-reclaimed sockets directly from background tasks instead of
  repeating foreground I/O cancellation, preventing `E_ILLEGAL_METHOD_CALL`
  interruptions during notification delivery.
- Detaches managed stream wrappers before background ownership is returned.
- Reports the exact socket-broker operation when a device rejects ownership
  setup or transfer.
- Refactors remaining product-facing labels, filenames, setup text, and errors
  to LiveBubbles or neutral server wording.

## v0.3.2.1 LiveBubbles

- Fixes SocketActivityTrigger delivery by using the active background task
  registration and reconnecting stale sockets after a close.
- Keeps broker ownership across normal keep-alive callbacks instead of treating
  the configured timer as an interruption.
- Defaults notification previews to on; status, diagnostics, test, and reconnect
  controls remain available only in Developer mode.
- Corrects remaining LiveBubbles product-facing error text while preserving the
  existing BlueBubbles server and media behavior.
- Real Lumia background delivery still requires device validation.

## v0.3.2.0 LiveBubbles

- Renames the displayed app to LiveBubbles while preserving installation identity,
  credentials, namespaces, pinned-chat activation, and update endpoints.
- Adds a separate notification background component using the BlueBubbles
  Socket.IO endpoint and Windows socket broker; it connects after normal sign-in.
- Adds private-by-default native toasts, unread badges/Live Tiles, per-chat mute,
  a notification toggle, local test notification, and reconnect control.
- Adds notification deduplication, read/visible-chat suppression, bounded state,
  network recovery, and safe disable/reset handling.
- Resolves toast activation for chats outside the current history filter.
- Preserves the existing message/media client, rendering, sends, saves, and shares.
- Repairs SDK discovery for the moved development environment.
- Real Lumia background-delivery, battery, and upgrade validation remains required.

## v0.3.1.2 Beta

- Fixes the message action menu failing to open with `Arg_ArgumentException` by using the explicit classic UWP flyout placement supported by the stable client.
- GitHub release downloads now publish only the complete ZIP and universal ARM/x86/x64 app bundle.

## v0.3.1.1 Beta

- Restores the System, Light, Blue, and Dark app theme selector without changing the stable media, polling, or message-loading paths.
- Adds Private API gated Send read receipts and Send typing indicators settings; both default to off.
- Adds carefully isolated message actions in the order Delete, Forward, Copy, and Save.
- Message deletion requires Private API and an explicit permanent-deletion confirmation.
- Forwarding reuses the existing compose and attachment-send workflow.

## v0.3.1.0 Beta

- Restored the complete v0.2.2.1 application codebase, including its proven image/media renderer, connection flow, polling, sending, actions, themes, and settings.
- Added only a manual GitHub app-bundle updater in Settings.
- Added accent-aware Windows rolling dots during the initial chat-list load and user-opened conversation loads only.
- No message, media, server-client, polling, send-path, context-menu, model, or theme behavior was backported from v0.3.x.

## v0.2.2.1 Beta

- Added an optional, persisted Send read receipts toggle. It is off by default.
- Kept local unread tracking active when server read receipts are disabled.
- Fixed message and media context menus so desktop right-click no longer opens overlapping menus or crashes.
- Fixed touch-and-hold Copy and Save menus closing before an action could be selected on Windows 10 Mobile.
- Updated the credits link to the LiveBubbles GitHub project.

### Coming soon

- Notifications and Live Tiles.
- Read receipts and incoming typing indicators.

## v0.2.2.0 Beta

- Made share-target files durable before upload so images remain attached throughout Compose.
- Resolved selected contacts to existing conversations before enforcing the new-chat text requirement.
- Closed Compose and the Windows share sheet immediately after a successful send.
- Navigated to the destination conversation independently of the subsequent chat refresh.
- Returned to Chats with a clear error if the destination conversation could not be opened.
- Prevented refresh failures from leaving Compose or the share sheet hanging after a successful send.
- Preserved original shared filenames and removed temporary share copies after completion.

## v0.2.1.9 Beta

- Restored reliable image rendering by returning to the proven direct attachment path.
- Prevented recycled image controls and hidden video controls from invalidating successfully loaded pictures.
- Preserved on-demand video playback, which was confirmed working in the release candidate.
- Made physical-keyboard Enter send on desktop and Shift+Enter insert a newline; phone Return remains newline-only.
- Made desktop right-click consistently offer Copy for messages and Save for media.
- Added a persistent 3/5/10/15/30-second message fetch setting, defaulting to five seconds.
- Kept search, compose, and conversation actions visible together in wide desktop layouts.
- Removed focused white backgrounds and borders from both message composers.
- Corrected Windows accent mode so only outbound bubbles use the selected accent; incoming bubbles remain gray.
- Added clearer notification placeholder text and a manual Private API status refresh.

## v0.2.1.8 Beta

- Restored image rendering with lazy authenticated downloads for visible messages, without blocking chat history on media.
- Limited concurrent image downloads and reused temporary image files during the session.
- Made physical-keyboard Enter send on desktop and Shift+Enter insert a newline; phone Return remains newline-only.
- Added a persistent 3/5/10/15/30-second message fetch setting, defaulting to five seconds.
- Kept search, compose, and conversation actions visible together in wide desktop layouts.
- Removed focused white backgrounds and borders from both message composers.
- Changed the normal notification placeholder to "Notifications and Live Tiles coming soon" while preserving diagnostics copy in Developer mode.
- Added a manual Private API status refresh when the helper is unavailable or its status cannot be read.
- Fixed accent mode so outbound bubbles immediately use the actual Windows accent color; incoming bubbles remain gray.

## v0.2.1.7 Beta

- Restored the pre-v0.2.1.5 media path: chats load attachment metadata immediately and visible media streams from BlueBubbles on demand.
- Removed eager sequential media downloads and local media conversion from chat loading.
- Added bounded retries to safe server reads and chat-list queries without retrying sends, deletes, renames, or other mutations.
- Reused one server-info response during connection setup instead of issuing duplicate requests.
- Made a successful authenticated connection survive an initial chat-sync failure; normal polling retries the sync automatically.
- Kept manual Save media as an explicit on-demand download.

## v0.2.1.6 Beta

- Anchored the newest message above the Lumia keyboard after its final layout pass.
- Avoided the Lumia close/reopen keyboard flicker after sending; PC still refocuses for continued typing.
- Made OLED black the default for new and reset installations.
- Applied compact header sizing to Settings, Contacts, and New message pages.
- Added raw exception diagnostics only when Developer mode is enabled.
- Removed the redundant public video-decoding error while retaining the unavailable label.
- Downloaded pictures and videos into a local media cache before rendering.
- Restored gray incoming bubbles; sent bubbles remain Messenger blue or Windows accent.
- Corrected the phone QR preview rotation in the opposite direction.
- Expanded sign-out/reset to remove local files, temporary media, caches, unread state, contacts, and credentials.

## v0.2.1.5 Beta

- Added a GroupMe-inspired compact phone layout with an optional Larger UI mode.
- Kept the Lumia keyboard focused while a message is sending and disabled input without removing focus.
- Made physical-keyboard Enter send on PC and Shift+Enter add a line; mobile Return always adds a line.
- Added clearer offline, unreachable-server, missing-conversation, send, rename, and delete errors.
- Added multiple attachment selection and multi-file share-target sending.
- Cached downloaded videos locally before playback and added an explicit unsupported-video error.
- Made unread state phone-local and treats the initial post-setup chat list as read.
- Sorted Chats newest-first and made search appear only when requested from the header.
- Added a secondary Windows accent shade for sent messages while retaining Messenger blue otherwise.
- Hardened legacy settings migration so malformed values cannot repeatedly crash startup.
- Corrected the phone QR camera preview rotation.

## v0.2.1.0 Beta

- Reorganized connected Settings into Sync, Theme, Server details, Reset, and Credits sections.
- Added persistent OLED-black and Windows accent-color themes with immediate updates.
- Unified incoming and outgoing bubbles under the selected Messenger blue or accent color.
- Added sanitized server details, Private API state, and a reserved Developer mode toggle.
- Added contact-resolved sender labels to incoming group-chat messages.
- Added a full-screen picture viewer that closes by tap or system Back.
- Added cached contact and group photos to unique per-conversation Start tiles, with safe logo fallback.
- Removed all visible composer service-availability status text while retaining internal routing checks.

## v0.2.0.1 Beta

- Matched the official BlueBubbles full-sync behavior by hiding chats whose latest message falls outside the selected timeframe.
- Made the server's chat-level `hasUnreadMessage` value authoritative so read state follows the Mac.
- Added paginated chat loading beyond the previous 1,000-chat ceiling.
- Prevented stale overlapping chat refreshes from replacing a newer timeframe result.

## v0.2.0.0 Beta

- Hotfix: moved growing unread/message state out of `ApplicationDataContainer.Values` and into atomic file-backed storage to prevent the settings size-limit crash.
- Hotfix: pruned stale per-chat state and moved startup diagnostics out of application settings.
- Added Blue iMessage/RCS and green SMS service styling.
- Added capability-gated iMessage availability checks.
- Added typing start and inactivity stop behavior.
- Added persistent read/unread state and server-side mark-as-read support.
- Added group photos, multi-recipient creation, rename, leave, and guarded delete.
- Added a Socket.IO stop-typing workaround for BlueBubbles Server 1.9.9.
- Added the WNS notification implementation plan; notifications remain disabled.

## v0.1.9.9 Legacy Beta

- Added an expanding, wrapping message composer with improved keyboard behavior.
- Added transparent Start, splash, package, and lock-screen branding.
- Added contact photos and stronger post-send conversation navigation.
- Improved bottom anchoring while message media loads.

## v0.1.9.8 Legacy Beta

- Opened conversations at their newest message.
- Added hold-to-save for received photos and videos.

## v0.1.9.7 Legacy Beta

- Fixed recycled message visuals leaking between conversations.
- Anchored the top bar and composer around the Lumia on-screen keyboard.

## v0.1.9.6 Legacy Beta

- Removed obsolete QR setup controls and improved QR sign-in progress.
- Corrected composer colors and several cross-chat async refresh races.
- Stabilized the message input above the on-screen keyboard.

## v0.1.9.5 Legacy Beta

- Kept the conversation header visible while composing.
- Added clickable URLs and short-lived network error messages.
- Improved share-target completion and registered-number display.
- Added initial video send and receive support.

## v0.1.9.0 Legacy Beta

- Set persistent sync defaults and refined the dedicated Settings page.
- Combined sign-out and local reset with a server-data safety confirmation.
- Disabled incomplete notification and Live Tile behavior.

## v0.1.8.0 Legacy Beta

- Corrected the full-width Lumia chat list and mobile navigation.
- Added native sync progress reporting and improved composer layout.
- Improved contact integration and per-chat Start tiles.

## v0.1.7.0 Legacy Beta

- Added dedicated Contacts and Compose pages.
- Improved photo attachment validation and conversation navigation.
- Added media context actions and initial per-chat pinning.

## v0.1.6.2 Legacy Beta

- Rebuilt the ARM package cleanly after the startup regression fix.
- Refreshed signed bundle metadata and dependencies.

## v0.1.6.1 Legacy Beta

- Fixed an ARM .NET Native XAML startup `InvalidCastException`.
- Preserved the improved mobile navigation and composer behavior.

## v0.1.6.0 Legacy Beta

- Added responsive Lumia navigation, archived-chat views, and chat actions.
- Added dedicated compose and contact flows.
- Added sync controls, safer setup, and initial attachment/share integration.
