using UnityEngine;

namespace Oxide.Plugins
{
    [Info("AutoUpkeepTC", "TheVekT", "1.0.0")]
    [Description("Автоматическое заполнение шкафа ресурсами при установке")]
    class AutoUpkeepTC : RustPlugin
    {
        private void OnEntityBuilt(Planner plan, GameObject go)
        {
            var player = plan?.GetOwnerPlayer();
            if (player == null) return;

            var tc = go.GetComponent<BuildingPrivlidge>();
            if (tc == null) return;

            FillItem(tc.inventory, "wood", 1000000);
            FillItem(tc.inventory, "stones", 1000000);
            FillItem(tc.inventory, "metal.fragments", 1000000);
            FillItem(tc.inventory, "metal.refined", 1000000);
        }

        private void FillItem(ItemContainer container, string shortname, int amount)
        {
            Item item = ItemManager.CreateByName(shortname, amount);
            if (item != null && !item.MoveToContainer(container))
            {
                item.Remove();
            }
        }
    }
}