using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TarkovMonitor
{
    internal class TarkovDevRepository
    {
        public List<TarkovDev.Task> Tasks;
        public List<TarkovDev.Map> Maps;
        public List<TarkovDev.Item> Items;

        public TarkovDevRepository()
        {
            Tasks = new List<TarkovDev.Task>();
            Maps = new List<TarkovDev.Map>();
            Items = new List<TarkovDev.Item>();
        }
    }
}
