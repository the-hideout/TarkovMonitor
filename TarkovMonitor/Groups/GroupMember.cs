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
        public string Name { 
            get
            {
                return groupMatchRaidReady.extendedProfile.Info.Nickname;
            }
        }
        public PlayerVisualRepresentation Loadout { 
            get
            {
                return groupMatchRaidReady.extendedProfile.PlayerVisualRepresentation;
            }
        }
        private GroupMatchRaidReadyEventArgs groupMatchRaidReady { get; }

        // GroupMembers are individuals within a group with a loadout of items.
        public GroupMember(GroupMatchRaidReadyEventArgs memberReady)
        {
            groupMatchRaidReady = memberReady;
        }
    }
}
