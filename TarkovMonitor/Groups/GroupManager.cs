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
        public Dictionary<string, GroupMember> GroupMembers { get; set; }
        public bool Stale { get; set; }

        // Constructor
        public GroupManager()
        {
            GroupMembers = new Dictionary<string, GroupMember>();
            Stale = false;
        }

        // Add a new GroupMember to the list
        public void AddGroupMember(GroupMember member)
        {
            GroupMembers.Add(member.Name, member);
            GroupMemberChanged(this, new GroupMemberChangedArgs());
        }

        // Remove a GroupMember from the list
        public void RemoveGroupMember(GroupMember member)
        {
            GroupMembers.Remove(member.Name);
            GroupMemberChanged(this, new GroupMemberChangedArgs());
        }
        
        public void RemoveGroupMember(string name)
        {
            GroupMembers.Remove(name);
            GroupMemberChanged(this, new GroupMemberChangedArgs());
        }

        public void UpdateGroupMember(string name, GroupMember member)
        {
            if (Stale && GroupMembers.Count > 0) ClearGroup();
            GroupMember? currentMember = GroupMembers.FirstOrDefault(m => m.Key == name).Value;
            if(currentMember != null)
            {
                currentMember = member;
                GroupMemberChanged(this, new GroupMemberChangedArgs());
            }
            else
            {
                AddGroupMember(member);
            }
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
