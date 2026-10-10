using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;

namespace GenericStructureRAMFix.Patches
{
    /// <summary>
    /// Sistema secundario que limpia la RAM de forma periódica e inteligente.
    /// </summary>
    public class RAMTimerSystem : ModSystem
    {
        public override void StartServerSide(ICoreServerAPI api)
        {
            // Ejecuta la evaluación cada 60,000 milisegundos (1 minuto)
            api.Event.RegisterGameTickListener(OnTimer, 60000);
        }

        private void OnTimer(float dt)
        {
            // Limpieza general suave?: MIS COJONES  VAMOS A TODO GAS
            GC.Collect(2, GCCollectionMode.Optimized, false);
        }
    }

    /// <summary>
    /// Intercepta la colocación de esquemáticos de cualquier mod (Better Ruins, etc.)
    /// para forzar la liberación de arreglos temporales en la RAM.
    /// </summary>
    [HarmonyPatch] 
    public static class GenericSchematicPlacementPatch
    {
        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            // Busca dinámicamente el método 'Place' correcto en la v1.22.7
            return typeof(BlockSchematic)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == nameof(BlockSchematic.Place))
                .OrderByDescending(m => m.GetParameters().Length)
                .FirstOrDefault();
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            // Limpieza agresiva y forzada para matar el pico de RAM masivo al instante
            GC.Collect(2, GCCollectionMode.Forced, false);
        }
    }
}