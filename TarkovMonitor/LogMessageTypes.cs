using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TarkovMonitor
{
    public class LogMessage
    {
        public string type { get; set; }
        public string eventId { get; set; }
    }

    public class NewMessage : LogMessage
    {
        public string dialogId { get; set; }
        public ChatMessage message { get; set; }
    }
    public class ChatMessage
    {
        public string _id { get; set; }
        public string uid { get; set; }
        public int type { get; set; }
        public int dt { get; set; }
        public string text { get; set; }
        public string templateId { get; set; }
        public bool hasRewards { get; set; }
        public int maxStorageTime { get; set; }
        public MessageAttachments? items { get; set; }
        public Dictionary<string, string>? systemData { get; set; }
    }
    public class FleaSoldNewMessage : LogMessage
    {
        public new FleaSoldChatMessage message { get; set; }
    }
    public class FleaSoldChatMessage : ChatMessage
    {
        public new FleaSoldData systemData { get; set; }
    }
    public class FleaSoldData
    {
        public string buyerNickname { get; set; }
        public string soldItem { get; set; }
        public int itemCount { get; set; }
    }
    public class MessageAttachments
    {
        public string stash { get; set; }
        public List<MessageAttachment> data { get; set; }
    }
    public class MessageAttachment
    {
        public string _id { get; set; }
        public string _tpl { get; set; }
        public ItemAttachmentCount upd { get; set; }
        public string parentId { get; set; }
        public string slotId { get; set; }
    }
    public class ItemAttachmentCount
    {
        public int StackObjectsCount { get; set; }
    }
    public class UserConfirmed : LogMessage
    {
        public string profileid { get; set; }
        public string profileToken { get; set; }
        public string status { get; set; }
        public string ip { get; set; }
        public int port { get; set; }
        public string sid { get; set; }
        public string version { get; set; }
        public string location { get; set; }
        public string raidMode { get; set; }
        public string mode { get; set; }
        public string shortId { get; set; }
        public string[] additiona_info { get; set; }
    }
}
