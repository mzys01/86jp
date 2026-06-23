using System;
using System.Collections.Generic;

namespace PvfLib
{
    
    
    
    
    public class StackableItemFile : PvfModelBase
    {
        #region 基本信息

        public string Name { get; set; }
        public string Explain { get; set; }
        public string FlavorText { get; set; }
        public int Grade { get; set; } = -1;
        public int Rarity { get; set; } = -1;
        public int MinimumLevel { get; set; } = -1;
        public int MaximumLevel { get; set; } = -1;

        #endregion

        #region 物品类型

        
        public string StackableType { get; set; }
        public int SubType { get; set; } = -1;
        public string AttachType { get; set; }
        public string ItemGroupName { get; set; }
        public string ItemCategory { get; set; }
        public int StackLimit { get; set; } = -1;

        #endregion

        #region 经济属性

        public int Price { get; set; } = -1;
        public int Value { get; set; } = -1;
        public int Weight { get; set; } = -1;
        public int CoolTime { get; set; } = -1;
        public string CooltimeGroup { get; set; }

        #endregion

        #region 外观

        public string Icon { get; set; }
        public string FieldImage { get; set; }
        public string IconMark { get; set; }
        public string MoveWav { get; set; }

        #endregion

        #region 使用限制

        public string UsableJob { get; set; }
        public string SuitableJob { get; set; }
        public string ImpossibleContents { get; set; }
        public int ExpirationDate { get; set; } = -1;
        public int UsablePeriod { get; set; } = -1;
        public int TradeLimit { get; set; } = -1;

        #endregion

        #region 强化/合成

        public int EnchantIndex { get; set; } = -1;
        public string EnchantTable { get; set; }
        public string BoosterInfo { get; set; }
        public int BoosterCategoryNum { get; set; } = -1;
        public int BoosterSelectionNum { get; set; } = -1;
        public string BoosterSelectCategory { get; set; }
        public string BoosterCategoryName { get; set; }

        #endregion

        #region 关联数据

        
        public string Equipment { get; set; }
        
        public string StringData { get; set; }
        
        public string IntData { get; set; }
        
        public string PackageData { get; set; }
        public string OutputItem { get; set; }
        public string InputItem { get; set; }
        public string NeedSkill { get; set; }
        public string NeedMaterial { get; set; }
        public int MonsterCardId { get; set; } = -1;

        #endregion

        #region 战斗属性

        public int PhysicalAttack { get; set; }
        public int MagicalAttack { get; set; }
        public int PhysicalDefense { get; set; }
        public int MagicalDefense { get; set; }

        #endregion
        #region 解析

        public static StackableItemFile Parse(string content)
        {
            if (string.IsNullOrEmpty(content))
                return new StackableItemFile { Content = content ?? "", Root = new ScriptNode { Tag = "ROOT" } };

            var root = new ScriptParser().Parse(content);
            var stk = new StackableItemFile { Root = root, Content = content };

            foreach (var node in root.Children)
            {
                string data = node.DataItems.Count > 0 ? node.GetFirstDataContent(content).Trim() : "";
                switch (node.Tag.ToLowerInvariant())
                {
                    
                    case "name": stk.Name = StripBacktick(data); break;
                    case "explain": stk.Explain = StripBacktick(data); break;
                    case "flavor text": stk.FlavorText = StripBacktick(data); break;
                    case "grade": stk.Grade = ParseInt(data); break;
                    case "rarity": stk.Rarity = ParseInt(data); break;
                    case "minimum level": stk.MinimumLevel = ParseInt(data); break;
                    case "maximum level": stk.MaximumLevel = ParseInt(data); break;

                    
                    case "stackable type": stk.StackableType = StripBacktick(data); break;
                    case "sub type": stk.SubType = ParseInt(data); break;
                    case "attach type": stk.AttachType = StripBacktick(data); break;
                    case "item group name": stk.ItemGroupName = StripBacktick(data); break;
                    case "item category": stk.ItemCategory = StripBacktick(data); break;
                    case "stack limit": stk.StackLimit = ParseInt(data); break;

                    
                    case "price": stk.Price = ParseInt(data); break;
                    case "value": stk.Value = ParseInt(data); break;
                    case "weight": stk.Weight = ParseInt(data); break;
                    case "cool time": stk.CoolTime = ParseInt(data); break;
                    case "cooltime group": stk.CooltimeGroup = data; break;

                    
                    case "icon": stk.Icon = data; break;
                    case "field image": stk.FieldImage = data; break;
                    case "icon mark": stk.IconMark = data; break;
                    case "move wav": stk.MoveWav = StripBacktick(data); break;

                    
                    case "usable job": stk.UsableJob = StripBacktick(data); break;
                    case "suitable job": stk.SuitableJob = StripBacktick(data); break;
                    case "impossible contents": stk.ImpossibleContents = data; break;
                    case "expiration date": stk.ExpirationDate = ParseInt(data); break;
                    case "usable period": stk.UsablePeriod = ParseInt(data); break;
                    case "trade limit max": stk.TradeLimit = ParseInt(data); break;

                    
                    case "enchant index": stk.EnchantIndex = ParseInt(data); break;
                    case "enchant table": stk.EnchantTable = data; break;
                    case "booster info": stk.BoosterInfo = data; break;
                    case "booster category num": stk.BoosterCategoryNum = ParseInt(data); break;
                    case "booster selection num": stk.BoosterSelectionNum = ParseInt(data); break;
                    case "booster select category": stk.BoosterSelectCategory = data; break;
                    case "booster category name": stk.BoosterCategoryName = data; break;

                    
                    case "equipment": stk.Equipment = data; break;
                    case "string data": stk.StringData = data; break;
                    case "int data": stk.IntData = data; break;
                    case "package data": stk.PackageData = data; break;
                    case "output item": stk.OutputItem = data; break;
                    case "input item": stk.InputItem = data; break;
                    case "need skill": stk.NeedSkill = data; break;
                    case "need material": stk.NeedMaterial = data; break;
                    case "monster card id": stk.MonsterCardId = ParseInt(data); break;

                    
                    case "physical attack": stk.PhysicalAttack = ParseInt(data); break;
                    case "magical attack": stk.MagicalAttack = ParseInt(data); break;
                    case "physical defense": stk.PhysicalDefense = ParseInt(data); break;
                    case "magical defense": stk.MagicalDefense = ParseInt(data); break;
                }
            }

            return stk;
        }

        #endregion
    }
}
