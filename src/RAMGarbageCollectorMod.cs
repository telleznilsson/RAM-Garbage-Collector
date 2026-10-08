using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using System;

namespace RAMGarbageCollector
{
    public class RAMGarbageCollectorMod : ModSystem
    {
        private Harmony harmony;
        public const string HarmonyId = "ramgarbagecollector.mod";

        // Obliga al motor a ignorar este archivo en el servidor y cargarlo solo en el cliente
        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return true;
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            base.StartClientSide(api);

            // Registro nativo de la API para recolección de basura al salir/cambiar de mundo
            api.Event.LeaveWorld += OnLeaveWorld;

            try
            {
                harmony = new Harmony(HarmonyId);
                harmony.PatchAll();
                api.Logger.Notification("[RAM GC] Parches de optimización aplicados correctamente.");
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[RAM GC] Error crítico al aplicar parches: {ex}");
            }
        }

        private void OnLeaveWorld()
        {
            // Ejecución de recolección de basura preventiva en el Heap
            GC.Collect(2, GCCollectionMode.Optimized, false);
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll(HarmonyId);
            base.Dispose();
        }
    }
}