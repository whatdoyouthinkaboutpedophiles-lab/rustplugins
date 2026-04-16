using UnityEngine;

namespace Oxide.Plugins
{
    [Info("SmartRemover", "TheVekT", "1.0.0")]
    [Description("Удаление построек взглядом на R (Reload) с киянкой")]
    class SmartRemover : RustPlugin
    {
        private const string PermUse = "smartremover.use";

        private void Init()
        {
            permission.RegisterPermission(PermUse, this);
        }

        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null) return;

            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, PermUse)) return;

            if (input.WasJustPressed(BUTTON.RELOAD))
            {
                var activeItem = player.GetActiveItem();
                
                if (activeItem != null && activeItem.info.shortname == "hammer")
                {
                    int layerMask = Rust.Layers.Mask.Construction | Rust.Layers.Mask.Deployed;
                    
                    if (Physics.Raycast(player.eyes.HeadRay(), out RaycastHit hit, 300f, layerMask))
                    {
                        var entity = hit.GetEntity();
                        if (entity != null)
                        {
                            string prefabName = entity.ShortPrefabName;
                            
                            entity.Kill(BaseNetworkable.DestroyMode.Gib);
                        }
                    }
                }
            }
        }
    }
}