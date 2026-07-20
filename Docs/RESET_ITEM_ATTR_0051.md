# 装备品级调整箱（0x0051）服务端实现

本次修改只涉及服务端及其 PVF 解析依赖；`Patch`、`86JP.dll` 和客户端资源均未修改。

## 实现内容

- 解析客户端 `0x0051` 的 8 字节请求：目标槽位、目标物品 ID、调整箱槽位。
- 兼容部分启动状态下客户端发送的旧式 8 字节异或正文。
- 校验目标装备、物品 ID、装备锁、调整箱数量、有效期和适用部位。
- 普通/解放调整箱随机生成新的装备品级种子。
- 黄金调整箱写入最高品级种子 `999999998`。
- 在同一个 SQLite 事务中更新装备、消耗一个调整箱并写审计日志；失败不消耗道具。
- 成功返回 `0x0051` ACK，随后以 `0x000E` 增量刷新目标装备槽和调整箱槽。
- 增量刷新异常时回退到主背包全量刷新；调整箱耗尽时同步排序锁。
- 添加专项自测入口：`--selftest-reset-item-attr`。

## PVF 中可直接使用的调整箱 ID

### 普通随机调整

| ID | 名称 | 适用范围 |
|---:|---|---|
| 15 | 装备品级调整箱 | 可调整的装备类型；称号仅调整基础属性 |
| 897 | 解放的装备品级调整箱 | 可调整的装备类型；称号仅调整基础属性 |

### 黄金最高品级调整

| ID | 类型 | 适用范围 |
|---:|---|---|
| 2683895 | 黄金品级调整箱（武器） | 武器 |
| 10004897 | 黄金品级调整箱（武器） | 武器 |
| 10006368 | 黄金品级调整箱（武器） | 武器 |
| 10007452 | 黄金品级调整箱（武器） | 武器 |
| 2683896 | 黄金品级调整箱（防具） | 上衣、下装、头肩、腰带、鞋 |
| 10006369 | 黄金品级调整箱（防具） | 上衣、下装、头肩、腰带、鞋 |
| 10007453 | 黄金品级调整箱（防具） | 上衣、下装、头肩、腰带、鞋 |
| 2683897 | 黄金品级调整箱（首饰） | 项链、戒指、手镯 |
| 10006370 | 黄金品级调整箱（首饰） | 项链、戒指、手镯 |
| 2683898 | 黄金品级调整箱（特殊装备） | 辅助装备、魔法石 |
| 10007893 | 黄金品级调整箱 | 除称号外的上述所有部位 |

以上共 13 个直接使用型调整箱。PVF 中另外命中的礼盒、礼袋、礼包和 booster 不直接走 `0x0051`，应先走各自的开启逻辑。

## 修改文件

新增：

- `Server/DfoServer/Game/Inventory/ResetItemAttrPolicyResolver.cs`
- `Server/DfoServer/Game/Inventory/SqliteInventoryStore.ResetItemAttr.cs`
- `Server/DfoServer/Network/Parsers/Inventory/ResetItemAttrRequestParser.cs`
- `Server/DfoServer/Network/Builders/Inventory/ResetItemAttrAckBuilder.cs`
- `Server/DfoServer/Network/Handlers/InventoryHandler.ResetItemAttr.cs`
- `Server/DfoServer/SelfTests/ResetItemAttrSelfTest.cs`

增量修改：

- `Server/DfoServer/Program.cs`
- `Server/DfoServer/Network/Protocol/GameProtocolHandler.cs`
- `Server/DfoServer/Game/Inventory/IInventoryStore.cs`
- `Server/DfoServer/Game/Inventory/InventoryModels.cs`
- `Server/DfoServer/Game/Inventory/InventoryAuditLogger.cs`
- `Server/DfoServer/Game/Inventory/InventoryDbPrimitives.cs`
- `Tool/PvfLib/Models/StackableItemFile.cs`

客户端结果窗标题为空、仅“最下级”排版偏移属于客户端 Popup 342/界面资源问题，不在本次服务端修改范围内。
