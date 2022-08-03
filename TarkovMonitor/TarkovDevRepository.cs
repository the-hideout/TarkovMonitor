using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TarkovMonitor
{
    internal class TarkovDevRepository
    {
        public List<TarkovDevApi.Quest> Quests;
        public List<TarkovDevApi.Map> Maps;
        public List<TarkovDevApi.Item> Items;

        public TarkovDevRepository()
        {
            Quests = new List<TarkovDevApi.Quest>();
            Maps = new List<TarkovDevApi.Map>();
            Items = new List<TarkovDevApi.Item>();
        }
    }
}
