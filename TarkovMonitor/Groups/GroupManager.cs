using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TarkovMonitor.GroupLoadout
{
    public delegate void GroupMemberChanged(object source, GroupMemberChangedArgs e);

    public class GroupMemberChangedArgs : EventArgs
    {
        public GroupMemberChangedArgs()
        {
        }
    }

    class GroupManager
    {
        // A list of GroupMembers
        public Dictionary<string, GroupMember> GroupMembers { get; set; } = new();
        public bool Stale { get; set; } = false;
        
        public void RemoveGroupMember(string name)
        {
            GroupMembers.Remove(name);
            GroupMemberChanged(this, new GroupMemberChangedArgs());
        }

        public void UpdateGroupMember(GroupMatchRaidReadyEventArgs member)
        {
            if (Stale && GroupMembers.Count > 0) ClearGroup();
            GroupMembers[member.extendedProfile.Info.Nickname] = new(member);
            GroupMemberChanged(this, new GroupMemberChangedArgs());
        }

        // Clear the GroupMember list
        public void ClearGroup()
        {
            GroupMembers.Clear();
            GroupMemberChanged(this, new GroupMemberChangedArgs());
        }

        // Event for when a group member is added or removed from the list
        public event GroupMemberChanged GroupMemberChanged = delegate { };
    }
}
