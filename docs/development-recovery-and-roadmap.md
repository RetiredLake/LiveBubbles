# Development recovery and next-feature plan

Audit date: 2026-09-07. Baseline: main at b51f46d, package version 0.3.1.2.
This document captures the recovery audit before the v0.3.2.0 implementation. The
implementation and release validation are recorded in the notification plan.

## Development setup recovered

- Repository: C:\Users\Nico\Documents\GitHub\WpBlueBubbles.
- The saved Codex project still targets D:\WpBlueBubbles; D: is not mounted. Open/add the repository at its current path for future tasks. No tool available in this task can change the saved project's path.
- Changed NuGet.config globalPackagesFolder from the old absolute D: path to .packages, relative to the configuration file. This is the only tracked configuration change from setup.
- Restored the solution with VS 2019 MSBuild's Restore target for Release ARM, x64, and x86. All succeeded; no Build, Rebuild, Compile, packaging, or installation targets ran.
- Verified resolved direct dependencies: Microsoft.NETCore.UniversalWindowsPlatform 6.2.14, Microsoft.UI.Xaml 2.7.3, ZXing.Net 0.16.9. The restore graph contains 175 packages, including .NET Native Compiler 1.7.6 and architecture runtimes. Packages resolve from the new local cache and existing Microsoft fallback caches.
- Visual Studio Community 2019 16.11.59 is installed at N:\Program Files (x86)\Microsoft Visual Studio\2019\Community. Its MSBuild and classic UWP XAML targets are present.
- Windows SDK 10.0.19041.0, MSVC 14.29.30133, MakeAppx, and .NET Native compiler executables for ARM/x86/x64 are present. The project minimum remains Windows 10 build 15063.
- A CN=retiredlake certificate with a private key is present in CurrentUser\My and expires in August 2027. Signing and matching it to the existing release chain were not tested.
- A separate modern dotnet SDK is not installed; this classic UWP solution uses the existing VS 2019 toolchain. No SDK upgrade is needed solely to restore this project.
- Preserved the pre-existing untracked BlueBubbles-App-Icon-Blue.png, IDE state, release artifacts, and upstream reference checkouts. No cleanup deletion was necessary.

### Remaining setup and release housekeeping

1. Open the current repository path in Codex, and open WpBlueBubbles.sln in VS 2019 when ready to resume development. The old task's default shell directory remains stale; commands in this audit used an explicit working directory.
2. The ignored WpBlueBubbles.csproj.user still selects an emulator for Debug ARM and contains an old remote-device setting. Choose the actual Lumia Device target before future debugging; do not assume the saved device is still correct. It was preserved during this audit.
3. Three ignored legacy scripts in work (package-v0.1.9.9.ps1, package-v0.2.0.ps1, prepare-github-releases.ps1) still hard-code D:\WpBlueBubbles. They were neither edited nor executed. Replace them with one portable, version-parameterized packaging workflow before the next package release; verify the signing certificate then.
4. The feature matrix and release status were refreshed for v0.3.2.0; keep them aligned with future implementation batches.
5. The notification plan's local unread and five-second polling assumptions are now preserved by the implementation; revisit them only with an explicit product decision.

Setup is restore-verified, not build- or device-verified. Future validation requires a separately authorized build and real Lumia testing.

## Audit findings that determine the roadmap

| Evidence | Current behavior | Consequence |
| --- | --- | --- |
| MainPage.xaml.cs, RefreshMessagesAsync | Skips updates when the last GUID and item count match; otherwise rebuilds the list and scrolls down | Existing-message changes can be missed; history/realtime work must preserve scroll and stable media controls |
| MainPage.xaml.cs, SendCurrentMessageAsync and SendComposedMessageAsync | Sequential sends with no durable per-item completion record; current-chat sends read mutable selection across awaits | Partial failures lack precise retry handling, and chat-switch behavior needs isolation |
| Models/MessageItem.cs, FromJson | Reads attachments[0] into a single set of attachment properties | A multi-attachment incoming message cannot expose all its attachments |
| Services/BlueBubblesClient.cs, GetMessagesAsync; SettingsStore.Load | Always requests offset=0; settings load 15 messages by default with a maximum of 50 | Older history is inaccessible through pagination |
| MainPage.xaml.cs, ApplyChatSearch | Filters loaded chat title, latest preview, and participants | Search does not search full message history |
| App.xaml.cs, OnBackgroundActivated; MainPage startup | Background activation immediately completes; startup disables notifications | Existing NotificationService code is dormant, not a working notification feature |
| Services/NotificationStateStore.cs | Local unread baseline and file-backed state | New realtime and push transports must share consistent deduplication and read rules |
| Services/GitHubUpdateService.cs | Hard-coded repository endpoint/user agent and app-bundle selection | A repository/branding rename needs updater compatibility planning |

The recent patch notes document repeated media and touch-menu regressions, followed by a return to a stable baseline. Keep the existing image/video rendering approach and introduce narrowly scoped changes with acceptance gates. Extract coordination logic from MainPage only where a feature needs it; avoid a broad UI rewrite.

## Proposed delivery sequence

Sizes are relative scope: S = focused change, M = several coordinated changes, L = substantial integration. They are not calendar estimates.

### 1. Reliable sending and saved drafts (M)

User outcome: unfinished text survives navigation/restart, and an interrupted multi-file send clearly identifies what succeeded.

- Persist drafts by server/account and chat, with a separate new-conversation draft. Save text, recipients, and durable attachment references; remove them on successful completion or explicit discard/reset.
- Capture the client, destination chat, text, and attachment list at send start. Prevent concurrent sends from double-click/Enter and stop subsequent operations after account reset.
- Track text and each attachment independently as pending, accepted, failed, or outcome unknown. Keep acknowledged items out of retries.
- Preserve a stable client correlation ID and reconcile with server messages before offering a retry after an ambiguous timeout. Do not assume the server guarantees idempotency or automatically resend unknown outcomes.
- Distinguish send success from refresh failure in both composers. Keep pending content available after partial failure.

Acceptance: draft survives app termination; switching chats during upload cannot change the destination; double Enter sends once; if file two of three fails, retry does not upload file one again; sign-out clears the relevant drafts and pending state. Test with simulated responses first.

### 2. Foreground realtime, incoming typing, and message status (L)

User outcome: new messages and supported delivery/read changes appear promptly without repeated whole-list refreshes.

- Extend the current one-off Socket.IO typing-stop transport into one lifecycle-managed foreground connection, with authentication, heartbeat, reconnect backoff/jitter, and suspension/resume handling.
- Handle new-message, updated-message, typing-indicator, and chat-read-status-changed. The local upstream server reference defines these events; confirm payloads against the supported server versions before implementation.
- Merge by message GUID; replace temporary IDs after acknowledgement. Update existing message properties even when the last GUID and count are unchanged.
- Expire incoming typing indicators automatically. Show sent/delivered/read status only where the server supplies reliable fields; Private API being unavailable must not break basic messaging.
- Retain configurable REST polling as recovery during rollout; reduce it only after successful device soak testing. Reconcile missed events after reconnect and expose unobtrusive connection status.
- Preserve local unread behavior and keep outbound read receipts/typing opt-in. Do not silently adopt the old plan's server-authoritative read policy.

Acceptance: a socket event plus a polling response produces one message; updated-message changes an existing row; reconnect catches missed messages; switching chats during media work cannot populate the wrong conversation; scrolling upward is not interrupted by arrival of a new message; Private API off still supports basic send/receive.

### 3. Notifications and Live Tiles (feasibility S, implementation L)

Run the feasibility check early while planning the previous milestones, because it may change the transport and package-identity plan.

Feasibility gate, before implementing a hosted relay:

- Verify classic UWP WNS registration/credentials and an actual channel on the intended sideloaded package and a real Lumia. A general WNS documentation page does not establish that this device/package combination works today.
- Determine whether Store association would require changing the current WpBlueBubbles/CN=retiredlake package identity. Resolve the installation/data migration implications before combining WNS with a public rename.
- Verify receipt while suspended, background access, battery-saver behavior, and cold/warm toast activation on both PC and Lumia. Record a go/no-go result; do not promise immediate notifications before this succeeds.

If feasible:

- Implement BlueBubbles webhook -> authenticated HTTPS relay -> WNS. Define device registration, renewal, revocation, token refresh, bounded retention, webhook authentication, and replay protection.
- Keep BlueBubbles server passwords out of the relay and logs. Default to a generic notification preview; define privacy controls before transmitting personalized content.
- Use one deduplication/read-state policy across push, foreground events, REST reconciliation, and optional background polling. Suppress outgoing, historical, already-read, muted, and currently viewed messages as appropriate.
- Provide per-chat mute, preview controls, a notification test/diagnostic state, unread badge/Live Tile updates, and exact chat activation. Renew/revoke channels on lifecycle changes.
- Restore the actual background execution path rather than merely enabling the existing toggle/service. Make state updates atomic across overlapping background and foreground operations.

If WNS is not viable on target Lumia hardware, ship clearly labeled best-effort periodic checks plus foreground notifications first. The existing 15-minute TimeTrigger approach is delayed and OS-scheduled; it is not a realtime substitute.

Acceptance: one message causes at most one alert; baseline sync causes none; mute and read suppression work; cold and warm activation open the right chat; reset revokes registration and removes notification state; expiration/network recovery do not require reinstalling.

### 4. Complete attachments (M)

User outcome: every photo, video, and file in an incoming message can be viewed, saved, or forwarded.

- Add an attachment collection with per-item metadata and loading/error state. Adapt the current renderer as repeated items without replacing the proven media pipeline.
- Make Save/Forward target a chosen attachment or explicit selection. Preserve filenames and show an understandable fallback for unsupported formats.
- Bound concurrent downloads and memory use on Lumia; cancel stale work when leaving a conversation. Preserve existing touch/hold and desktop context-menu behavior.

Acceptance: a mixed photo/video/file message exposes all items; one failed item does not hide the rest; all media remains associated with its own chat after rapid navigation.

### 5. Older history, scoped search, and optional offline reading (L)

User outcome: users can reach an older message and find it again without losing their place.

- Add bounded older-message pages with cursor/before semantics when supported, or stable offset reconciliation when necessary. Deduplicate overlapping pages and preserve the top visible message when prepending.
- Keep new-message reconciliation independent of loaded historical pages. Add a jump-to-latest control rather than forcing scroll.
- First add explicit in-chat search over loaded/cached messages, with a clear scope label. Then verify and add server search with pagination and jump-to-result; do not label preview filtering as full history search.
- Add optional bounded local message caching only after pagination/merge behavior is stable. Partition caches by server/account; provide storage controls and clear them on reset. Avoid downloading all media eagerly.

Acceptance: load beyond 50 messages without omissions/duplicates during concurrent arrivals; retain viewport; clearly distinguish cached from full-server search; resolve a result outside the initial window; offline mode shows its freshness and storage limits.

### Validation for future implementation

- Add focused tests for state reconciliation, event deduplication, send outcome handling, draft persistence, paging boundaries, and attachment parsing. No tests or test code were written now.
- Then build Release ARM/x86/x64 and test on real Lumia plus desktop, with Private API both available and unavailable.
- Preserve known-good QR/manual setup, keyboard behavior, photo/video playback, share-target completion, context menus, themes, reset, and updater behavior.
- No test may send messages or perform destructive actions on a live server without explicit consent. Use fixtures and a controlled test server for automated validation.
- Defer reactions, editing/unsending, scheduling, and major theme redesign until these core flows are stable.

## Rename assessment

Recommendation: LiveBubbles, provisionally, with the subtitle "A BlueBubbles client for Windows" and developer attribution to retiredlake.

| Criterion | LiveMessages | LiveBubbles |
| --- | --- | --- |
| Immediately explains messaging | Strong | Needs a subtitle for newcomers |
| Windows Live/Messenger association | Strong, but can suggest Microsoft affiliation | Strong enough through "Live" and the visual style |
| Continuity with BlueBubbles | Weaker; better suited to a future multi-backend product | Strong and appropriate to this project's present purpose |
| Distinctiveness | More descriptive/generic | More memorable and visually flexible |
| Existing uses found in this audit | Close to Salesforce's LiveMessage messaging product | Exact-name LiveBubbles game and publishing uses exist |

This is a product-name comparison and a preliminary public search, not confirmation that either name, domain, store listing, or handle is available. Check desired identifiers and naming conflicts before reserving or publishing. Salesforce's documented LiveMessage name is singular; it is a close-name collision, not evidence of an exact LiveMessages product.

Choose LiveMessages instead if the intended direction is a broader Windows messaging hub with several backends. For the current dedicated BlueBubbles client, LiveBubbles communicates continuity better.

### Rename rollout

1. Decide the public name and subtitle, and check availability. Do this before investing in final icons or Store/WNS registration.
2. Start with display branding: manifest DisplayName/ShortName/Description, app heading and errors, assembly descriptive metadata, logos/splash/tiles, README, screenshots, credits, and release wording. Keep references to the BlueBubbles server accurate in setup instructions.
3. Preserve installation identity initially: Identity.Name=WpBlueBubbles, Publisher=CN=retiredlake, application Id=App, PhoneProductId, and the existing signing chain. Keep version numbers monotonically increasing from 0.3.1.2.
4. Preserve namespace/assembly/entry-point names, PasswordVault resource WpBlueBubbles.Server, stored setting/file keys, background-task identifier, chat= activation arguments, and chat tile IDs. These internal names need not match the public label. A display rename should not require users to sign in or repin chats.
5. The v0.3.2.0 branding release renamed the public repository to LiveBubbles and
   updated the updater URL, user agent, credits, and documentation together.
   GitHub redirects the old endpoint for older clients; verify that redirect and
   future package downloads on upgrade paths rather than relying on it blindly.
6. Verify upgrade from an installed 0.3.1.2 package on PC and Lumia: same app identity, settings/vault credentials retained, unread state and pinned chat tiles preserved, share target and toast activation working, installer accepted, and updater still functional. Previously pinned visuals may need an explicit refresh.
7. Treat a new Store/WNS package identity as a separate migration project with an explicit data-transfer/re-authentication plan. Do not hide it inside cosmetic branding.

The display rename shipped as part of v0.3.2.0 while preserving installation identity.
Keep any future repository or Store identity migration separate from notification
transport work.

## External sources checked

- Microsoft WNS overview: https://learn.microsoft.com/en-us/windows/apps/develop/notifications/push-notifications/wns-overview
- Salesforce LiveMessage Classic retirement and naming: https://help.salesforce.com/s/articleView?id=000380605&language=en_US&type=1
- Existing LiveBubbles game: https://muhmad2.itch.io/livebubbles

Local source files and the copied upstream server were inspected without contacting a configured BlueBubbles server. No runtime behavior or naming availability is claimed beyond the checks described above.
