using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace GenericStructureRAMFix
{
    public class GenericStructureFixSystem : ModSystem
    {
        public const string HarmonyID = "com.generic.structureramfix";
        private Harmony harmony;

        public override bool ShouldLoad(EnumAppSide side)
        {
            return side == EnumAppSide.Server;
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            base.StartServerSide(api);

            if (!Harmony.HasAnyPatches(HarmonyID))
            {
                harmony = new Harmony(HarmonyID);
                harmony.PatchAll();
                api.Logger.Notification("[GenericStructureRAMFix] Parches de optimización de RAM cargados para 1.22.x.");
            }
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll(HarmonyID);
            base.Dispose();
        }
    }
}