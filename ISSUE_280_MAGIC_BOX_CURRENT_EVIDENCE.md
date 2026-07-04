# Issue 280 magic-box / Seria-luck current evidence

Date: 2026-07-04.
Scope: `MerelyFun/issue-280-magic-box-luck`.

This file records only currently trusted evidence and the next investigation plan. It is intentionally separate from the older notes because some earlier notes contain superseded hypotheses and mixed encoding.

## User-visible bugs still open

- Opening strong-chicken gift box still triggers the client popup "购买其他职业的物品".
- Seria-luck single-open currently has the correct special open window, sound, and reward-result UI, but the visible top-left Seria-luck value UI does not steadily increase.
- Seria-luck ten-open still does not show the expected special ten-open UI/sound/result layout.
- The ten-open UI should be a large native result window: left side primary ten rewards, right side doubled rewards.

## Protected facts

- Do not regress the `0x00D0` Seria-luck single-open path. The user confirmed this path has the correct window, sound, and special reward-result UI.
- Current `0x00D0` body baseline: `success, clientType, doubleFlag, sourceSlot, materialSlot, 31-byte reward rows`.
- Current live `0x03F3` Seria-luck batch evidence says reward rows must be 31 bytes. The earlier Frida note that labeled them 27 bytes is superseded by the 18:46 live crash/log evidence below.
- Do not send the dangerous strong-chicken native `0x03F3 bodyLen=6215/displayRows=200` path again.
- Do not treat `0x019D` as Seria-luck progress. Frida evidence shows it is a 5-byte active/time-style UI packet, not a 0..100 progress value.

## Confirmed client handler evidence

### `0x03F3` batch / expanded magic-box

Registration:

```text
0xCD09AA  push 0
0xCD09AC  push 0xCCEBC0
0xCD09B1  push 0x3F3
0xCD09B8  call 0x1189FC0
```

Handler: `0xCCEBC0`.

Read order after the dispatcher success argument:

```text
u8  clientType
u8  doubleFlag
u16 openCount
u16 sourceSlot
u16 materialSlot
list #1 through 0xCCCEB0
u16 separator/unknown
list #2 through 0xCCCEB0
```

Important branch:

```text
0xCCED57 cmp byte ptr [clientType], 4
0xCCED5E je 0xCCEE53
```

Meaning: `clientType == 4` skips material-slot validation and matches Seria-luck batch open.

The handler reads `u16 count` reward lists. A previous Frida pass inferred 27-byte rows, but the 18:46 live Seria-luck ten-open disproved that: 27-byte rows were accepted at packet level, then the client misread reward item ids and crashed. Current service rule is 31-byte reward rows for both native `0x00D0` and native `0x03F3`.

### `0x00D0` single Seria-luck

Handler: `0xCCD1C0`.

It checks the dispatcher success argument, then reads a client type/category byte and branches into the Seria-luck special window path. This path is the live-confirmed good baseline, so avoid changing it while investigating batch/strong-chicken.

### `0x00A0` obtained-items popup

Handler: `0xCEB4A0`.

Known body contract:

```text
success
u16 slot
u32 context1
u32 context2
u16 count
repeat itemId:u32 + count:u32
```

Current `SelectablePackageAckBuilder` matches this layout. For non-Seria `0x03F3`, current service code deliberately falls back to this old aggregated path instead of native `0x03F3`.

### `0x019D` negative evidence

Client reader around `DNF.exe+0xB09510` reads exactly 5 bytes:

```text
byte[0]    -> UI active/fire-like flag at object +0x1E0
bytes[1-4] -> time-like value at object +0x1E4
```

Other UI code subtracts a current-time-like value from `+0x1E4`, divides it, and formats a remaining-time string. This explains the user's "fire flashes, then disappears" report when the server sent `seria_luck_value` through `0x019D`.

Current service rule: all runtime/init `0x019D` sends for this feature are disabled.

### `0x000E` item update

Client handler `0xD26210` reads item-space, count, then `0x54` bytes per entry. The client reads the byte at item raw offset 10, which matches the server's `ExtData0` placement. This proves the `ExtData0` packet placement is syntactically valid, but does not prove the visible top-left Seria-luck progress UI consumes it.

## Strong-chicken popup evidence target

The suspected client popup branch is around `0x114B670`, inside a function that checks item ids and builds the "购买其他职业的物品" confirmation.

Before asking the user to open strong chicken again, install this read-only Frida hook:

```javascript
if (!globalThis.__issue280Hooks) globalThis.__issue280Hooks = {};
if (!globalThis.__issue280Hooks.otherJobPopup) {
  Interceptor.attach(ptr('0x114B670'), {
    onEnter(args) {
      const c = this.context;
      console.log('[issue280 other-job-popup] item=0x' + (c.esi >>> 0).toString(16) +
        ' edi=0x' + (c.edi >>> 0).toString(16) +
        ' ebx=0x' + (c.ebx >>> 0).toString(16) +
        ' ret=' + this.returnAddress);
    }
  });
  globalThis.__issue280Hooks.otherJobPopup = true;
}
```

Expected evidence: item id and return address for the popup. Do not continue changing reward ACKs until this is known.

## Next plan

1. Install narrow Frida hooks for:
   - `0x114B670` other-job popup branch.
   - `0xCCEBC0` `0x03F3` handler entry if ten-open is tested.
   - `0xCCD1C0` `0x00D0` handler entry only as a baseline if needed.
2. Ask the user for exactly one action at a time:
   - First: one strong-chicken open, only after hook is installed.
   - Second: one Seria-luck ten-open, only after hook is installed.
3. Use hook output plus packet logs to decide whether the strong-chicken popup is:
   - before server ACK,
   - caused by `0x00A0`,
   - caused by `0x000E`,
   - or caused by a client-local item/shop metadata branch.
4. For Seria-luck progress UI, continue reversing item `2682272` extra-data reads and any `0x00D9` request/response relation. Do not revive `0x019D` as progress.
5. Only after evidence, update service code and add focused self-tests around golden byte layouts.

## 2026-07-04 live-environment correction

After the user reported one strong-chicken open with no "buy other job item" popup, process/port inspection showed the live stack was not this worktree:

```text
7001/10011 -> PvfProxy pid 7396
7002/10012 -> DfoServer pid 11340
DfoServer path -> C:\Users\...\magic-box-contract-auto-use\Server\DfoServer\bin\Debug\DfoServer.exe
PvfProxy path  -> C:\Users\...\magic-box-contract-auto-use\Tool\...
```

Therefore that action is not valid evidence for `E:\DNF0828\86jp-worktrees\issue-280-magic-box-luck`.

Important rule before the next live test:

- Restart through `E:\DNF0828\86jp\.codex-harness\scripts\Start-86jpCapturedStack.ps1` from this worktree, or pass `-ServerRepo E:\DNF0828\86jp-worktrees\issue-280-magic-box-luck`.
- The script should kill only owners of `7001/10011/7002/10012`, preserve `DNF.exe`, and bind new packet logs to this worktree.
- Re-check port owners and process paths before asking the user to open strong chicken or Seria-luck ten-open again.

## 2026-07-04 trusted strong-chicken retest on the correct worktree

After restarting through the canonical harness against:

```text
E:\DNF0828\86jp-worktrees\issue-280-magic-box-luck
```

the user opened one strong-chicken gift-box flow and reported no
"buy other job item" popup.

This is now trusted evidence because the live listeners were verified first:

```text
7001/10011 -> PvfProxy from the issue-280 worktree
7002/10012 -> DfoServer.dll from the issue-280 worktree
DNF.exe    -> preserved running client from E:\DNF0828\86jp\DXF
```

Packet/server evidence:

```text
18:41:45 CERA_SHOP_BUY item 102930 auto-opened source 0x0098B3B5
          -> 0x0098B347 x100 and 0x0098B348 x100

18:41:48 RECV  cmd=0x01 type=0x03F3
          body = 00 54 00 48 B3 98 00 7A 00 47 B3 98 00 64 00

18:41:48 SEND  cmd=0x01 type=0x00A0
          body = 01 54 00 00 00 00 00 00 00 00 00 02 00
                 4E AC 27 00 C8 00 00 00
                 A0 ED 28 00 64 00 00 00

18:41:48 SEND  cmd=0x00 type=0x000E
          source slot 84 consumed/depleted
          reward slots refreshed:
            slot 66 item 0x0027AC4E count 1000
            slot 67 item 0x0028EDA0 count 486 ext0=100
```

Current conclusion:

- The strong-chicken "other job item" popup is not triggered by this current
  non-Seria fallback path.
- For non-Seria `0x03F3`, keep using the legacy `0x00A0` obtained-items
  popup path instead of native magic-box `0x03F3` result bodies.
- Do not regress this path while continuing Seria-luck batch/progress work.

Next single-action test should target Seria-luck ten-open on this same running
worktree, then compare the fresh `0x03F3` native ACK bytes against the expected
large ten-result UI.

## 2026-07-04 trusted Seria-luck ten-open crash evidence

The user opened one Seria-luck ten-open on the verified issue-280 worktree and
reported a client crash.

Request:

```text
18:46:47 RECV cmd=0x01 type=0x03F3
body = 04 43 00 A0 ED 28 00 FF FF FF FF FF FF 0A 00
```

Server ACK:

```text
18:46:47 SEND cmd=0x01 type=0x03F3 bodyLen=906
head = 01 04 01 0A 00 43 00 FF FF 1E 00 FF FF 2A 00
```

Server interpretation:

```text
clientType=0x04
displayRows=30
doubleRows=3
seriaLuck=100->10/100
doubleTriggered=True
```

Client log immediately after receiving packet 1011 / `0x03F3`:

```text
CMD RECV 1011 (Size : 906) Result : Ok
createItem fail item index : -65536
createItem fail item index : 808464432
```

Windows Error Reporting:

```text
DNF.exe exception 0xc0000409 at DNF.exe+0x008CD1B7
```

Conclusion:

- The `0x03F3` header is now accepted well enough to enter the result handler.
- The crash is in the native reward list decoding path.
- The 27-byte row inference is wrong for the live client; it produces shifted
  reward item ids, matching the older screenshot where a reward count was shown
  as the next reward item id.
- `MagicBoxOpenAckBuilder.BuildBatch` must use 31-byte reward rows, while
  keeping the current header order:
  `success, clientType, doubleFlag, openCount, sourceSlot, materialSlot`.

## 2026-07-04 19:01 stale-binary retest evidence

The user opened Seria-luck ten-open again and the client hung/froze. This run
did not exercise the 31-byte-row source fix.

Evidence:

```text
listening DfoServer before restart: PID 11444, started 18:40:20
normal Debug DfoServer.dll before restart: LastWriteTime 17:09:59
MagicBoxOpenAckBuilder.cs source: LastWriteTime 18:49:17
isolated fixed build: LastWriteTime 18:50:02
```

The 19:01 packet still had the old 27-byte-row length:

```text
SEND 0x03F3 bodyLen=825
head = 01 04 00 0A 00 41 00 FF FF 1E 00 FF FF 24 00
client GameLog: CMD RECV 1011 (Size : 825) Result : Ok
client GameLog: createItem fail item index : 808464432
```

`825 = 9-byte header + 2 + 30 * 27 + 2 + 2`, so the running server was still
the pre-fix Debug binary. The client symptom is the same stale 27-byte stride
failure, not a new result from the 31-byte-row source change.

Correction performed immediately after this evidence:

```text
powershell -NoProfile -ExecutionPolicy Bypass -File E:\DNF0828\86jp\.codex-harness\scripts\Start-86jpCapturedStack.ps1 -ServerRepo E:\DNF0828\86jp-worktrees\issue-280-magic-box-luck -Build
```

Post-restart verification:

```text
DfoServer PID 10140 listening on 7002/10012, started 19:02:58
PvfProxy PID 29400 listening on 7001/10011, started 19:03:00
normal Debug DfoServer.dll LastWriteTime 19:02:57
dotnet Server\DfoServer\bin\Debug\DfoServer.dll --selftest-selectable-package
=> 210 PASS, 0 FAIL
```

Next live test must use the post-19:02:57 Debug binary. If the client is still
showing a disconnected/frozen old session, reconnect/return to character select
without killing `DNF.exe`; the service and proxy have been refreshed.

## 2026-07-04 18:57 retest was still the stale 27-byte Debug binary

The user opened Seria-luck ten-open again and reported another freeze/crash.
This run must not be treated as evidence against the 31-byte row fix because the
running process and on-disk Debug DLL were stale.

Process/build timestamps:

```text
DfoServer dotnet PID 11444 start: 2026-07-04 18:40:20
PvfProxy PID 21808 start:      2026-07-04 18:40:22
bin/Debug/DfoServer.dll time:  2026-07-04 17:09:59
MagicBoxOpenAckBuilder.cs:     2026-07-04 18:49:17
```

Latest stale run packet evidence:

```text
18:57:19 RECV cmd=0x01 type=0x03F3 body [04 43 00 A0 ED 28 00 FF FF FF FF FF FF 0A 00]
18:57:19 SEND cmd=0x01 type=0x03F3 bodyLen=825
head = 01 04 00 0A 00 43 00 FF FF 1E 00 FF FF 2A 00
```

`bodyLen=825` is exactly the old 27-byte-row shape for 30 primary rows and no
double rows:

```text
9-byte header + 2-byte count + 30 * 27-byte rows + 2-byte separator + 2-byte second-list count = 825
```

Client log and Windows crash event matched the earlier row-stride failure:

```text
CMD RECV 1011 (Size : 825) Result : Ok
createItem fail item index : 808464432
DNF.exe exception 0xc0000409 at DNF.exe+0x008CD1B7
```

Next launch requirement:

- Do not retest ten-open against the current running stack.
- The next server/client launch for this worktree must use
  `Start-86jpCapturedStack.ps1 -ServerRepo E:\DNF0828\86jp-worktrees\issue-280-magic-box-luck -Build`
  so the normal `bin\Debug` output is rebuilt after the script kills only the
  service/proxy port owners.
- The expected 31-byte-row no-double ten-open body length is `945`, not `825`.
