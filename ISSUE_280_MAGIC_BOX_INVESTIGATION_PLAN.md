# Issue 280 魔盒 / 赛丽亚幸运值调查计划

更新时间：2026-07-04

状态：暂停继续猜协议体。先把客户端解析、UI 触发、服务端回包三件事拆开取证，再改代码。

## 总目标

修复 Issue 280 的魔盒开启链路，同时避免再破坏已经正确的单开行为：

- `赛丽亚的幸运` 单开：保留当前正确的开启窗口、特殊音效、获得物品 UI、满幸运值双倍奖励展示。
- `赛丽亚的幸运值` UI：服务端必须让客户端看到当前已累计多少点；未满时应逐步增加，满时 UI 才冒火，下一个盒子触发双倍并重置/回落。
- 十连开：使用客户端正确的十连专用大界面，左侧 10 次开启结果，右侧双倍奖励结果；不能落到普通“获得物品”列表。
- `坚强鸡礼盒`：恢复不崩溃、不弹“购买其他职业物品”的正确开启逻辑。用户确认过：之前坚强鸡礼盒开启逻辑是正确的，不能再盲改。

## 已知正确，先保护

- `0x00D0 OPEN_MAGIC_BOX_SINGLE` 的单开路径已经接近正确：赛丽亚单开窗口、音效、获得物品 UI 是对的。
- 满幸运值后的赛丽亚单开双倍奖励结果 UI 是对的；这个 UI 是 `赛丽亚的幸运` 独有展示，不等同于普通获得物品弹窗。
- 服务端实际发奖聚合是可信的：坚强鸡崩溃前服务端日志显示实际给了 `0x0027AC4E x200` 和 `0x0028EDA0 x100`，问题主要在客户端显示/解析路径。
- 31 字节 reward row 比 27 字节 row 更接近客户端期望；27 字节 row 曾导致展示错位。

保护规则：

- 未拿到客户端处理证据前，不再改 `0x00D0` 单开协议。
- 未拿到 `0x03F3` 客户端布局证据前，不再继续试 header/list 顺序。
- 不再让用户测试会触发坚强鸡闪退的服务端版本。
- 所有 live 测试必须先写明“要你做什么动作、预期看什么、失败看哪份日志”。

## 当前危险证据

2026-07-04 16:29:57，坚强鸡礼盒走 `0x03F3` 后客户端闪退：

- 请求体：`00-56-00-48-B3-98-00-AE-00-47-B3-98-00-64-00`
- 服务端 ACK：`type=0x03F3 bodyLen=6215`
- ACK 头部：`01-00-00-56-00-AE-00-64-00-C8-00-FF-FF-4E-AC-27-00-02-00-00-00-00-00-00`
- 服务端展示列表：`displayRows=200`，内容为 `0x0027AC4E x2` 与 `0x0028EDA0 x1` 重复 100 次。
- 实际发奖：`0x0027AC4E x200@86`、`0x0028EDA0 x100@65`。
- 客户端随后发 `0x02B3 [00]` 并断线，`DXF/CrashDNF2.cra` 时间戳为 2026-07-04 16:29:59。

判断：当前 `0x03F3` native ACK + 200 展示行对坚强鸡不安全。不能再让用户开坚强鸡验证这个版本。

## 已尝试和结论

| 尝试 | 结果 | 结论 |
| --- | --- | --- |
| 27 字节 reward row | 单开/展示错位 | row stride 不对，不能回退到 27 |
| 31 字节 reward row | 单开获得物品 UI/音效正确 | 单开路径继续保留 |
| `0x03F3` 改成 count-first | 客户端 slot critical / 错误弹窗 | 不能凭服务端猜字段顺序 |
| `0x03F3` header 带 source/material item id | 客户端 slot critical / 错误弹窗 | 这个布局不成立 |
| `0x03F3` header 去掉 item id + 展开 200 行 | 坚强鸡客户端闪退 | 这条路径不安全，必须先逆向客户端 |
| 每次开盒后都发 `0x019D BOOSTER_GAGE` | 赛丽亚幸运 UI 瞬间冒火又消失 | `0x019D` 不是“每次进度+1”的简单动画包，至少不能每次乱发 |
| 只在满值时发 `0x019D [64 00 00 00]` | 服务端日志正确，但 UI 仍未确认正确增长 | UI 增长可能走另一个包、物品扩展字段、或客户端本地状态刷新 |

## 调查原则

1. 一次只改变一个变量。
2. 先逆向客户端解析，再改服务端协议。
3. 先用日志、IDA、Frida 得证据；只有需要用户动作时再启动/要求开盒。
4. 任何测试前先写明成功/失败判据，不让“看起来像”当结论。
5. 单开正确路径作为基准样本，十连和坚强鸡不能再通过改单开去碰运气。

## 任务 1：安全收口，避免继续崩客户端

目标：在继续调查前，先确定当前服务端不会诱导用户再开崩坚强鸡。

行为：

- 检查当前运行的 worktree 服务端是否仍是危险 `0x03F3 bodyLen=6215/displayRows=200` 版本。
- 代码层面只允许做最小安全收口：把坚强鸡从危险 native `0x03F3` 展示路径隔离出来，或恢复到旧的已知不崩逻辑。
- 不改变 `0x00D0` 单开赛丽亚幸运路径。

需要得到的结果：

- 自测能证明坚强鸡实际发奖仍是 `复活币/赛丽亚幸运` 聚合结果。
- live 测试前不会再发送 200 行危险展示体。

暂不做的事：

- 不在没有客户端布局证据时重新设计十连 UI 包。
- 不要求用户再次开坚强鸡测试崩溃版。

## 任务 2：静态逆向 `0x03F3` 十连/批量开启处理器

目标：拿到客户端 `CMDFUNC_ENUM_CMDPACKET_USE_RANDOMBOX_ITEM_EXPAND` 的真实解析布局。

工具：

- IDA Pro MCP，先读 `.codex-harness/docs/IDA_PRO_MCP.md`。
- 关键字：`USE_RANDOMBOX_ITEM_EXPAND`、`0x03F3`、十连/随机盒/魔盒相关字符串。

具体要查：

- `0x03F3` 成功回包的字段顺序：success、clientType、doubleFlag、sourceSlot、materialSlot、requested/open count、display count、double count 是否存在。
- reward row 的 stride、字段含义、最大行数。
- 十连专用大界面是否要求“10 个开启组”，而不是扁平 `displayRows=30`。
- 右侧双倍奖励列表如何传：单独 list、list 后 count，还是每组内字段。
- 坚强鸡 `clientType=0x00` 是否走同一个 UI，或者需要普通礼盒/批量礼盒专用 UI。
- 错误弹窗“购买其他职业物品”是由哪个字段触发：slot、item id、job flag、shop metadata，还是解析错位。

输出物：

- `0x03F3` body layout 表：offset、类型、含义、证据来源。
- 十连 UI 所需数据结构：左侧 10 项、右侧双倍项如何编码。
- 坚强鸡是否可用 `0x03F3`，如果可用需要聚合还是展开。

## 任务 3：Frida 运行时取证 `0x03F3`

目标：验证 IDA 推出来的字段在真实客户端里如何被读、如何决定 UI 分支。

工具：

- Frida MCP，先读 `.codex-harness/docs/FRIDA_MCP.md`。
- 只读 hook 优先：包处理入口、UI 打开函数、弹窗函数、错误字符串触发点、关键字段读取位置。

测试矩阵：

| 测试 | 用户动作 | 预期证据 | 备注 |
| --- | --- | --- | --- |
| 赛丽亚单开基准 | 开 1 个赛丽亚幸运 | 记录正确 UI/音效路径，不改它 | 已知接近正确 |
| 赛丽亚十连 | 开 10 个赛丽亚幸运 | 记录 `0x03F3` 解析字段、专用大 UI 是否打开 | 当前 UI 错 |
| 坚强鸡安全版 | 仅在安全收口后开 | 证明不崩、不弹职业购买框 | 不能用当前危险版 |

失败判据：

- 出现“购买其他职业物品”弹窗：立即记录触发函数和入参，不继续下一轮。
- 出现崩溃/断线：记录最后一个 handler、最后一个 UI 函数、最后一包。

## 任务 4：逆向赛丽亚幸运值 UI

目标：弄清楚左上角 `赛丽亚的幸运值` UI 到底吃什么状态。

已知现象：

- 服务端数据库和日志显示值会增长，例如 `78->79`、`89->90`、`90->100`。
- 初始化时服务端发过 `0x019D [4E 00 00 00]`。
- 满值时服务端发过 `0x019D [64 00 00 00]`。
- 只靠 `0x000E` 物品刷新和当前 `0x019D` 策略，用户看到的单开进度仍没正确变化。

具体要查：

- `0x019D BOOSTER_GAGE` 在客户端是否是绝对值、满值通知、火焰动画触发，还是其它系统共用包。
- `0x0028EDA0` 赛丽亚幸运道具的 `ExtData0` 是否被 UI 读取；如果读取，读取发生在背包刷新、道具 tooltip、还是专门 UI。
- 开盒后是否还有一个客户端期待的刷新包，例如 `0x00D9` 相关请求/响应、账号状态刷新、buff/gage noti。
- UI 冒火状态和“下一次双倍”是否是同一个状态，还是一个显示值加一个满值标记。

测试矩阵：

| 初始值 | 动作 | 服务端应记录 | 客户端应表现 | 要确认的问题 |
| --- | --- | --- | --- | --- |
| 78 | 单开 1 个 | `78->79` | UI 进度增加，不冒火 | 非满值进度包是什么 |
| 90 | 十连 | `90->100` | UI 满并冒火 | 满值通知是什么 |
| 100 | 单开 1 个 | 双倍触发并重置/回落 | 双倍 UI 正确，火状态消失 | reset 包是什么 |

输出物：

- 赛丽亚幸运值 UI 协议表：初始化、非满增长、满值、双倍触发后 reset。
- 服务端应发送的包顺序。

## 任务 5：实现前的服务端设计

只有任务 2-4 得到证据后才改服务端。预计改动层级：

- Parser：确认 `0x03F3` 请求字段，不扩大已有猜测。
- Package open service：保持实际发奖逻辑；把“实际发奖”和“客户端展示模型”分离。
- ACK builder：按客户端证据分别构造单开、十连、坚强鸡/普通批量礼盒的展示协议。
- Luck state：按客户端证据发送赛丽亚幸运值进度/满值/reset 包。
- SelfTests：新增 golden byte 测试，覆盖单开、十连、满值双倍、坚强鸡批量，不再只测发奖。

禁止事项：

- 不用“普通获得物品 UI”替代十连专用大界面。
- 不用金币/随机占位物填展示列表。
- 不让 display list 和 actual grant list 混在一起互相污染。

## 任务 6：验证计划

本地验证：

- `dotnet run --project Server/DfoServer/DfoServer.csproj -- --selftest-selectable-package`
- `dotnet build Server/DfoServer.sln`

live 验证只在用户明确要求启动时进行，并且必须使用：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File E:\DNF0828\86jp\.codex-harness\scripts\Start-86jpCapturedStack.ps1
```

live 验证顺序：

1. 单开赛丽亚幸运：确认窗口、音效、获得物品 UI 不回退；确认幸运值 UI 增长。
2. 满值单开赛丽亚幸运：确认双倍奖励 UI 和 reset 行为。
3. 十连赛丽亚幸运：确认专用大界面，左侧 10 项，右侧双倍项，音效正确。
4. 坚强鸡礼盒：确认不崩、不弹职业购买框、实际获得和展示一致。

每次 live 后检查：

- `Server/DfoServer/bin/Debug/capture_logs/packet_log.txt`
- `Server/DfoServer/bin/Debug/capture_logs/pvfproxy_*.log`
- `Server/DfoServer/bin/Debug/server.log`
- `DXF/GameLog.log`
- `DXF/CrashDNF*.cra`

## 当前下一步

1. 已完成任务 1 的第一层安全收口：非赛丽亚来源的 `0x03F3` 先回到旧的聚合 `0x00A0` 获得物品路径，避免坚强鸡继续收到危险 native ACK。
2. 已用 Frida MCP 完成任务 2 的关键部分：确认 `0x03F3` 客户端 handler 注册点和包体读取顺序。
3. 下一步继续任务 4：定位 `0x019D` 和赛丽亚幸运值 UI 的真实绑定关系。

在拿到上述证据前，不继续改十连协议体。

## 2026-07-04 Frida `0x03F3` 证据更新

Frida 只读 attach 到正在运行的 `DNF.exe`：

- PID：`18572`
- 架构：`ia32`
- `DNF.exe` 基址：`0x400000`
- `86JP.dll` 基址：`0x58890000`

`0x03F3` 注册点：

```text
0xCD09AA  push 0
0xCD09AC  push 0xCCEBC0
0xCD09B1  push 0x3F3
0xCD09B6  mov ecx, esi
0xCD09B8  call 0x1189FC0
```

结论：`0x03F3 / USE_RANDOMBOX_ITEM_EXPAND` 的客户端 handler 是 `0xCCEBC0`。

`0xCCEBC0` handler 的成功体读取顺序：

```text
dispatcher consumes success flag first
read u8  clientType
read u8  doubleFlag
read u16 openCount
read u16 sourceSlot
read u16 materialSlot
read list #1 through 0xCCCEB0
read u16 separator/unknown
read list #2 through 0xCCCEB0
```

关键分支：

```text
0xCCED57  cmp byte ptr [ebp - 0x613], 4
0xCCED5E  je 0xCCEE53
```

结论：`clientType == 4` 会跳过材料槽校验，这对应 `赛丽亚的幸运`；`clientType == 0` 的坚强鸡/普通批量礼盒会走材料校验。

`0xCCCEB0` reward-list reader：

- 先读 `u16 count`。
- 每行使用 `0x2100490`/`0x2100500`/`0x2100420` 读取固定字段。
- Frida 反汇编确认：
  - `0x2100420` 读 `u8`
  - `0x2100490` 读 `u16`
  - `0x2100500` 读 `u32`
- `0x03F3` list row 是 27 字节。

服务端映射：

- `MagicBoxOpenAckBuilder.BuildBatch` 必须写：
  - success
  - clientType
  - doubleFlag
  - openCount
  - sourceSlot
  - materialSlot
  - 27-byte primary list
  - `u16 0` separator
  - 27-byte double list
- `BuildSingle` 目前保持不动，因为用户确认 `0x00D0` 单开窗口、音效、获得物品 UI 已正确，不能把 `0x03F3` 的 27 字节 row 套给单开。
- 非赛丽亚来源的 `0x03F3` 暂时不走 native ACK，先用旧的聚合 `0x00A0` 获得物品路径保护坚强鸡。

验证：

```text
dotnet run --project Server/DfoServer/DfoServer.csproj -c Debug -- --selftest-selectable-package
=> 212 PASS, 0 FAIL

dotnet build Server/DfoServer/DfoServer.csproj -c Debug -p:UseAppHost=false -o %TEMP%\dnf86jp-issue280-frida-layout-build
=> Build passed, 2 existing ScopedStoreContext obsolete warnings
```

## 2026-07-04 `0x019D` plan update

New Frida evidence makes the old `0x019D == Seria-luck progress value` assumption invalid.

Observed client path:

```text
DNF.exe+0xB09510 reads 5 bytes
byte[0]      -> UI active/fire flag at object +0x1E0
bytes[1..4] -> time-like value at object +0x1E4
```

Other UI code reads `+0x1E4`, subtracts the current-time-like value, divides the result, and formats a remaining-time string. Therefore the server must not send the current `seria_luck_value` as a 4-byte `0x019D` body.

Immediate implementation rule:

- Disable all current server `0x019D` sends, including select-character init and post-open refresh.
- Keep DB progression and item `2682272` `ExtData0` overlay intact.
- Keep the live-confirmed `0x00D0` single-open path unchanged.
- Do not claim visible `赛丽亚的幸运值` progress is fixed until the real client binding is identified.

Next investigation target:

- Reverse the visible non-full Seria-luck progress UI binding. Focus on item `2682272` extra data reads, any request/response around `0x00D9`, and client-side refresh calls after `0x000E` item updates.
