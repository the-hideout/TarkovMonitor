using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TarkovMonitor
{
    internal class TarkovDevRepository
    {
        public List<TarkovDevApi.Task> Tasks;
        public List<TarkovDevApi.Map> Maps;
        public List<TarkovDevApi.Item> Items;

        public TarkovDevRepository()
        {
            Tasks = new List<TarkovDevApi.Task>();
            Maps = new List<TarkovDevApi.Map>();
            Items = new List<TarkovDevApi.Item>();
        }
    }
}
