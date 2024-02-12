using System.Text.Json.Nodes;

namespace TarkovMonitor
{
    public class JsonEventArgs : EventArgs
    {
        public string type { get; set; }
        public string eventId { get; set; }
    }
    public class GroupMatchUserLeaveEventArgs : JsonEventArgs
    {
        public string Nickname { get; set; } = "You";
    }
    public class GroupEventArgs : JsonEventArgs
    {
        public PlayerInfo Info { get; set; }
        public bool isLeader { get; set; }
    }
    public class GroupMatchRaidReadyEventArgs : JsonEventArgs
    {
        public ExtendedProfile extendedProfile { get; set; }
        public override string ToString()
        {
            return $"{this.extendedProfile.Info.Nickname} ({this.extendedProfile.PlayerVisualRepresentation.Info.Side}, {this.extendedProfile.PlayerVisualRepresentation.Info.Level})";
        }
    }
    public class ExtendedProfile
    {
        public PlayerInfo Info { get; set; }
        public bool isLeader { get; set; }
        public PlayerVisualRepresentation PlayerVisualRepresentation { get; set; }
    }
    public class PlayerInfo
    {
        public string Side { get; set; }
        public int Level { get; set; }
        public string Nickname { get; set; }
        public int MemberCategory { get; set; }
    }
    public class PlayerVisualRepresentation
    {
        public PlayerInfo Info { get; set; }
        public PlayerEquipment Equipment { get; set; }
        public PlayerClothes Customization { get; set; }
    }
    public class PlayerEquipment
    {
        public string Id { get; set; }
        public LoadoutItem[] Items { get; set; }
    }
    public class LoadoutItem
    {
        public string _id { get; set; }
        public string _tpl { get; set; }
        public string? parentId { get; set; }
        public string? slotId { get; set; }
        public string? name { get; set; }
        //public LoadoutItemLocation? location { get; set; }
        public LoadoutItemProperties? upd { get; set; }
        public override string ToString()
        {
            var displayName = this._tpl;
            if (this.name != null) displayName = this.name;
            if (this.upd?.StackObjectsCount > 1) displayName += $" (x{this.upd.StackObjectsCount})";
            if (this.upd?.Repairable != null) displayName += $" ({Math.Round(this.upd.Repairable.Durability, 2)}/{this.upd.Repairable.MaxDurability})";
            return displayName;
        }
    }
    public class LoadoutItemLocation
    {
        public int x { get; set; }
        public int y { get; set; }
        public int r { get; set; }
        public bool isSearched { get; set; }
    }
    public class LoadoutItemProperties
    {
        public int? StackObjectsCount { get; set; }
        public bool? SpawnedInSession { get; set; }
        public LoadoutItemPropertiesDurability? Repairable { get; set; }
        public LoadoutItemPropertiesHpResource? MedKit { get; set; }
        public LoadoutItemPropertiesHpResource? FoodDrink { get; set; }
        public LoadoutItemPropertiesFireMode? FireMode { get; set; }
        public LoadoutItemPropertiesScope? Sight { get; set; }
        public LoadoutItemPropertiesResource? Resource { get; set; }
        public LoadoutItemPropertiesDogtag? Dogtag { get; set; }
        public LoadoutItemPropertiesTag? Tag { get; set; }
        public LoadoutItemPropertiesKey? Key { get; set; }
    }
    public class LoadoutItemPropertiesDurability
    {
        public float MaxDurability { get; set; }
        public float Durability { get; set; }
    }
    public class LoadoutItemPropertiesHpResource
    {
        public int HpResource { get; set; }
    }
    public class LoadoutItemPropertiesFireMode
    {
        public string FireMode { get; set; }
    }
    public class LoadoutItemPropertiesScope
    {
        public List<int> ScopesCurrentCalibPointIndexes { get; set; }
        public List<int> ScopesSelectedModes { get; set; }
        public int SelectedScope { get; set; }
    }
    public class LoadoutItemPropertiesResource
    {
        public int Value { get; set; }
    }
    public class LoadoutItemPropertiesDogtag
    {
        public string AccountId { get; set; }
        public string ProfileId { get; set; }
        public string Side { get; set; }
        public int Level { get; set; }
        public string Time { get; set; }
        public string Status { get; set; }
        public string KillerAccountId { get; set; }
        public string KillerProfileId { get; set; }
        public string KillerName { get; set; }
        public string WeaponName { get; set; }
    }
    public class LoadoutItemPropertiesTag
    {
        public string Name { get; set; }
    }
    public class LoadoutItemPropertiesKey
    {
        public int NumberOfUsages { get; set; }
    }
    public class PlayerClothes
    {
        public string Head { get; set; }
        public string Body { get; set; }
        public string Feet { get; set; }
        public string Hands { get; set; }
    }
    public class GroupRaidSettingsEventArgs : JsonEventArgs
    {
        public string Map
        {
            get
            {
                return raidSettings.location;
            }
        }
        public string RaidMode
        {
            get
            {
                return raidSettings.raidMode;
            }

        }
        public RaidType RaidType
        {
            get
            {
                if (raidSettings.side == "Pmc")
                {
                    return RaidType.PMC;
                }
                if (raidSettings.side == "Savage")
                {
                    return RaidType.Scav;
                }
                return RaidType.Unknown;
            }
        }
        public RaidSettings raidSettings { get; set; }
        public class RaidSettings
        {
            public string location { get; set; }
            public string raidMode { get; set; }
            public string side { get; set; }
        }
    }
    public class ChatMessageEventArgs : JsonEventArgs
    {
        public ChatMessage message { get; set; }
    }
    public class ChatMessage
    {
        public MessageType type { get; set; }
        public string text { get; set; }
        public bool hasRewards { get; set; }
    }
    public class SystemChatMessage : ChatMessage
    {
        public string templateId { get; set; }
    }
    public class SystemChatMessageEventArgs : ChatMessageEventArgs
    {
        public new SystemChatMessage message { get; set; }
    }
    public class SystemChatMessageWithItems : SystemChatMessage
    {
        public MessageItems items { get; set; }
    }
    public class MessageItems
    {
        public List<LoadoutItem> data { get; set; }
    }
    public class FleaMarketSoldChatMessage : SystemChatMessageWithItems
    {
        public FleaSoldData systemData { get; set; }
    }
    public class FleaSoldData
    {
        public string buyerNickname { get; set; }
        public string soldItem { get; set; }
        public int itemCount { get; set; }
    }
    public class FleaSoldMessageEventArgs: SystemChatMessageEventArgs
    {
        public string Buyer
        {
            get
            {
                return message.systemData.buyerNickname;
            }
        }
        public string SoldItemId
        {
            get
            {
                return message.systemData.soldItem;
            }
        }
        public int SoldItemCount
        {
            get
            {
                return message.systemData.itemCount;
            }
        }
        public Dictionary<string, int> ReceivedItems
        {
            get
            {
                Dictionary<string, int> items = new();
                foreach (var item in message.items.data)
                {
					items.Add(item._tpl, item.upd?.StackObjectsCount ?? 1);
                }
                return items;
            }
        }
        public new FleaMarketSoldChatMessage message { get; set; }
    }
    public class FleaExpiredeMessageEventArgs: JsonEventArgs
    {
        public string ItemId
        {
            get
            {
                return message.items.data[0]._id;
            }
        }
        public int ItemCount
        {
            get
            {
                return message.items.data[0].upd?.StackObjectsCount ?? 1;
            }
        }
        public SystemChatMessageWithItems message { get; set; }
    }
    public class TaskStatusMessageEventArgs : ChatMessageEventArgs
    {
        public string TaskId
        {
            get
            {
                return message.templateId.Split(' ')[0];
            }
        }
        public TaskStatus Status
        {
            get
            {
                return (TaskStatus)message.type;
            }
        }
        public new SystemChatMessage message { get; set; }
    }
}
