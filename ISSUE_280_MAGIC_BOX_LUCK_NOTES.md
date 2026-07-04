# Issue 280 Magic Box / Seria Luck Notes

Date: 2026-07-04

This is a temporary project note in the service worktree. It records live-client evidence and current hypotheses for the issue-280 magic-box/Seria-luck work. It is not intended as polished product documentation.

## Active Workspace

- Service worktree: `E:\DNF0828\86jp-worktrees\issue-280-magic-box-luck`
- Branch: `MerelyFun/issue-280-magic-box-luck`
- Canonical launch entrypoint: `E:\DNF0828\86jp\.codex-harness\scripts\Start-86jpCapturedStack.ps1`
- Latest capture directory used for this note: `Server\DfoServer\bin\Debug\capture_logs`

## Confirmed Breakthrough

Latest confirmed root cause for the mismatched obtained-items popup:

- User screenshot showed the actual grant was `复活币 x1 + 她的心意 x70`, while the native reward popup showed `复活币 + 海之勇者礼包 2 (格斗家)`.
- `GM工具\item_index.cache.json` maps `海之勇者礼包 2 (格斗家)` to item template id `70`.
- Therefore the client was reading the second reward row's count value `70` as the next row's item id.
- The server's native magic-box reward row was 27 bytes, but the client handler advances by 31 bytes per row.
- `MagicBoxOpenAckBuilder.WriteRewardRow` now appends a 4-byte reserved tail so each native `0x00D0` / `0x03F3` reward row is 31 bytes.
- Focused self-test now checks item/count offsets for:
  - `0x03F3` strong chicken / normal magic-box batch rows,
  - `0x03F3` Seria-luck ten-open rows,
  - `0x00D0` Seria-luck single-open rows,
  - `0x00D0` full-value double Seria-luck rows.

This is stronger evidence than the earlier "single should maybe use DisplayRewards" hypothesis. The screenshot proves a row-stride decode error: item id `70` was not chosen by PVF; it was the previous row's count.

Single-open Seria luck now reaches the correct client-side random-box presentation path:

- The open result window is the correct special reward window.
- The magic-box special sound effect is correct.
- This started working after the server stopped hardcoding magic-box ACK client type to `00` and instead echoed the request's raw first byte.

Packet evidence from `capture_logs\packet_log.txt`:

```text
RECV 0x00D0 body [04 43 00 FF FF]
SEND 0x00D0 body [01 04 01 43 00 FF FF ...]
```

Interpretation:

- Request byte `04` is not a normal server `InventoryListType.Main` byte. It is the 86JP client's magic-box UI/client type for the main inventory random box.
- The ACK must carry that same client type byte. Returning `00` made the client use a generic/wrong branch.
- Current single ACK prefix is:

```text
01              success
04              echoed client type from request body[0]
01              single-open marker/count
43 00           source slot 67
FF FF           no hammer/material slot
...
```

This aligns with the user's live observation: correct window and correct sound.

## 2026-07-04 Client-Confirmed Single-Open Contract

The user confirmed the single-open Seria-luck window and special sound are now correct. Preserve this path unless new client evidence directly disproves it.

Current native `0x00D0` single-open ACK contract:

```text
01                 success
<clientType>       echo request body[0], e.g. 04 for Seria luck
<doubleFlag>       01 only when Seria-luck value was full before this open
<sourceSlot:i16>
<materialSlot:i16> FF FF when no hammer/material is consumed
<rewardCount:u16>
<31-byte reward row>...
```

Important invariants:

- Do not put source item id or material item id into the `0x00D0` ACK header. The client reads slots at offsets 3 and 5.
- Do not use the generic `0x00A0` obtained-items popup for `0x00D0`.
- Single-open display rows must come from actual aggregated granted rewards (`BoosterUseResult.Rewards`), not from the batch display/double split. This is what made the single popup match the real received items.
- Each reward row is 31 bytes. The earlier 27-byte row caused the next row's item id to be decoded from the previous row's count.
- Keep the `0x000E` item refresh after the native ACK so the inventory count and Seria-luck item `ext_data1` update.

## Current Remaining Single-Open Problem

User observation after the breakthrough:

- Single-open `赛丽亚的幸运` now opens the correct client window and plays the correct special sound.
- After a non-full single-open, the top-left Seria-luck UI briefly turns into the fire/full state and then disappears.
- Correct behavior: every open increases `赛丽亚的幸运值` by one; only the full value should show the fire/full UI, and the next open consumes the full value for double rewards.

Relevant current server logic in `Server/DfoServer/Game/Inventory/InventoryPackageStore.cs`:

```text
if seria_luck_value >= 100:
    duplicate current rewards into DoubleRewards
    duplicate current rewards into rewardsToGrant
    seria_luck_value = 0

seria_luck_value = min(100, seria_luck_value + 1)
```

Potential issue to verify next:

- The account may already be at `100`, causing the first observed open to double and then reset to `1`.
- Or the client expects a separate Seria-luck value refresh packet/state update that the server still does not send, so the UI may not show value changes even though the DB changes.
- Do not conflate this with `LuckyStar` or rental lucky-star state. The feature name here is specifically `赛丽亚的幸运值`.

Update after client item-list evidence:

- `DXF\GameLog.log` logged the Seria-luck item as `赛丽亚的幸运(2682272) : SlotIndex(3), Data(170), ext_data1(0), ...`.
- `Data(170)` matches the stack count, so the client-visible progress candidate is the common item extra field logged as `ext_data1`.
- Server common item packets write `CommonInventoryItem.ExtData0` at that extra-field position.
- The server now overlays account `seria_luck_value` onto item `2682272`'s `ExtData0` for both initial item-list snapshots and single-item refresh loads.
- This is intentionally not `LuckyStar` and not rental/lucky-star state.
- Focused self-test now asserts the client-visible field:
  - starts at `0`,
  - becomes `10` after ten-open,
  - becomes `11` after one more single open,
  - becomes `1` after a full-value double-trigger open resets/restarts the value.

Remaining live check:

- Need user/live client confirmation that the visible `赛丽亚的幸运值` bar/text now changes after open.
- If it still does not move, the next candidate is a separate client state packet, but the item-list `ext_data1` mapping is now the strongest known evidence.

Update after the live fire-flash observation:

- Runtime `0x019D` after every Seria-luck open is negative evidence. The packet sequence was native ACK, `0x000E` item refresh, then `0x019D`, and the UI briefly entered the fire/full state even for non-full values.
- Current fix direction: keep DB progression and the `0x000E` Seria-luck item refresh, but do not send runtime `0x019D` for non-full values. Runtime `0x019D` is now reserved for the newly-full state (`valueAfter >= max` and no double reset).
- This preserves the value increase path through item `2682272`'s `ext_data1`/server `ExtData0` while avoiding the non-full fire animation.

## Batch / Ten-Open Status

Ten-open still does not behave correctly in the live client.

Latest packet evidence:

```text
RECV 0x03F3 body [04 43 00 A0 ED 28 00 FF FF FF FF FF FF 0A 00]
SEND 0x03F3 body [01 04 43 00 FF FF 0A 00 ...]
```

Current interpretation:

- `04` is again the client magic-box type and should be echoed.
- `43 00` is source slot 67.
- `A0 ED 28 00` is item template `0x0028EDA0` / decimal `2682272`, the Seria luck item.
- `FF FF` material slot means no hammer/material slot for this item.
- `0A 00` is requested count 10.

Before the latest ACK fix, the server was effectively returning the batch prefix in an order that let the client interpret the count as a slot, producing client-side random-box/hammer slot validation errors. The current prefix avoids that specific mistake:

```text
01              success
04              echoed client type
43 00           source slot
FF FF           material slot
0A 00           consumed source count
...
```

However, ten-open is still not client-correct. The next likely area is the layout after the first result list:

```text
current server batch body:
01 04 <sourceSlot> <materialSlot> <count>
<primary reward list>
00 00
<double reward list>
```

Open question:

- The client may expect an extra field between the primary rewards and double rewards, or may interpret the second list differently for Seria luck.
- Need client handler evidence for `0x03F3` before changing this again.

Correction after the 2026-07-04 13:20-13:22 live run:

- The attempted unified `openedCount, sourceSlot, materialSlot` header is wrong.
- Client `GameLog.log` reported the random-box/hammer slot critical error immediately after receiving the count-first `0x03F3` ACK.
- For Seria ten-open, the failing ACK head was:

```text
01 04 0A 00 6B 00 FF FF ...
```

The client then read `0A 00` as the random-box slot and `6B 00` as the hammer/material slot, proving the batch ACK header must put slots before count.

- Strong chicken also failed after the count-first ACK head:

```text
01 00 64 00 41 00 AD 00 C8 00 ...
```

If the client reads slots first, that head means source slot `100` and hammer slot `65`, explaining the same slot-validation failure.

Current corrected batch prefix after the latest slot-alignment fix:

```text
01 <clientType> <doubleFlag:u8> <sourceSlot:i16> <materialSlot:i16> <openedCount:u16> <rewardListCount:u16> ...
```

Expected Seria ten-open example:

```text
01 04 00 6B 00 FF FF 0A 00 <rewardListCount> ...
```

Expected strong-chicken example:

```text
01 00 00 41 00 AD 00 64 00 <rewardListCount> ...
```

Important correction:

- The batch request includes source/material item ids, but the batch ACK must not echo those item ids before the slots are read.
- The previous native batch ACK wrote `sourceSlot, sourceItemId, materialSlot, materialItemId, count`. Live `GameLog.log` then reported the client-side random-box/hammer slot critical error immediately after receiving `0x03F3`.
- That failure is explained by offset drift: the client reads the material slot right after the source slot, so the ACK's source item id bytes were being interpreted as the hammer/material slot.
- Strong chicken reward logic was not changed by this correction; only the native `0x03F3` ACK header was realigned with the slot offsets already proven by single-open.

This source/material/count header is now covered by the focused self-test, but still needs a fresh live run after rebuilding/restarting the worktree server.

Additional diagnostic logging was added for the next live run:

- ACK body length and first bytes for `0x00D0` / `0x03F3`.
- `clientType`, primary display rows, double rows.
- Seria luck value `before -> after / max` and `doubleTriggered`.

This should directly answer whether the server value is increasing and whether a single open really sends double rows every time.

2026-07-04 validation after this correction:

- `dotnet run --project Server\DfoServer\DfoServer.csproj -- --selftest-selectable-package`: 216 PASS / 0 FAIL.
- `dotnet build Server\DfoServer.sln`: success, 0 warnings / 0 errors.
- Canonical harness script restarted the worktree stack afterward:
  - `PvfProxy` on `7001/10011`
  - `DfoServer` on `7002/10012`
- Existing `DNF.exe` was not killed. The client initially showed a network-disconnected dialog because the old service process had been stopped to unlock the build output. Later logs showed fresh login/character traffic, but no new magic-box open packet yet.
- Next live evidence needed: after a fresh single-open, confirm no runtime `0x019D` non-full refresh is sent; after a fresh ten-open/strong-chicken open, confirm the `0x03F3` ACK head starts with `01 <clientType> <doubleFlag> <sourceSlot> <materialSlot> <count>` and the client no longer logs the random-box/hammer slot critical error.

## PVF / Client Item Knowledge

Temporary PVF inspection found item `2682272`:

```text
path: ect/chn_random/chn_blessed_box.stk
name: 赛丽亚的幸运
stackable type: [random upgradable legacy] 1
randomBoxRewards: 766 entries
removalItems:
  item 201 count 0
  item 2682272 count 1
```

Other `[random upgradable legacy]` item names include `坚强鸡礼盒`, `赛丽亚的幸运`, and `赛丽亚的祝福`.

Important caution:

- `[RANDOMBOX] [int data]` entries appear to have a three-int header before reward rows. Current parser skips the first three ints. This may be related to reward-row semantics, but do not change it globally without client/protocol proof.
- Static IDA headless probing found immediate ref `0x00D0` near `sub_59F18D6`, but the function is heavily obfuscated/jump-flattened and did not provide clean packet-layout proof. No useful `0x03F3` immediate ref was found in that pass.

## Important Client Parsing Knowledge

Observed command bodies:

```text
0x00D0 single Seria luck open:
04 <sourceSlot:i16> <materialSlot:i16>

0x03F3 batch Seria luck open:
04 <sourceSlot:i16> <sourceItemId:i32> <materialSlot:i16> <materialItemId:i32> <count:u16>
```

Known request example:

```text
04 43 00 A0 ED 28 00 FF FF FF FF FF FF 0A 00
```

Field decode:

- `04`: client magic-box/main-inventory type
- `43 00`: source slot 67
- `A0 ED 28 00`: source item `2682272`
- `FF FF`: no material slot
- `FF FF FF FF`: no material item
- `0A 00`: requested count 10

Known successful-looking single ACK prefix:

```text
01 04 01 43 00 FF FF
```

Important correction:

- The third byte after success/client type was initially guessed as a single-open count and was hardcoded to `01`.
- Live UI behavior strongly suggests this byte is actually a double-reward flag for the single-open result window: hardcoding `01` made normal single opens show the `x2` result presentation.
- The server now writes `00` for ordinary single opens and `01` only when `DoubleRewards.Count > 0`, i.e. when Seria luck was already full and the open really triggered double reward.
- Expected ordinary single prefix after this correction:

```text
01 04 00 <sourceSlot> <materialSlot> <rewardList>
```

- Expected full-value double single prefix:

```text
01 04 01 <sourceSlot> <materialSlot> <rewardList including normal + double rows>
```

Known currently attempted batch ACK prefix:

```text
01 04 0A 00 43 00 FF FF
```

## Strong Chicken Gift Box / Wrong Other-Job Prompt

User observation:

- Opening `坚强鸡礼盒` still wrongly pops `购买其他职业的物品，可能会无法使用，您确定要购买吗？`

Current evidence splits this flow into a mall purchase phase and a later open-box phase. Latest capture around the prompt includes a cash-shop command:

```text
RECV 0x0040 body [00 00 03 00 00 FF 00 12 92 01 ...]
SEND 0x0040 body [01 00 FF FF FF FF 12 92 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00]
```

Field decode:

- `body[2] = 3`: three cart entries in the request.
- `body[4] = 0`: cera payment mode.
- Each cart entry resolves `commodityNo` at item offset `+3`.
- `12 92 01 00` is commodity `102930`.

Server log chain:

```text
CERA_SHOP_BUY parsed: 1 item(s) [102930] paymentMode=0
CeraShopBuy product=0x00019212 -> item=0x0098B3B5
CeraShopBuy auto-open source=0x0098B3B5 rewards=Main:0x0098B347x100,Main:0x0098B348x100
OPEN_MAGIC_BOX raw: 00 <boxSlot> 48-B3-98-00 <hammerSlot> 47-B3-98-00 64-00
```

Interpretation:

- Commodity `102930` is the mall product.
- The product item `0x0098B3B5` auto-opens into magic hammer `0x0098B347` and strong chicken box `0x0098B348`.
- The client warning is more likely tied to the `0x0040` mall purchase path, or to a client-side mall confirmation before `0x0040` is sent, than to the `0x03F3` strong-chicken open ACK itself.
- The old packet log used an old `0x03F3` ACK prefix for `RawListType=00`. Current source now writes the patched `openedCount, sourceSlot, materialSlot` order for normal boxes.

Current code involved:

- `Server/DfoServer/Network/Handlers/CeraShopHandler.cs`
- `Server/DfoServer/Network/Builders/CeraShop/CeraShopPurchaseAckBuilder.cs`

Current ACK builder intent:

- Success ACK starts with result `1`.
- It writes category `-1` to force all-category lookup.
- It writes the purchased `commodityNo`.
- It writes extra item count `0` to avoid the client treating extra item ids as shop items.

Remaining issue:

- Need live confirmation after the `RawListType=00` header fix.
- If the warning appears before `RECV 0x0040`, it is client-side mall metadata / purchase confirmation.
- If it appears after `SEND 0x0040` but before `RECV/SEND 0x03F3`, inspect `CeraShopPurchaseAckBuilder.BuildSuccess` and repeated ACK behavior for multi-item cart purchases.
- If it appears only after `0x03F3`, inspect the current `MagicBoxOpenAckBuilder.BuildBatch` runtime bytes first to ensure the running binary is using the patched normal-box header.

## Files Currently Touched In Worktree

Current implementation work is dirty and not yet final:

- `Server/DfoServer/Game/Inventory/SqliteInventoryStore.cs`
- `Server/DfoServer/Game/Inventory/SqliteInventoryStore.Move.cs`
- `Server/DfoServer/Network/Parsers/Inventory/MagicBoxOpenRequest.cs`
- `Server/DfoServer/Network/Builders/Inventory/MagicBoxOpenAckBuilder.cs`
- `Server/DfoServer/Network/Handlers/InventoryHandler.Package.cs`
- `Server/DfoServer/Game/Inventory/InventoryPackageStore.cs`
- `Server/DfoServer/Game/Inventory/InventoryModels.cs`
- `Server/DfoServer/Game/Inventory/InventoryDbPrimitives.cs`
- `Server/DfoServer/Game/Inventory/InventoryMigrationRunner.cs`
- `Server/DfoServer/Infrastructure/SqliteDatabaseBootstrap.cs`
- `Server/DfoServer/Sqlite/item_schema.sql`
- `Server/DfoServer/SelfTests/SelectablePackageSelfTest.cs`

## Validation Already Done Before Latest Live Test

Build:

```text
dotnet build Server\DfoServer\DfoServer.csproj -c Debug -p:UseAppHost=false -o %TEMP%\dnf86jp-issue280-dfoserver
```

Result:

```text
Build passed, 2 existing ScopedStoreContext obsolete warnings.
```

Self-test:

```text
dotnet %TEMP%\dnf86jp-issue280-dfoserver\DfoServer.dll --selftest-selectable-package
```

Result:

```text
189 PASS, 0 FAIL
```

Latest validation after the `RawListType=00` header split and diagnostic logs:

```text
dotnet build Server\DfoServer\DfoServer.csproj -c Debug -p:UseAppHost=false -o %TEMP%\dnf86jp-issue280-dfoserver
```

Result:

```text
Build passed, 2 existing ScopedStoreContext obsolete warnings.
```

```text
dotnet %TEMP%\dnf86jp-issue280-dfoserver\DfoServer.dll --selftest-selectable-package
```

Result:

```text
189 PASS, 0 FAIL
```

Latest validation after Seria-luck `ExtData0/ext_data1` item-list sync:

```text
dotnet build Server\DfoServer\DfoServer.csproj -c Debug -p:UseAppHost=false -o %TEMP%\dnf86jp-issue280-dfoserver
```

Result:

```text
Build passed, 2 existing ScopedStoreContext obsolete warnings.
```

```text
dotnet %TEMP%\dnf86jp-issue280-dfoserver\DfoServer.dll --selftest-selectable-package
```

Result:

```text
194 PASS, 0 FAIL
```

Latest validation after unifying `0x03F3` batch ACK header to count-first for both `RawListType=00` and `RawListType=04`:

```text
dotnet build Server\DfoServer\DfoServer.csproj -c Debug -p:UseAppHost=false -o %TEMP%\dnf86jp-issue280-dfoserver
```

Result:

```text
Build passed, 2 existing ScopedStoreContext obsolete warnings.
```

```text
dotnet %TEMP%\dnf86jp-issue280-dfoserver\DfoServer.dll --selftest-selectable-package
```

Result:

```text
194 PASS, 0 FAIL
```

Live client observation after launch through the canonical script:

- Single Seria luck: correct window and sound, but currently shows double reward.
- Ten-open: still not correct.
- Strong chicken gift box: still shows the wrong other-job purchase confirmation.

Latest live-client evidence after the count-first build:

- Single Seria luck: correct window and sound, but displayed popup rows did not match the user's observed inventory result. New diagnostic logging now records both `display=` ACK rows and `BOOSTER_MAIN_UPDATE` inventory refresh rows.
- Seria ten-open: no sound and client logged the random-box/hammer slot critical error after `CMD RECV 1011`; root cause is the count-first `0x03F3` ACK header.
- Strong chicken gift box: still shows the wrong purchase/other-job prompt, and the same slot critical error appeared after the count-first `0x03F3` ACK. The next live run must distinguish whether the remaining visible prompt is fixed by the source/material/count header or still comes from the earlier `0x0040` shop confirmation path.

Superseded correction after that evidence:

- The intermediate attempt wrote `success, clientType, sourceSlot, materialSlot, openedCount, primaryRewardList, 0, doubleRewardList`.
- That was still incomplete. The later correction below adds `sourceItemId` and `materialItemId` between the slots and count.
- `MagicBoxOpenAckBuilder.BuildSingle` keeps the known-good single-open window/sound prefix `success, clientType, doubleFlag, sourceSlot, materialSlot`, but its reward rows now come from the actual aggregated grants (`BoosterUseResult.Rewards`) instead of raw per-draw display rows. This targets the observed mismatch where a single open displayed duplicate rows while the bag received merged stack deltas.
- `InventoryHandler.Package` now logs ACK display rows and `BOOSTER_MAIN_UPDATE` rows so the next live test can compare popup intent with the inventory refresh directly.
- Frida MCP was available, but no `DNF.exe` process was running when checked, and the user had not asked for a new launch during this investigation step; no live hook was attached.

Latest root-cause correction after the 2026-07-04 13:39-13:41 client run:

- The live run was still using the old `0x03F3` body head:

```text
01 04 6B 00 FF FF 0A 00 ...
01 00 54 00 AE 00 64 00 ...
```

- Client `GameLog.log` immediately logged `CMDFUNC_ENUM_CMDPACKET_USE_RANDOMBOX_ITEM_EXPAND` critical errors after those ACKs:

```text
CRITICAL ERROR : 랜덤박스와 망치를 썼는데 해당 슬롯에 랜덤박스 또는 망치가 없음!
```

- The captured client request proves the expand request carries item template ids:

```text
04 <sourceSlot:i16> <sourceItemId:i32> <materialSlot:i16> <materialItemId:i32> <count:u16>
```

- The ACK must therefore echo the same source/material identity fields before the count, not just slots:

```text
success:u8
clientType:u8
sourceSlot:i16
sourceItemId:i32
materialSlot:i16
materialItemId:i32
count:u16
primaryRewardList
reservedOrSeparator:u16 = 0
doubleRewardList
```

- Expected Seria ten-open head after this correction:

```text
01 04 6B 00 A0 ED 28 00 FF FF FF FF FF FF 0A 00 ...
```

- Expected strong-chicken ten/open-100 head after this correction:

```text
01 00 54 00 48 B3 98 00 AE 00 47 B3 98 00 64 00 ...
```

- Focused self-test was updated TDD-style: it first failed with 8 layout failures when the item ids were missing, then passed after `MagicBoxOpenAckBuilder.BuildBatch` wrote the item ids.
- This fix has not yet been live-client tested. The previous user screenshots/logs were from the old `824B/5414B` body lengths; the corrected bodies should grow by 8 bytes because they include source and material item ids.

Seria luck value note after this pass:

- Server-side state is increasing and is sent through item-list refresh rows:

```text
slot107:0x0028EDA0x535/ext0=45
slot107:0x0028EDA0x534/ext0=46
slot107:0x0028EDA0x533/ext0=47
```

- The client still visibly did not show the value change in the user's run, so `0x00D9` remains a candidate close/overflow/value-refresh request:

```text
RECV 0x00D9 body [01 D0 00]
```

- Do not implement an arbitrary `0x00D9` response yet. The repository enum names collide (`CmdPacketType.OVERFLOW_INFO` vs `NotiPacketType.CLOSE_DISJOINT_STORE`), and there is no packet-layout proof. Use IDA/Frida runtime evidence before changing this.

Latest validation after restoring source/material/count batch ACK and aggregating single-open display rows:

```text
dotnet build Server\DfoServer\DfoServer.csproj -c Debug -p:UseAppHost=false -o %TEMP%\dnf86jp-issue280-dfoserver
```

Result:

```text
Build passed, 2 existing ScopedStoreContext obsolete warnings.
```

```text
dotnet %TEMP%\dnf86jp-issue280-dfoserver\DfoServer.dll --selftest-selectable-package
```

Result:

```text
195 PASS, 0 FAIL
```

Latest validation after adding source/material item ids to `0x03F3` batch ACK:

```text
dotnet build Server\DfoServer\DfoServer.csproj -c Debug -p:UseAppHost=false -o %TEMP%\dnf86jp-issue280-dfoserver
```

Result:

```text
Build passed, 2 existing ScopedStoreContext obsolete warnings.
```

```text
dotnet %TEMP%\dnf86jp-issue280-dfoserver\DfoServer.dll --selftest-selectable-package
```

Result:

```text
206 PASS, 0 FAIL
```

Frida status for this pass:

- `frida-mcp` tools are exposed in Codex.
- `get_process_by_name("DNF.exe")` returned `found=false`, so no live client hook was attached in this pass.

Latest live-client evidence after the source/material item-id ACK:

- The worktree service did send the new `0x03F3` body with source/material item ids; this was not a stale-binary run.
- Example Seria ten-open ACK head from `server.log` / `packet_log.txt`:

```text
01 04 6B 00 A0 ED 28 00 FF FF FF FF FF FF 0A 00 ...
```

- The client still rejected it immediately in `CMDFUNC_ENUM_CMDPACKET_USE_RANDOMBOX_ITEM_EXPAND`:

```text
CMD RECV 1011 (Size : 832) Result : Ok
CRITICAL ERROR : 랜덤박스와 망치를 썼는데 해당 슬롯에 랜덤박스 또는 망치가 없음!
Open IRDPopupWindow Type : 511
```

Root-cause correction from this evidence:

- `0x00D0` single-open uses this accepted native header:

```text
success:u8
clientType:u8
doubleFlag:u8
sourceSlot:i16
materialSlot:i16
rewardList
```

- The previous `0x03F3` fix still omitted the same `doubleFlag` byte. That made the expand handler read the source slot from the wrong offset, exactly matching the client-side random-box/hammer-slot critical error.
- `0x03F3` batch ACK should now start:

```text
success:u8
clientType:u8
doubleFlag:u8
sourceSlot:i16
sourceItemId:i32
materialSlot:i16
materialItemId:i32
count:u16
primaryRewardList
reserved:u16 = 0
doubleRewardList
```

- Expected Seria ten-open head after this correction:

```text
01 04 00 6B 00 A0 ED 28 00 FF FF FF FF FF FF 0A 00 ...
```

- Expected full-value Seria ten-open head should use `01 04 01 ...` when `DoubleRewards.Count > 0`.
- Expected strong-chicken head after this correction:

```text
01 00 00 <sourceSlot:i16> 48 B3 98 00 <hammerSlot:i16> 47 B3 98 00 <count:u16> ...
```

TDD evidence for the latest `0x03F3` correction:

```text
dotnet %TEMP%\dnf86jp-issue280-redtest\DfoServer.dll --selftest-selectable-package
```

Result before production change:

```text
195 PASS, 13 FAIL
```

The failures were the intended `0x03F3` offset checks for missing `doubleFlag` and shifted source/material/count fields.

After adding `doubleFlag` to `MagicBoxOpenAckBuilder.BuildBatch`:

```text
dotnet %TEMP%\dnf86jp-issue280-greentest\DfoServer.dll --selftest-selectable-package
```

Result:

```text
208 PASS, 0 FAIL
```

The normal worktree `bin\Debug` was also rebuilt after stopping only the locked `DfoServer` PID, and the real `Server\DfoServer\bin\Debug\DfoServer.dll` passed the same self-test:

```text
208 PASS, 0 FAIL
```

PVF reward-pool evidence after the user's "0金币" screenshot:

- `赛丽亚的幸运` (`2682272`, `stackable/ect/chn_random/chn_blessed_box.stk`) has 766 random-box reward entries.
- Its pool legitimately includes small stackable item ids such as:
  - `42`: `复活币`
  - `36`: `服务器喇叭`
  - `15`: `装备品级调整箱`
  - `30/31/44`: contract items
- The inspected pool did not indicate that "金币" should be emitted as a normal magic-box reward row. A displayed `0金币` is therefore more likely a native random-box result-row decoding/display mismatch than a real PVF reward choice.
- `坚强鸡礼盒` (`10007368`, `stackable/ect/chn_random/chn_amazingbox_10007368.stk`) has 22 random-box reward entries: `瞬间移动药剂` rows plus `赛丽亚的幸运`. It should not display gold either.
- `坚强鸡礼盒` and `幸运魔锤` PVF metadata both have `usable=[all]`, no `suitable job`, and no direct profession restriction in the parsed stackable fields:

```text
10007368 name=坚强鸡礼盒 usable=[all] impossible=none
10007367 name=幸运魔锤 usable=[all] impossible=none
```

- `DXF\GameLog.log` showed popup types `448/522/448` before the client sent packet `0x03F3`. If popup `448` is the "购买其他职业..." window, then that visible prompt is opened by the client before the server's magic-box ACK returns. The server-side `0x03F3` fix can address the later `511` random-box/hammer-slot error, but suppressing the pre-request profession prompt will require client/PVF popup-branch evidence.

Remaining live-client uncertainties:

- The `0x03F3` missing-flag fix has not yet been live-client tested.
- Single-open `0x00D0` reaches the correct client branch and sound/window path, but the user's displayed rows still did not match the actual inventory grants. If it persists after the batch offset fix, use Frida/IDA evidence for the 27-byte native reward row fields.
- Server-side Seria luck value persists and is sent in common item `ExtData0`, including incremental item refresh rows. The client UI still did not visibly move in the user's run. The post-single-open request remains a candidate for a value/overflow refresh, but must not be faked without packet-layout proof:

```text
RECV 0x00D9 body [01 D0 00]
```

Latest row-stride correction after the user's `复活币 + 她的心意70` screenshot:

- The native reward row is now 31 bytes, not 27 bytes.
- The old 27-byte row caused the client to start row 2 four bytes too late; row 2 item id then landed on row 1 or row 2 count data.
- Concrete proof: displayed `海之勇者礼包 2 (格斗家)` has item id `70`, exactly matching the actual `她的心意 x70` count from the server log.
- Validation:

```text
dotnet build Server\DfoServer\DfoServer.csproj -c Debug -p:UseAppHost=false -o %TEMP%\dnf86jp-issue280-rowstride
```

Result:

```text
Build passed, 2 existing ScopedStoreContext obsolete warnings.
```

```text
dotnet %TEMP%\dnf86jp-issue280-rowstride\DfoServer.dll --selftest-selectable-package
```

Result:

```text
212 PASS, 0 FAIL
```

The normal worktree Debug output was also rebuilt:

```text
dotnet build Server\DfoServer\DfoServer.csproj -c Debug
```

Result:

```text
Build passed, 2 existing ScopedStoreContext obsolete warnings.
```

Remaining live-client uncertainties after this correction:

- The row-stride fix has not yet been live-tested in the client.
- It should address the wrong native popup result rows for both single-open and ten-open.
- It may also fix the native ten-open result flow enough for sound/window behavior, but that still needs a live run.
- It does not by itself prove the visible Seria-luck value bar/text will move. Server-side value already increases and is sent as `ExtData0` in `0x000E`; the client still sends unhandled `0x00D9 [01 D0 00]` after single-open, so that remains the next evidence target.

Latest Seria-luck value refresh correction:

- Packet enum evidence shows server->client noti `0x019D` is named `BOOSTER_GAGE`.
- The project had historically wired `0x019D` through `LuckyStarInfoBodyBuilder`, sending `accounts.lucky_star`.
- That conflicts with the user-observed feature: the UI is specifically `赛丽亚的幸运值`, not rental/lucky-star state.
- `0x019D` is now wired to `accounts.seria_luck_value` through `BoosterGageBodyBuilder`.
- Character initialization loads `seria_luck_value` into the initialization snapshot and sends it as the 4-byte `0x019D` body.
- After opening `赛丽亚的幸运`, the server now sends a fresh `0x019D BOOSTER_GAGE` noti carrying `SeriaLuckValueAfter`.
- This is still paired with the existing `0x000E` item refresh, but no longer relies on the client reading the value from the random-box item's `ExtData0`.

Validation after the `BOOSTER_GAGE` correction:

```text
dotnet build Server\DfoServer\DfoServer.csproj -c Debug -p:UseAppHost=false -o %TEMP%\dnf86jp-issue280-rowstride
```

Result:

```text
Build passed, 2 existing ScopedStoreContext obsolete warnings.
```

```text
dotnet %TEMP%\dnf86jp-issue280-rowstride\DfoServer.dll --selftest-selectable-package
```

Result:

```text
216 PASS, 0 FAIL
```

The normal worktree Debug output was rebuilt again:

```text
dotnet build Server\DfoServer\DfoServer.csproj -c Debug
```

Result:

```text
Build passed, 2 existing ScopedStoreContext obsolete warnings.
```

## Next Debugging Plan

1. Do not guess further packet layouts.
2. Use latest logs to separate:
   - `0x00D0` single Seria luck behavior,
   - `0x03F3` batch Seria luck behavior,
   - `0x0040` cash-shop/strong-chicken prompt behavior.
3. For `0x03F3`, obtain client handler evidence for the exact success body layout after the primary reward list.
4. For Seria luck value, verify the account DB value before/after single opens and identify any client-visible value refresh packet.
5. For strong chicken, verify if the other-job prompt is caused by ACK parse fields or by the client's shop-item metadata before the server ACK returns.

## 2026-07-04 Strong-Chicken Crash Evidence

The latest `0x03F3` native ACK experiment is unsafe for `坚强鸡礼盒`.

Live evidence from the worktree server:

```text
[16:29:57.471] OPEN_MAGIC_BOX raw(15B): 00-56-00-48-B3-98-00-AE-00-47-B3-98-00-64-00
[16:29:57.483] OPEN_MAGIC_BOX ACK: type=0x03F3 bodyLen=6215 head=01-00-00-56-00-AE-00-64-00-C8-00-FF-FF-4E-AC-27-00-02-00-00-00-00-00-00 clientType=0x00 displayRows=200 doubleRows=0
[16:29:57.488] OPEN_MAGIC_BOX: source=0x0098B348 slot=86 requested=100 applied=100 remaining=0 material=0x0098B347x100@174 materialRemaining=0 clientType=0x00 displayRows=200 doubleRows=0 rewards=Main:0x0027AC4Ex200@86,Main:0x0028EDA0x100@65
[16:29:59.257] Unhandled CMD type=0x02B3 body(1B): 00
[16:29:59.354] Admin client disconnected
```

Client-side evidence:

```text
E:\DNF0828\86jp\DXF\CrashDNF2.cra  LastWriteTime=2026/7/4 16:29:59
```

Interpretation:

- Actual grant aggregation still looks correct: `复活币 x200` plus `赛丽亚的幸运 x100`.
- The crash is correlated with the display/native ACK path, especially the `0x03F3` body with `displayRows=200`.
- Do not ask the user to open `坚强鸡礼盒` again on this server build.
- Do not continue changing `0x03F3` by guesswork. The next step must be client handler evidence from IDA/Frida.

Related Seria-luck UI evidence from the same session:

```text
[16:27:31] single Seria luck: seriaLuck=78->79/100, no runtime BOOSTER_GAGE after open
[16:29:31] single Seria luck: seriaLuck=89->90/100, no runtime BOOSTER_GAGE after open
[16:29:33] ten Seria luck: seriaLuck=90->100/100, then BOOSTER_GAGE refresh value=100
```

Interpretation:

- The server-side value does increase.
- The client-visible progress UI still does not visibly update correctly for the user.
- `0x019D BOOSTER_GAGE` needs reverse-engineering before further changes; sending it on every open previously caused a brief false fire/full animation.

See `ISSUE_280_MAGIC_BOX_INVESTIGATION_PLAN.md` for the evidence-first plan before the next fix.

## 2026-07-04 Frida `0x03F3` Layout Breakthrough

Frida MCP was attached read-only to the live `DNF.exe` process. The client is 32-bit and loaded `DNF.exe` at `0x400000`.

The `0x03F3` packet registration was found at `0xCD09AA`:

```text
push 0
push 0xCCEBC0
push 0x3F3
call 0x1189FC0
```

So the `0x03F3 / USE_RANDOMBOX_ITEM_EXPAND` handler is `0xCCEBC0`.

Important handler findings:

- The dispatcher appears to consume the success flag before `0xCCEBC0`; the handler checks the success argument at `[ebp+0xC]`.
- The handler then reads:
  - `u8 clientType`
  - `u8 doubleFlag`
  - `u16 openCount`
  - `u16 sourceSlot`
  - `u16 materialSlot`
- `0xCCED57 cmp [clientType], 4` skips material-slot validation for `clientType == 4`, matching `赛丽亚的幸运`.
- The list reader `0xCCCEB0` initially appeared to read `u16 count`, then
  fixed 27-byte rows, but the later live ten-open crash supersedes using
  27-byte rows in the server ACK.
- The handler reads list #1, then a `u16` separator/unknown, then list #2.

Service changes made from this evidence:

- `BuildBatch` now writes `openCount` before `sourceSlot/materialSlot`.
- `BuildBatch` now uses 31-byte rows for `0x03F3`.
- `BuildSingle` still uses the previously live-correct 31-byte rows for `0x00D0`.
- Non-Seria `0x03F3` paths no longer use native ACK; they use the old aggregated `0x00A0` obtained-items popup path so `坚强鸡礼盒` no longer receives a 200-row native ACK.

Validation:

```text
--selftest-selectable-package: 212 PASS, 0 FAIL
isolated dotnet build: passed, 2 existing ScopedStoreContext obsolete warnings
```

Remaining unknown:

- `0x019D BOOSTER_GAGE` still needs client-side UI binding evidence for non-full Seria-luck progress.
- `0x03F3` live ten-open still needs a client smoke after restart to confirm the corrected header/31-byte rows open the special ten-open UI and sound.

## 2026-07-04 Frida `0x019D` Negative Evidence

Do not treat `0x019D / BOOSTER_GAGE` as the Seria-luck progress value.

New Frida evidence found a client UI reader at `DNF.exe+0xB09510`. It reads exactly 5 bytes through `0x2100570`:

```text
read 5 bytes into stack buffer
byte[0] != 0  -> UI object field +0x1E0
bytes[1..4]   -> UI object field +0x1E4
```

Related UI code calls `0xB09500` to read field `+0x1E4`, subtracts a current-time-like value from it, divides the result, and formats a remaining-time string. This strongly indicates the 4-byte value is an active/expiry timestamp or time target, not a 0..100 progress value.

This explains the user's fire-flash observation: sending the current server Seria-luck value as a 4-byte `0x019D` body can be interpreted as an active/full/fire state rather than progress.

Service guardrail added from this evidence:

- The default select-character init sequence no longer includes `0x019D`.
- `BoosterGageBodyBuilder` remains registered only as a safety gate for old DB templates, but returns `false` and sends no body.
- Runtime magic-box open no longer sends `0x019D` for non-full, full, or reset states.
- Seria-luck DB progression and item `2682272` `ExtData0` overlay remain intact. That is still the current best server-side value propagation path, but it is not yet proven to drive the visible top-left progress UI.

Current implementation invariant:

- Preserve `0x00D0` single-open ACK: it is live-confirmed to produce the correct special Seria-luck open window and sound.
- Preserve `0x03F3` batch ACK header for Seria-luck ten-open: success,
  clientType, doubleFlag, openCount, sourceSlot, materialSlot. Reward lists
  must use 31-byte rows; the older 27-byte row inference is superseded.
- Do not send native `0x03F3` for non-Seria strong-chicken/normal batch gift paths until client evidence proves the correct non-Seria UI. Those paths are isolated to the older aggregated `0x00A0` obtained-items popup for safety.

## 2026-07-04 Live Seria-Luck Ten-Open Crash Evidence

The user opened Seria-luck ten-open on the verified issue-280 worktree and the
client crashed immediately after receiving `0x03F3`.

```text
RECV 0x03F3 request: 04 43 00 A0 ED 28 00 FF FF FF FF FF FF 0A 00
SEND 0x03F3 bodyLen=906 head: 01 04 01 0A 00 43 00 FF FF 1E 00 FF FF 2A 00
client GameLog: CMD RECV 1011 (Size : 906) Result : Ok
client GameLog: createItem fail item index : -65536
client GameLog: createItem fail item index : 808464432
WER: DNF.exe exception 0xc0000409 at DNF.exe+0x008CD1B7
```

This proves the current header was accepted and the crash happened while the
client decoded the native reward lists. The older screenshot where actual
`她的心意 x70` displayed as an item whose id is `70` is the same row-stride
bug: count bytes were being read as the next item id.

Current protected conclusion:

- `0x00D0` single-open ACK stays as-is because the special window and sound were
  live-confirmed.
- Non-Seria/strong-chicken `0x03F3` stays on legacy aggregated `0x00A0`; the
  user live-tested strong-chicken twice with no "other job item" popup.
- Seria-luck `0x03F3` native ten-open must keep the accepted header but use
  31-byte rows for both primary and double reward lists.
- The current running server was still the old binary when this note was
  written. The next launch must use the harness script with `-Build` before any
  live ten-open retest.
- After this code change, isolated build plus `--selftest-selectable-package`
  passed: `210 PASS, 0 FAIL`.

Next evidence target:

- Find the real packet/state that updates the visible non-full `赛丽亚的幸运值` progress UI. Candidate areas are item `2682272` extra data consumption in the UI, a different notification/request pair such as the observed `0x00D9`, or a client-local refresh path after inventory item update.
