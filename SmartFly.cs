using UnityEngine;

namespace Oxide.Plugins
{
    [Info("SmartFly", "TheVekT", "1.0.5")]
    [Description("Умный noclip через перехват нативной команды lighttoggle")]
    class SmartFly : RustPlugin
    {
        // Название нашего пермишена
        private const string PermUse = "smartfly.use";

        // Метод вызывается при загрузке плагина
        private void Init()
        {
            // Регистрируем пермишен
            permission.RegisterPermission(PermUse, this);
        }

        private object OnServerCommand(ConsoleSystem.Arg arg)
        {
            if (arg == null || arg.Connection == null || arg.Player() == null || arg.cmd == null) return null;

            if (arg.cmd.Name == "lighttoggle")
            {
                var player = arg.Player();

                // Проверяем: если игрок НЕ админ и у него НЕТ пермишена, то игнорируем
                if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, PermUse)) return null;

                var activeItem = player.GetActiveItem();

                if (activeItem != null && (activeItem.info.shortname == "hammer" || activeItem.info.shortname == "building.planner"))
                {
                    player.SendConsoleCommand("noclip");
                    
                    return false; 
                }
            }
            
            return null;
        }
    }
}