using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TarkovMonitor;

namespace TarkovMonitor.GroupLoadout
{
    class GroupMember
    {
        public string Name { get; set; }
        public PlayerLoadout Loadout { get; set; }
        
        // GroupMembers are individuals within a group with a loadout of items.
        public GroupMember(string name, PlayerLoadout loadout)
        {
            Name = name;
            Loadout = loadout;
        }
    }
}
