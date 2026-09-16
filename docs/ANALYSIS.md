# Static analysis as fences — Roslyn analyzers, Sonar's rules and a banned-API list in every build

*Round 70. The question was whether SonarQube, Roslyn analyzers or StyleCop could improve Patterns.
The answer is a measured one: the SDK's analyzers, Sonar's rules (the same rules a SonarQube server
runs, here inside the compiler) and the threading analyzers now run on every build with a named set of
rules that stop it — fences, each with its reason written beside it — and a banned-API list of the
calls the desk never makes. StyleCop was measured and left out. This paper has the baseline counted
before anything was fixed, the tools judged, the fences and the bugs they found.*

## 1. The answer, first

- **Roslyn analyzers: yes, as fences.** The .NET SDK ships some three hundred rules; the build ran a
  dozen of them by default. With every rule on, the tree had 7,359 findings — most of them naming and
  API-shape rules written for libraries, not a desk. The rules that catch what breaks a show are a
  smaller set: culture-sensitive formatting and comparison (the wire and the files must read the same
  on a German machine), disposables never disposed (a leak for the length of a show), blocking waits on
  the UI thread (a hang), tasks started on whatever scheduler is current (work landing on the UI
  thread), P/Invokes without a search path (a DLL planted beside a portable exe), results ignored (an
  attempt taken for a fact). Those are errors now, by family, and the build stops on a new instance.
- **SonarQube: its rules, yes; its server, not here.** `SonarAnalyzer.CSharp` is the analyzer the
  SonarQube and SonarCloud scanners run; as a NuGet package it runs in the build with no server, and
  its correctness rules — a regular expression without a timeout, a DateTime without a Kind, an
  exception swallowed, a non-short-circuit boolean, arguments in the parameters' names but another
  order, dead stores, dead code — are fences beside the SDK's. A SonarCloud dashboard needs a token
  and one CI job; the maintainer's decision, recorded in §7, not built blind.
- **StyleCop: measured, no.** 58,459 findings, 25,720 of them the `this.` prefix, 6,596 braces on
  single statements, 4,261 usings inside the namespace — a house style that is not this house's, and
  not one defect class among them. The shape that matters here is kept by the module rules and the
  architecture fence (rounds 59 and 69), which are tests.
- **The banned-API list: yes.** `Environment.TickCount` wraps after 24.9 days and a show machine runs
  for weeks; `Thread.Sleep` stalls the thread it is on; `GC.Collect` is a pause the show does not
  choose; `Console.Write` goes nowhere on a desk; `Task.Wait` on the UI thread is a hang. Each is
  listed with its reason in `BannedSymbols.txt` and the build fails on a use (RS0030).
- **One culture.** The sharpest finding was not one site but a class: 145 places formatted a number or
  compared a verb by the machine's locale. On a Hamburg desk `0.5` becomes `0,5` and `-1` on a Swedish
  one carries a Unicode minus; the wire, the files and the words would differ from a Leeds desk.
  Every site now names its provider, and `CultureGuard` puts the process on the invariant culture from
  Main's first line — belt and braces.

## 2. What the build ran before

`Directory.Build.props` set `TreatWarningsAsErrors` and nothing else about analysis: the SDK's default
level (a dozen rules — platform compatibility, a handful of usage rules) and the xUnit analyzers in the
test projects. No `.editorconfig`, no third-party analyzer, no suppression file.

## 3. The baseline, counted

Every count is a distinct diagnostic (file, line, rule) from one Release build of the solution with
`TreatWarningsAsErrors` off, before any fix.

| Analyzer | Findings | Of which in `src/` | Notes |
|---|---:|---:|---|
| .NET SDK, every rule on (`AnalysisMode=All`) | 7,359 | 2,941 | CA1307 alone 3,032 (Equals/Contains are ordinal already); CA1062 1,067 (nullable is on) |
| SonarAnalyzer.CSharp 10.34 (Sonar way) | ~1,600 | 2,276 lines | S3358 nested conditionals 562 (house style); S8969 redundant `!` 186 |
| Microsoft.VisualStudio.Threading.Analyzers 18.7 | 139 | 30 | VSTHRD002 blocking waits 55 (47 in tests) |
| StyleCop.Analyzers 1.2.0-beta.556 | 58,459 | — | SA1101 `this.` 25,720; SA1503 braces 6,596; SA1200 usings 4,261 |
| BannedApiAnalyzers 5.6 | 0 | 0 | no list yet; the list is this round's |

The SDK rules with a bug class behind them, in the source tree at the baseline:

| Rule | In `src/` | What it catches |
|---|---:|---|
| CA1305 | 92 | a number, date or byte formatted by the machine's locale |
| CA1310 | 53 | StartsWith / EndsWith / IndexOf by the machine's culture — the wire's verbs among them |
| CA2213 | 39 | a disposable field the owner never disposes (paints, listeners, token sources, timers) |
| CA5392 | 55 | a P/Invoke with no DLL search path |
| CA1508 | 29 | dead conditional code (mostly the flow analysis wrong about volatile fields and loops) |
| CA2000 | 27 | dispose before losing scope (mostly handed on; reviewed by hand) |
| CA1815 | 22 | a struct compared without Equals (boxing) |
| CA1806 | 7 | a result ignored — SetThreadExecutionState, RegDeleteKeyValue among them |
| CA1849 | 7 | a blocking call inside an async method |
| CA1001 | 5 | a type owning a disposable it never disposes |
| CA2211 | 4 | a mutable public static field |
| CA2002 | 3 | a lock on an object with weak identity (an array of longs) |
| CA1851 | 3 | an IEnumerable enumerated twice |
| CA2008 | 2 | a task started without a scheduler |
| CA2016 | 2 | a cancellation token not forwarded |
| CA2020 | 2 | a pointer narrowed to an int |
| CA5351 | 1 | MD5 (the PJLink digest — the protocol's own) |

Sonar's, in the source tree:

| Rule | In `src/` | What it catches |
|---|---:|---|
| S6444 | 29 | a regular expression without a timeout, on text from the wire, a deck or a page |
| S1244 | 22 | floating-point equality (reviewed by hand: a sentinel compared exactly is intended) |
| S2386 / S3887 | 17 / 18 | mutable public statics and readonly arrays that are still mutable |
| S108 | 16 | an empty block |
| S1144 / S4487 / S1481 | 12 / 3 / 3 | dead private members, fields, locals |
| S2930 | 8 | an IDisposable made and never disposed |
| S8949 | 24 | a cancellation token not passed on |
| S8969 | 10 | a null-forgiving operator the compiler does not need |
| S6561 | 3 | elapsed time measured on the wall clock |
| S2178 | 2 | a non-short-circuit `|` on booleans |
| S2234 | 2 | arguments in the parameters' names but the other order |
| S2245 | 2 | `Random` where the rule expects a cryptographic source (the playlist shuffle: intended) |
| S4036 | 2 | a command started by name, found on PATH |
| S5443 | 2 | a shared temp directory |
| S6562 | 1 (65 in tests) | a DateTime built without a Kind |

## 4. The tools judged

| Tool | Verdict | Why |
|---|---|---|
| .NET SDK analyzers (`Microsoft.CodeAnalysis.NetAnalyzers`, in the SDK) | **In, as fences** | Already in every build; the rules with a failure mode behind them — culture, disposal, threading, P/Invoke search paths, ignored results — are the ones a show machine needs stopped; the naming and API-shape rules are off by name with the reason. |
| `SonarAnalyzer.CSharp` 10.34 (NuGet) | **In, as fences** | The same rules a SonarQube server runs, inside the compiler, no server: a regular expression without a timeout, a DateTime without a Kind, an exception swallowed, a non-short-circuit boolean, swapped arguments, dead stores and dead code. The style rules (nested conditionals, loops-to-LINQ, `this`) are off by name. |
| `Microsoft.VisualStudio.Threading.Analyzers` 18.7 | **In, as fences** | Blocking waits, unscheduled continuations, async-void, unobserved results — the hang classes of a desk with a UI thread. The JoinableTaskFactory-specific rules and the `Async` suffix rule are off: the desk has no JTF and not that naming. |
| `Microsoft.CodeAnalysis.BannedApiAnalyzers` 5.6 | **In, with a list** | `Environment.TickCount`, `Thread.Sleep`, `GC.Collect`, `Console.Write`, `Task.Wait` — each with its reason in `BannedSymbols.txt`; the desk's code only, the tests may sleep. |
| `StyleCop.Analyzers` 1.2 | **Out** | 58,459 findings and not one defect class: `this.` prefixes, braces, using placement, blank lines, file headers — a house style that is not this house's. The shape that matters here is kept by tests (the module rules, the architecture fence). |
| SonarQube / SonarCloud server | **Recorded, not built** | Needs a token and a CI job; adds history and hotspot review over the same rules the build enforces; cannot be proven from here. §7 has the shape. |
| Custom Roslyn analyzers of Patterns' own | **Not now** | The doctrines worth a rule — a swallowed exception, a result ignored, a blocking wait, an API the desk never calls — are already rules in the sets above; the ones that are not (no allocation in a frame path) are kept by the render budget tests, which measure rather than pattern-match. |

## 5. The fences

Each family is a list of rules in `tools/gen_editorconfig.py` with a reason per rule; a family lands
as errors when its instances are fixed, and the build stops on a new one. Counts are the instances in
`src/` at the baseline; every family below is at zero in the source, with the exceptions carrying a
written reason beside the code.

| Family | Rules | Before | The exceptions that stay, with their reason written |
|---|---|---:|---|
| culture | CA1305, CA1310, S6580 | 145 | none; the tests keep their own culture (a suggestion there) |
| disposal | CA2213, CA1001, S2930, S3881, S1215 | 52 | a frame retired behind the render fence (2), a child handle quit by `End`, the host's own stdout, a timer disposed with a wait handle, a launcher whose `End` decides the standby's fate, the kernel's instance mutex (a shutdown phase), an Avalonia control's sink guard (2), a cache entry's simulation |
| threading | CA2008, VSTHRD105, CA1849, VSTHRD103, VSTHRD002, VSTHRD100, VSTHRD101, VSTHRD110, CA2002, S3998, CA2016, S8949, CA2012 | 54 | eight deliberate blocking waits — the exit's bounded waits on the persistence lane (ADR-008), a task already complete (3), a marshal from a worker to the UI thread, a folder camera read from disk; two fire-and-forget page scripts the VT observer must not wait on |
| security | CA5392, CA5351, CA5399, S4036, S5443, S2245, S4790 | 62 | the PJLink digest (MD5 is the protocol's), the playlist shuffles (a shuffle, not a secret), `/dev/shm` on Linux (the same user's shared memory, by design) |
| correctness | CA1806, CA2211, CA1851, CA1068, CA1513, CA2020, CA1812, S1751, S2123, S2178, S2234, S2486, S108, S1854, S1144, S4487, S1481, S3237, S4275, S6561, S6444, S6562, S8969, S2681, S2699, S2701 | 112 | the three DTOs System.Text.Json fills (CA1812, S1144), the two transposes whose arguments swap by definition (S2234), the layout that follows the page (S4275), the process start as the only mark before Main and the wall-clock backup names (S6561) |
| performance | CA1869, CA1870, CA1875, CA1865, CA1866, CA1826, CA1827, CA1829, CA1860, CA1815, S1155 | 51 | the structs equality is not a question for: the NDI SDK's six native layouts, the calibration's 3×3 in an array, the per-frame render bundle (CA1815) |
| banned | RS0030, RS0031 (`BannedSymbols.txt`) | 17 | five, each with its reason: the frame ring's millisecond poll off Windows (no cross-process event there), the owner store's poll at boot (another process's file), the supervisor's restart back-off and its three stand-down datagrams (its own thread, whose job is to wait), `--verify-runtime`'s console verdict |

Off by name, with the reason in `.editorconfig`: the SDK's naming and API-shape rules (CA1002, CA1003,
CA1030, CA1034, CA1051, CA1054–CA1056, CA1062, CA1307, CA1308, CA1515, CA1707, CA1711, CA1716, CA1720,
CA1724, CA1725, CA1805, CA1814, CA1819, CA2234), CA1031 (the show path catches everything by design;
S2486 keeps the catch honest), CA2007 (the desk's async code continues on the UI context on purpose),
CA5394 (show-visual randomness); Sonar's style rules (S3358, S3267, S1135, S125, S1075, S1313, S101,
S1118, S1199, S1905, S1125, S1066, S907, S3218, S3903, S4136, S1871, S3220, S2292, S1104, S3604, S2325,
S6608, S6610, S4200, S6640, S5332, S2925, S1643, S2376, S3973, S3241, S2479); the threading analyzers'
JoinableTaskFactory rules and the `Async` suffix rule.

Kept as suggestions the IDE shows: CA2000 (dispose before losing scope — mostly handed on), CA1508
(dead conditions — the flow analysis is wrong about volatile fields and loops here), CA2216, CA1063,
CA1861 (constant arrays — 27 cold sites), CA1859 (a concrete type over an interface in a private
signature — by hand where a path is hot), CA1867 (a single-char string compared with anything but
`Ordinal` — the analyzer itself calls the char overload unsafe there: `EndsWith("s",
OrdinalIgnoreCase)` is not `EndsWith('s')`, so the five sites stay as they are and a new one is judged,
not fenced), S1244 (floating-point equality — sentinels compared exactly on purpose), S1172, S927,
S2365, S2696, S2386, S3887, S1450, S127, S6966, S2933, S1994.

## 6. The bugs the fences found

What a fence caught that a reader had not, by family. Each is fixed in the tree; the review names the
unit.

- **Culture (145).** Not one site but a class: every `ToString()`, `Parse` and `StartsWith` without a
  provider or a comparison would have spelt a number or judged a verb by the machine's locale — `0,5`
  on the wire from a Hamburg desk, a Unicode minus from a Swedish one, a verb compared by culture
  rules. The class is closed twice: every site names its provider, and the process runs invariant from
  Main's first line.
- **Disposal (52).** The render pipeline's fourteen paints, its font and its mask image were never
  disposed — a native Skia leak per pipeline, per sink, per re-attach, for the length of a show. The
  control service's, the twin's, the beacon's, mDNS's, OSC's and the device links' token sources,
  listeners and timers were stopped but never disposed; the deck converter's semaphore and the break
  music's lifetime token the same.
- **Threading (54).** Continuations without a scheduler ran on whatever scheduler was current — the UI
  thread's when a tick started them. Two `async void` appliers on the web source turned any exception
  into a process crash; they are tasks now, discarded on purpose, so an exception is logged. A clock
  locked on an array of longs (an object with weak identity). Twenty-six fire-and-forget tasks and
  delays ran on after their service stopped; they carry its token — and the five that must not (the
  twin's writes across a handover, whose token is cancelled and not renewed at once) say so with
  `CancellationToken.None`, which the suites found when the fixer got it wrong.
- **Security (62).** Fifty-five P/Invokes had no DLL search path: a portable exe run from a stick would
  load a `user32.dll` planted beside it. Two commands opened `explorer.exe` through PATH. The shared
  frame ring lived in the machine's temp folder; it lives in the user's own local data on Windows.
- **Correctness (112).** A keep-awake whose result was never read: `SetThreadExecutionState` can refuse,
  and the desk took the attempt for the fact — it logs a refusal now. Two registry deletes the same.
  Twenty-nine regular expressions over text the desk did not write — the assistant's probes over
  anyone's words, the cue sheet's, the feed parser's, the attachments' — ran with no timeout; a pattern
  that backtracks on one hostile line is otherwise a hang for the length of the input; every one
  carries `SafeRegex.Timeout` (250 ms). Two lambdas whose `_` was a parameter, not a discard — `_ =
  StartScreencastAsync()` inside `(_, e) =>` assigns the sender — read as discards and were not. A
  `DateTime` built without its Kind. Two `(int)IntPtr` conversions in the decoder's callbacks that
  would have wrapped silently since .NET 7 are `checked`. Two parse results ignored where the default
  was the intent are said so. Two one-line blocks in tests whose second statement ran once, not per
  iteration — both as intended, and braced now so the next reader sees it. Two UI tests with no
  assertion assert what they walk. Dead weight out: a per-second slow count written and never read,
  a multiview helper, a warp conversion, a pump helper, two brushes, a drag index, a fullness flag, an
  mDNS join count, unused locals; empty catches say why they are empty.
- **Performance (51).** Eighteen single-character string searches use the char overload (the
  `Ordinal` class the analyzer calls safe; the five case-insensitive ones stay, see §5). Fourteen
  `First`/`FirstOrDefault`/`Last` over lists index instead. The four character-set searches use one
  `SearchValues` each (`Separators`). Five `Matches(...).Count` are `Regex.Count`. A
  `JsonSerializerOptions` built per receipt is the shared one. The palette, the pad state and the take
  scope are record structs — value equality by synthesis rather than reflection.
- **Banned (17).** The NDI sender slept two seconds between retries, and a `Stop()` waited that out;
  the arcade loop, the stream renderer and the audio's NDI lane paced with sleeps a stop had to wait
  for. Each waits on a stop event now: the pause ends the moment the stop is asked. The desk polled a
  quitting host every 25 ms for three seconds; the handle waits for the exit. The encoder host's
  heartbeat thread slept its period; it is a `PeriodicTimer`. The five sleeps and prints that are the
  mechanism carry their reason (§5).

## 7. How to run and read them

- The build is the run: `dotnet build -c Release` fails on a fenced rule locally and in CI; the Windows
  lane builds the same tree with the same `.editorconfig`.
- `tools/gen_editorconfig.py` is the plan: a family is a list of rules with a reason each; `python3
  tools/gen_editorconfig.py <family> <family> …` regenerates `.editorconfig` with the named families at
  error and the rest at suggestion. Edit the plan, never the generated file.
- A new instance of a fenced rule is a decision: fix it, or write the reason beside it — a `#pragma
  warning disable RULE // reason` around the line, or a `[SuppressMessage]` with its Justification on
  the member — never a blanket suppression.
- To see everything the analyzers would say, not only the fences: `dotnet build -c Release
  -p:AnalysisMode=All -p:TreatWarningsAsErrors=false` and count by rule with the line in §3's method.
- A SonarCloud dashboard, if the maintainer wants one: a `SONAR_TOKEN` secret, a job that installs
  `dotnet-sonarscanner`, runs `begin` with the project key, builds, and runs `end`; the rules are the
  same ones the build enforces today, so the dashboard adds history and the hotspot review, not new
  findings.

## 8. Honest limits

- A fence at zero says the pattern is absent, not the class of bug. CA2213 finds a field never disposed;
  it does not find a lease never released. The fences are a floor under the reviews, not a ceiling.
- Every exception is a decision written beside the code — thirty-odd across the families (§5) — and a
  reader may disagree with one. The list is the code's; this paper repeats it so the disagreement has a
  place to start.
- The tests carry a lighter fence: a blocking wait, a plain `HttpClient`, a sleep for a worker, a
  redundant `!`, a date literal without a Kind are a test's own business, and `tools/gen_editorconfig.py`
  says which rules and why. A defect class the tests share with the source (an empty catch, a missing
  assertion, a swapped argument pair, a result ignored) is fenced there too, and the tests gained real
  assertions from it.
- An analyzer's suggestion is not a fact either. CA1867 proposed a case-sensitive rewrite of five
  case-insensitive checks; the token fixer cancelled the twin's writes across a handover; a `_` that
  read as a discard was a parameter. Every mechanical pass was re-read, and the suites caught the one
  that got through. The same holds for the next contributor: a fence is a stop to think, and the fix
  the IDE offers is the start of that thought, not the end.
- Not built, by decision, not for want of time: a SonarCloud server and its dashboard (a token and one
  job, §7 — history and hotspot review over the same rules the build already enforces), `dotnet format`
  as a CI gate (formatting is not a defect class here; the shape that matters is kept by the module rules
  and the architecture fence, which are tests), a lint for the Companion module's JavaScript (its own
  test suite runs in CI).
- The baseline counts are one run's, on the Linux lane, with `AnalysisMode=All`; the Windows lane builds
  the same tree with the same `.editorconfig`, so a fence holds there too, but the Windows-only paths were
  counted as compiled on Linux, not as run on Windows.
- One test in the App suite (`NodeHostTests`, the arcade case) failed once under the full suite's load
  during 70.3 and passed alone twice; it is watched, not excused, and the full runs since have been green.
