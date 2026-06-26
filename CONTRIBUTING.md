# 贡献指南

感谢你对项目的关注！为了提高协作效率，请在提交 PR 前阅读以下内容。

## 提交前准备

1. **Rebase 到最新 main**：提交前确保你的分支已经 rebase 到最新的 main，避免冲突
2. **确保编译通过**：`dotnet build Server/DfoServer/DfoServer.csproj -c Debug`
3. **一个 PR 只做一件事**：不要在一个 PR 里混合多个不相关的功能或修复

## 数据库变更

- 新增表：在 `Sqlite/item_schema.sql` 中添加 `CREATE TABLE IF NOT EXISTS`
- 新增列：除了改 schema 文件，还必须在 `Infrastructure/SqliteDatabaseBootstrap.cs` 的 `Initialize` 方法中添加 `EnsureColumns` 调用，否则已有数据库不会自动补列

## 协议改动

涉及新增或修改包格式时，请在 PR 描述中简要说明字段来源：
- PVF 数据（标注文件路径）
- 抓包实测（标注包体 hex 示例）
- 推测/参考（说明参考了什么）

## 项目结构

```
Server/DfoServer/
  Game/Inventory/          背包系统（拆分为多个 Store）
  Game/CharacterData/      角色数据 Repository
  Game/Dungeon/            副本逻辑
  Game/Skills/             技能系统
  Network/Handlers/        协议处理（按域拆分子目录）
  Network/Builders/        封包构建
  Network/Protocol/        协议分发
  GameWorld/               PVF 只读数据
```

## 社区交流

Discord: https://discord.gg/3wct6SZp
