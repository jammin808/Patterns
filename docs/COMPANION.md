# Companion — what the research found, and what Patterns does about it

*Round 54 (`docs/PLAN.md` §72). The brief asked for Patterns and Bitfocus Companion to interact in
new ways — speed of setup, total control including the nodes around the desk, live feedback in
useful colours — and for the latest Companion development documents to be read first. This page is
the reading, dated September 2026, and where each finding landed. Sources at the end.*

## 1. Where Companion is

- **Companion 5.0** is the current release (5.0.5 at the time of reading). Modules are installable
  plugins since 4.0 — from the module store, or an offline bundle imported under *Modules* — and
  since 4.3 surfaces are modules too. 5.0's button graphics are a stack of elements (text, box,
  image, gauge, line, circle, groups) with an image library and fonts; actions can return a result
  into a variable; expressions have loops and control statements; **Companion announces its
  satellite ports over mDNS**.
- **The module base is 2.x** (`@companion-module/base` 2.1.3, June–August 2026; 2.0 in March).
  Breaking from 1.x: the entrypoint is the module's default export (no `runEntrypoint`), with
  `UpgradeScripts` as a named export; variable definitions are an object, not an array;
  `setPresetDefinitions(structure, presets)` takes sections with ids; `checkAllFeedbacks` is its
  own call; feedbacks lost their subscribe callbacks; `learn` returns only the learned values;
  `parseVariablesInString` is gone; the manifest needs `type: "connection"` and a `node22` or
  `node26` runtime. New: preset *sections* and *groups* (including template groups), *layered*
  presets built from graphics elements with **gauges**, *alternatives* (several variants of one
  preset, the host picks the first it can draw), *text* presets, feedback-based local variables,
  abort signals on actions and feedbacks, `secret-text` config fields, `isVisibleExpression` on
  fields, **`bonjour-device` config fields with `bonjourQueries` in the manifest** (a `type` and
  `protocol`, optional `port`, `txt` filters and address family; Companion browses with
  `@julusian/bonjour-service` and hands the module `address:port`, or null for *Manual*).
- **The host's checks**: `@companion-module/host` sanitises every preset — unknown action or
  feedback ids, unknown option keys, layered elements against a schema (coordinates 0–100,
  colours as integers), style overrides against declared element ids — and drops what fails with
  a warning. Patterns' module suite runs its presets through that same sanitiser.
- **Companion's own APIs for being driven**: the TCP remote-control API (port 16759, lines
  answered `+OK` / `-ERR`) with `SURFACE <id> PAGE-SET <n>` / `PAGE-UP` / `PAGE-DOWN`, `LOCATION
  <page>/<row>/<column> PRESS` / `DOWN` / `UP` / `ROTATE-LEFT` / `ROTATE-RIGHT` / `SET-STEP` /
  `STYLE TEXT|COLOR|BGCOLOR`, `SURFACES RESCAN`, `CUSTOM-VARIABLE <name> SET-VALUE|GET-VALUE`; the
  same over UDP, HTTP (`/api/location/<p>/<r>/<c>/press`, `/api/custom-variable/<name>/value`),
  OSC, Ember+ and Art-Net.
- **The Satellite API** (TCP 16622, WebSocket 16623; announced over mDNS as
  `_companion-satellite-tcp._tcp` and `_companion-satellite-ws._tcp` with the version in the TXT
  record): a client says `ADD-DEVICE DEVICEID=… PRODUCT_NAME=… KEYS_TOTAL=… KEYS_PER_ROW=… BITMAPS=…
  COLORS=… TEXT=…`, receives `KEY-STATE` with bitmaps, colours and text per key, sends `KEY-PRESS`
  and `KEY-ROTATE`, and since 4.2 may describe a complex layout; 5.0 adds compressed images.

## 2. What that meant for Patterns

1. **The module as it stood (2.8.0, base 1.11, node18) could not be loaded by Companion 5.** That
   came first: the port to base 2.x is 54.1, with every id kept so saved pages keep working, and a
   test suite that boots it on the real base with a fake host, so the next base change is a failing
   test rather than a venue.
2. **Speed of setup is discovery.** `bonjour-device` in the connection plus a desk that announces
   `_patterns._tcp` means the connection dialog lists "Patterns desk FOH-PC" and nobody types an
   address (54.2). Every node announces too, by kind, so a Companion can drive an arcade or a stage
   timer node directly. The desk browses back for `_companion-satellite-tcp` and names the
   Companion in the room.
3. **Total control, both ways.** The wire already had the nodes', the twin's, the stage's, the
   plan's and the arcade's verbs; the module gained their keys (54.1) and STATE gained the room
   around the desk so the keys light (54.4). The other direction — the desk turning the deck's
   pages — is Companion's TCP API as a device profile (54.3): a cue's `DEVICE Companion PAGE 3`.
4. **Live feedback in useful colours.** One palette on both sides, a hue per kind and a treatment
   per state, held equal by a test (54.4); the stage timer's key wears the timer's own colour and,
   on Companion 5, a ring of how far through — the first use of layered presets here, offered as
   an *alternative* with the plain key as the fallback.

## 3. Considered and left

- **Patterns as a Satellite surface** — the desk, a node or a phone page showing Companion's
  buttons. The protocol is simple enough (above) and it would put a Stream Deck on any screen
  Patterns has; but Companion's own emulator page already does that in any browser, and the desk's
  pages are the desk's. Left until somebody at a desk asks for it.
- **The module store.** Publishing the module to Companion's store is a submission, not code;
  until then the CI's `.tgz` is imported under *Modules*.
- **Template preset groups** (one template, many values) could replace the per-item *… — this
  show* presets; the per-item presets are simpler to read in Companion's list and cost nothing.
- **Companion's HTTP API** as the driving path instead of TCP: the TCP API answers every line,
  which is what a receipt needs; the HTTP profile is there for anything else.

## 4. Unverified here

No Companion ran in this environment. What is verified: the module boots on the real module base
(2.1.3), its manifest validates, its presets pass Companion's own sanitiser, every line it sends
parses on the desk, the desk answers a real mDNS query on the loopback, and the Companion profile's
lines are the documented ones. What a Companion 5 in the room will confirm: the `.tgz` importing,
the bonjour pick listing the desk, a layered key drawn, the TCP API answering `+OK`.

## Sources

- github.com/bitfocus/companion-module-base — the monorepo's CHANGELOG (1.10 → 2.1.3), the
  manifest schema, and the typings read from the installed package (`base.d.ts`, `input.d.ts`,
  `feedback.d.ts`, `action.d.ts`, `preset/*.d.ts`, `graphics.d.ts`, `enums.d.ts`).
- github.com/bitfocus/companion-module-host — `instance.d.ts`, `internal/presets.js`,
  `schema/elements.js`.
- github.com/bitfocus/companion — `companion/lib/Service/TcpUdpApi.ts`, `Tcp.ts`,
  `MdnsAdvertise.ts`, `BonjourDiscovery.ts`, `Satellite/SatelliteApi.ts`,
  `Instance/Connection/Thread/Entrypoint.ts`, `Instance/Connection/ApiVersions.ts`,
  `webui/src/Components/BonjourDeviceInputField.tsx`, and CHANGELOG.md (4.0 → 5.0.5).
- github.com/bitfocus/companion-module-template-js — `companion/manifest.json`, `package.json`.
- RFC 6762 (multicast DNS) and RFC 6763 (DNS-based service discovery).
