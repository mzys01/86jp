# Issue 280 magic-box / Seria-luck current evidence

Date: 2026-07-04.
Scope: `MerelyFun/issue-280-magic-box-luck`.

This file records only currently trusted evidence and the next investigation plan. It is intentionally separate from the older notes because some earlier notes contain superseded hypotheses and mixed encoding.

## 2026-07-07 current milestone status

- Current staged milestone commit:
  `082a695 修复赛丽亚幸运单开和十连结果回包`.
- Seria-luck single-open and ten-open currently have the native open sound and
  reward-result popup layout aligned enough for the user-confirmed milestone.
- Strong-chicken gift-box opening on the verified issue-280 worktree did not
  trigger the "buy other job item" popup when tested through the current
  non-Seria fallback path.
- Still open: the visible top-left Seria-luck value UI does not steadily
  reflect the persisted/current value.
- Still open: the precise client-side binding for the top-left numeric/progress
  value is not proven.
- Guardrail: do not treat `0x0312` subtype 1 record 7 as numeric progress. It
  is the active/full/fire state switch only.

## 2026-07-07 Seria-luck value model correction

The earlier service implementation assumed a `0..100` Seria-luck value and
incremented by one per opened box. Treat that as a superseded hypothesis.

New evidence:

- User reported that the value appears to advance faster than one point per
  opened box / ten points per ten-open.
- Public old DNF guide snippets include the rule that opening 8 Seria-luck
  boxes accumulates the luck value and the 9th open can receive double rewards.
- Static client reverse around subtype-4 UI state also uses small stage values
  (`FA3CC0() <= 7` in `B32100`), which fits an 8-step/9th-double model better
  than a visible `0..100` numeric bar.

Current service rule after this correction:

```text
persisted accounts.seria_luck_value range: 0..8
0..7: progress toward full
8: full/fire state; the next Seria-luck open triggers doubled rewards
after a doubled draw: reset to 0, then the same draw advances the next cycle
```

Examples:

```text
single-open from 0: 0 -> 1, no double
single-open from 8: double, then 8 -> 1
ten-open from 0: draw 9 doubles, final value 2
ten-open from 7: draws 2 and 10 double, final value 1
```

This correction may also explain why the top-left UI did not move: the server
was sending item `ExtData0` values such as `69`, while the client-side UI paths
seen so far look like small stage/progress consumers. Live client verification
is still required before calling the UI linkage solved.

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

## 2026-07-05 Seria-luck value run and premium-service evidence

The user kept the same client running, performed several Seria-luck ten-opens,
sold items to free space, hovered the top-left Seria-luck UI, then right-clicked
one more Seria-luck single-open.

Trusted server/runtime state after that run:

```text
accounts.seria_luck_value = 68
Seria-luck item slot 65 = item 0x0028EDA0, count 501
Last single-open: seriaLuck=67->68/100, doubleTriggered=False
```

Successful opens all sent the current sequence:

```text
native open ACK: 0x00D0 or 0x03F3
0x0312 premium-service refresh
0x000E item refresh including slot65 ext0=<current seria_luck_value>
```

Last single-open packet facts:

```text
RECV 0x00D0 body = 04 41 00 FF FF
SEND 0x00D0 doubleFlag = 00
SEND 0x0312 record7 active=0x7FFFFFFF threshold=01
SEND 0x000E slot65 item=0x0028EDA0 count=501 ext0=68
```

Two ten-open attempts failed before mutation because the pet inventory was full
for generated pet rewards:

```text
no empty slot item=0x0000F671 list=Pet
no empty slot item=0x0000F629 list=Pet
```

These failed attempts returned ACK `[00]` and did not send `0x0312` or `0x000E`.
After the user sold items, pet inventory space was available again.

Frida read-only evidence from the same running `DNF.exe`:

```text
Process.arch = ia32
DNF.exe base = 0x400000
```

`0x019D` negative evidence was reconfirmed. Client function `0xB09510` reads
exactly 5 bytes, writes byte0 into a UI active/fire-like field, writes bytes1-4
to a time/target field, and triggers an effect when byte0 is `1`. It is still
not a 0..100 Seria-luck progress-value packet.

`0x0312` subtype `1` was also inspected. Handler `0xCC70A0` reads a `0x4A`
premium-service payload and copies it into the premium-service object through
`0xFA0690`. Record checks use these functions:

```text
F9FBD0(7) returns constant 1
FA0010(index):
  record active dword at object + 0x1d + index*9
  record threshold byte at object + 0x21 + index*9
  inactive active dword => state 0
  active and F9FBD0(index) > threshold => state 3
  active and F9FBD0(index) <= threshold => state 2
```

Therefore record `7` in `0x0312` is a state/fullness switch, not a numeric
progress carrier. With current server data:

```text
active=0x7FFFFFFF threshold=1 => state 2, active/not-full
active=0x7FFFFFFF threshold=0 => state 3, full/fire state
```

The visible top-left "Seria-luck value" progress still needs a different
binding. Current strongest candidate remains item `0x0028EDA0` common-item
extra data (`ExtData0` / client log `ext_data1`), because the server sends it
correctly and it now carries `68`. What remains unproven is whether the top-left
UI consumes that refreshed item field live, consumes it only on UI creation, or
requires another UI-refresh packet after `0x000E`.

Next evidence target:

1. Keep `0x019D` disabled for Seria-luck progress.
2. Keep `0x0312` as active/full state only.
3. Instrument the item/tooltip/UI path for item `0x0028EDA0` and its extra byte,
   or run one controlled user action with hooks installed:
   - focus the DNF client,
   - hover the top-left Seria-luck UI,
   - optionally perform one right-click single-open,
   - capture whether any candidate UI functions or item-extra reads fire.

### 2026-07-05 controlled hook after user action

A 90-second read-only Frida hook was installed on the existing `DNF.exe`
process. The user focused the client and performed one more Seria-luck
single-open.

Captured runtime facts:

```text
F9FA10(index=7) is polled continuously and returns 1.
F9FBD0(index=7) returned 1.
FA0010(index=7) returned 2 before the new 0x0312 handler ran.
0xCC70A0 handled a new 0x0312 success packet.
After 0x0312, premium record7 remained active=0x7FFFFFFF threshold=1.
0x000E item-entry reader received slot65 item=0x0028EDA0 count=500 ext0=69.
0x019D reader 0xB09510 was not called.
```

Interpretation:

- The client definitely receives the updated Seria-luck item extra byte after
  the open (`ext0=69`).
- The client also continuously polls premium-service record `7`, but that poll
  is only an active/full-state check.
- The numeric progress UI still is not explained by `0x0312` or `0x019D`.
- The next reverse target is the client-side consumer of item `0x0028EDA0`
  `ext0/ext_data1`, or a UI refresh path that mirrors this item field into the
  top-left widget.

### 2026-07-05 top-left hover / later single-open evidence

The user then performed several more UI actions on the still-running client:
more Seria-luck ten-opens, item selling, top-left Seria-luck UI hover, and one
right-click single-open with the cursor left on the top-left widget.

Trusted server state after re-reading the live worktree logs and database:

```text
account 10038 seria_luck_value = 69
current character 1002 slot65 item 0x0028EDA0 count=500 instance_value=500
last handled open = 0x00D0 single, seriaLuck=68->69/100
latest 0x000E refresh included slot65 item=0x0028EDA0 count=500 ext0=69
```

The later user selling actions were captured as many `0x0016` sell requests.
No additional server-handled ten-open or single-open appears after the
`68->69` single-open. The only later notable client packets are:

```text
0x043E body includes ASCII "changed 0xb09510"
0x00C2 bodyLen=361, currently unhandled and not yet identified as Seria-luck
```

Important caution: `0x043E changed 0xb09510` points at the earlier Frida hook
site used to inspect `0x019D`. Avoid further Interceptor hooks on `0xB09510`
and prefer read-only memory/disassembly or very narrow one-shot hooks only when
the user explicitly agrees to a controlled action.

Additional client reverse facts now trusted:

```text
0xFE7DF0 compares item id against 0x0028EDA0 / 0x0028F3A0, then calls FA0010(7).
  state 3 => full/fire UI branch
  non-state-3 => active/not-full branch
  no numeric 0..100 progress byte is read here

0x10136E3 / 0x101372A also gate on 0x0028EDA0 / 0x0028F3A0 and FA0010(7).
  this path is full-state animation/effect logic, not numeric progress storage

0xD26210 handles 0x000E item refreshes and calls the item-entry reader.
  entry +0  = slot
  entry +2  = item id
  entry +6  = count
  entry +10 = ext0, so the packet value 69 is definitely consumed

0x10A53B0 stores ExtData0 into the client item object as bit-packed data.
  low 5 bits go through the item object's +8 bitfield
  high bits go through the item object's +0x30 bitfield
  ext0=69 is therefore stored as low=5, high=2, not as one plain byte 0x45
```

Tooltip/resource evidence:

```text
UTF-16 tooltip text address: 0xD98A3E8
pointer table: 0xDE31838
nearby resource id: 0x000116E8 at 0xDE3184C
text: 每开启一个[赛丽亚的幸运]，将累积幸运值；待幸运值达到满格状态后，
      开启[赛丽亚的幸运]可获得双倍道具奖励。
```

Current remaining hypothesis:

- Server growth and item refresh are working for the persisted value.
- `0x0312` is the active/full switch only.
- The top-left numeric/progress display is probably bound either to a decoded
  item-object getter for ExtData0 or to a separate UI refresh packet that has
  not been identified yet.
- Next reverse target is the full ExtData0 getter/caller chain: find code that
  recombines the `0x10A53B0` low/high bitfields (`low | (high << 5)`) and then
  xref that caller to top-left widget code.

### 2026-07-07 continuation sanity check

After the user resumed and said they had performed several additional
ten-opens, sold items, hovered the top-left Seria-luck UI, and right-clicked
one more single-open, the current machine state no longer had a live captured
stack:

```text
7001/10011/7002/10012: no listening process
DNF.exe/DfoServer/PvfProxy: not running
latest issue-280 packet_log.txt write: 2026-07-05 01:00:20.987
latest issue-280 server.log write:     2026-07-05 01:00:21.023
latest issue-280 inventory.db write:   2026-07-05 01:00:21.020
```

The issue-280 worktree log still has no server-handled open after the
`00:21:48` single-open:

```text
last handled open: 0x00D0 single, seriaLuck=68->69/100
latest 0x000E after open: slot65 item=0x0028EDA0 count=500 ext0=69
final DB account 10038 seria_luck_value=69
final DB char1002 slot65 item=0x0028EDA0 count=500 instance_value=500
```

Packets after that open were not open requests:

```text
00:22:25 0x043E body includes ASCII "changed 0xb09510"
00:23:05 0x043E body includes ASCII "changed 0xb09510"
00:33:24 0x00C2 bodyLen=361
01:00:20 0x00C2 bodyLen=364
01:00:20 0x00FA bodyLen=50
01:00:20 0x0117 bodyLen=23
01:00:20 0x0003 / 0x008F disconnect-adjacent packets, then client disconnect
```

Other local worktrees and the main Codeberg checkout had older `server.log`
timestamps and no newer issue-280 open evidence. Therefore the user's
additional open/sell/hover actions cannot currently be attributed to this
worktree's captured service log. Before the next live protocol conclusion,
restart with the canonical harness against this exact worktree and verify the
port owner paths first.

Static packet-name check from the service enum also makes these later packets
low-probability Seria-luck progress candidates:

```text
0x00C2 CMD=FRAME_LAG_STATISTICS / NOTI=NPC_FAVOR
0x00FA CMD=LODING_TIME_REPORT / NOTI=VILLAGE_ATTACKED_REWARD
0x0117 CMD=LAG_STATISTICS / NOTI=SECRET_SHOP_NPC
0x0252 CMD=SECURITY_STATUS / NOTI=RAID_WAITING_MODIFY
0x043E CMD=END
```

`0x00D9` remains ambiguous by enum name (`CMD=OVERFLOW_INFO`,
`NOTI=CLOSE_DISJOINT_STORE`), but observed bodies such as `01 F3 03` appeared
only after failed `0x03F3` opens. Treat it as a failed-open/overflow/error
candidate, not as a proven Seria-luck numeric UI refresh.

### 2026-07-07 offline client reverse: premium block and progress caveats

Static reverse of `DNF.runtime.pe` with base `0x400000` clarified what the
current `0x0312` refresh can and cannot update.

`0x0312` handler `0xCC70A0`:

```text
success path reads u16 subtype
subtype 1:
  reads 0x4A bytes into a local buffer
  calls 0xFA0690 with global 0x3079BA4
subtype 4:
  reads 0x4A bytes and calls 0xFA3B10 with global 0x3079C8C
```

`0xFA0690` copies exactly the 74-byte payload:

```text
0xFA069B lea edi, [ebx + 0x17]
0xFA069E mov ecx, 0x12
0xFA06A3 rep movsd   ; 72 bytes
0xFA06A5 movsw       ; +2 bytes = 74 total
```

Therefore the service-side `0x0312` body shape is still:

```text
byte success = 1
u16 subtype  = 1
74-byte premium payload copied to client global+0x17..+0x60
```

Record `7` lives inside that copied range:

```text
record7 active dword    = global+0x1D + 7*9
record7 threshold byte  = global+0x21 + 7*9
FA0010(7) compares F9FBD0(7) with that threshold
F9FBD0(7) returns constant 1
threshold 1 => active/not-full state 2
threshold 0 => full/fire state 3
```

This proves `0x0312` can drive the top-left full/fire state, but it cannot
carry an arbitrary numeric 0..100 Seria-luck progress value.

The previously suspicious `global+0x6A` field is outside the copied `0x0312`
range. It is read by `0xF9FFE0` and used only in full-state animation timing
paths seen so far:

```text
0x101376B calls F9FFE0 when FA0010(7)==3 and global+0x0C==1
0x1E85E1B calls F9FFE0 under the same full-state/global+0x0C gate
```

No verified writer to `global+0x6A` was found in the premium-service handler.
Byte-pattern hits outside this region were either unrelated object constructors
or non-code/data false positives. Do not treat `global+0x6A` as a server-owned
progress byte without new runtime evidence.

The `0x10136E3` / `0x1013A80` path initializes and ticks a UI animation object
with a max of `100`, but the direct caller passes fixed arguments `0, 100`.
Current evidence says this is a local UI animation/timer, not the persisted
Seria-luck value.

Item `ExtData0` remains risky as a 0..100 carrier. The packet reader does pass
the full byte to `0x10A53B0`, and that setter splits the value into:

```text
low 5 bits  -> item object +8 bitfield
high bits   -> item object +0x30 bitfield
```

However the common direct getter `0x10A5C10` reads only the low-bitfield value.
The xrefs found for `0x10A5C10` are generic item attribute checks and not yet
confirmed as the top-left Seria-luck progress UI. A static scan around the item
object methods did not find a clear `low | (high << 5)` getter.

Next evidence target before another service patch:

1. In a controlled live run, hook only read-only return values for:
   - `0x10A5C10` and its caller return addresses when item id is `0x0028EDA0`,
   - `0xFA0010(7)`,
   - `0xF9FFE0`,
   - the `0x000E` item reader at `0xD264EA`.
2. Ask the user for one specific action at a time: hover top-left UI, then open
   exactly one Seria-luck box from consumables slot 0.
3. Do not change `0x019D`; current evidence still says it is not Seria-luck
   numeric progress.

### 2026-07-07 offline client reverse: item-id branches and ExtData getter limit

Additional static reverse refined the remaining top-left UI candidates.

`0x00D9` is no longer a good Seria-luck progress candidate:

```text
0x00D9 registration: 0xD3EFDD -> handler stub 0xD33070 -> jmp 0xAA1FC0
0xAA1FC0 reads one u32 and resolves/closes an item-related UI object
No 0..100 progress byte/word layout was found in this handler.
```

Hardcoded item id `0x0028EDA0` branches are split by responsibility:

```text
0xFE7D50 cluster:
  top-left Seria-luck widget visibility/state
  checks item ids 0x0028EDA0 / 0x0028F3A0
  calls FA0010(7) for active/full/fire state
  no numeric ExtData/progress getter seen

0x1013200..0x10137D0 cluster:
  Seria-luck UI animation setup
  initializes a progress/animation component with fixed 0..100 arguments
  full-state animation duration is gated by FA0010(7), global+0x0C, F9FFE0()
  no persisted current-value read seen

0x1159804 cluster:
  right-click item-use path for 0x0028EDA0
  count < 10 sends 0x00D0 single-open request
  count >= 10 opens the native ten-open confirm window

0x114B300..0x114B670 cluster:
  "buy other job item" whitelist/warning path
  0x0028EDA0 and 0x0028F3A0 are whitelisted
  non-whitelisted ids fall through to the warning popup at 0x114B670
```

The `ExtData0` setter/getter situation is still the main blocker:

```text
0xD26210 / 0xD264EA item refresh calls 0x10A53B0 with raw entry+10 ExtData0.
0x10A53B0 stores:
  low 5 bits      -> item object +8 bitfield
  value >> 5 bits -> item object +0x30 bitfield

Known direct getter 0x10A5C10 reads only item object +8.
Direct xrefs to 0x10A5C10:
  0x6496F4, 0xA727B3, 0xABEEF2, 0xB255C4, 0xB4CDB5, 0x105C0BB

A static scan of the surrounding item-object methods and direct bitfield-reader
calls did not find a clear low | (high << 5) recombine getter.
Only three direct shl/sal *,5 instructions were found in the scanned code
range, and none matched the item ExtData high-bit read path.
```

Interpretation:

- The server currently persists and sends the value through DB and `0x000E`
  item ext0, but client static evidence does not yet prove that the top-left UI
  can consume the full 0..100 value from that field.
- If the UI uses `0x10A5C10`, values above 31 would be seen as only the low
  five bits, e.g. `69 -> 5`; this would explain a non-steady or invisible
  progress display, but it is still a hypothesis until a live hook confirms the
  caller.
- The next live run, if needed, should be narrow and controlled: attach
  read-only hooks for `0x10A5C10`, `0xD264EA`, `0xFE7D50`,
  `0x1013A80`, `0xFA0010(7)`, and `0xF9FFE0`, then ask the user to hover the
  top-left UI and open exactly one Seria-luck box.

### 2026-07-07 offline client reverse: `0x0312` subtype 4 is related

Read-only parallel reverse clarified that `0x0312 subtype=4` is not a random
unrelated premium block. It is related to the Seria-luck / magic-box UI chain,
but it still does not look like a simple `currentLuck` integer packet.

Relevant client flow:

```text
0xCC7109..0xCC7118:
  0x0312 handler reads u16 subtype

0xCC7118..0xCC713C:
  subtype 4 clears a local 0x4A buffer, reads 0x4A bytes, calls FA3B10

0xFA3B10:
  copies 74 bytes into [0x3079C8C]+4, covering object +0x04..+0x4D

0xCC7141..0xCC7168:
  if object byte0 is 1 and FA43E0() says the UI condition is ready,
  calls FA44C0(), then clears object byte0

0xFA4140..0xFA43C4:
  when conditions are met, plays a UI/effect path and actively builds/sends
  a client request packet 0x0312 subtype 4 with a 0x4A-byte buffer

0xB32100..0xB321AF:
  consumes [0x3079C8C] UI state; FA3CD0() timing divided by 1000 is written
  to UI +0x204, and FA3CC0() stage/limit is written to UI +0x208
```

Fields definitely read from the 74-byte subtype 4 payload:

```text
payload +0x1C -> object +0x20
payload +0x2E -> object +0x32
payload +0x37 -> object +0x3B
```

Fields `object+0x50/+0x51/+0x56` are also read by subtype-4 object functions,
but they are outside the `FA3B10` copy range and should not be treated as
server-owned payload fields without live evidence.

Interpretation:

- The server currently initializes and refreshes only `0x0312 subtype=1`.
- Subtype 1 controls record-7 active/full/fire state.
- Subtype 4 likely carries an additional 74-byte stage/threshold/timer state
  table used by the Seria-luck UI chain.
- If the live client sends `0x0312 subtype=4` and the server replies with
  subtype 1, that is a protocol gap to fix.
- Do not encode the persisted luck value as a raw int in subtype 4 until a live
  hook or packet trace identifies the exact byte/field semantics.

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
