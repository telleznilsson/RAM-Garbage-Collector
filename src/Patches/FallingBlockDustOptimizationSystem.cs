using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Client;

namespace GenericStructureRAMFix.Patches
{
    /// <summary>
    /// Client-only reduction for the expanding dust emitted while sand/snow blocks fall.
    /// This is separate from block-breaking particles and does not alter falling physics.
    ///
    /// In Vintage Story's FallingBlockParticlesModSystem, the expanding Quad dust quantity
    /// is multiplied by EntityBlockFalling.dustIntensity. We set that instance field to 25%
    /// of its original watched value after Initialize/FromBytes, only for sand/snow block codes.
    /// </summary>
    public sealed class FallingBlockDustOptimizationSystem : ModSystem
    {
        private const string HarmonyId = "ramgarbagecollector.fallingdust25";
        private const float SandSnowDustScale = 0.25f;
        private const string FallingEntityTypeName = "Vintagestory.GameContent.EntityBlockFalling";

        private static FallingBlockDustOptimizationSystem activeInstance;
        private static readonly object OriginalValuesLock = new object();
        private static readonly ConditionalWeakTable<object, OriginalDustValue> OriginalValues =
            new ConditionalWeakTable<object, OriginalDustValue>();
        private static readonly HashSet<string> LoggedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private Harmony harmony;
        private ICoreClientAPI clientApi;
        private Type fallingEntityType;
        private FieldInfo dustIntensityField;
        private FieldInfo blockCodeField;
        private int initializePatches;
        private int fromBytesPatches;

        private sealed class OriginalDustValue
        {
            public float Value;

            public OriginalDustValue(float value)
            {
                Value = value;
            }
        }

        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return forSide == EnumAppSide.Client;
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            base.StartClientSide(api);
            clientApi = api;
            activeInstance = this;
            harmony = new Harmony(HarmonyId);

            try
            {
                fallingEntityType = FindRuntimeType(FallingEntityTypeName);
                if (fallingEntityType == null)
                {
                    api.Logger.Warning(
                        "[RAM GC] Optimizador de polvo de caída no aplicado: no se encontró " + FallingEntityTypeName + ".");
                    return;
                }

                // Current VS source stores the original per-entity multiplier in this internal field.
                dustIntensityField = fallingEntityType.GetField(
                    "dustIntensity",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                blockCodeField = fallingEntityType.GetField(
                    "blockCode",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                MethodInfo initialize = fallingEntityType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(method =>
                    {
                        if (method.Name != "Initialize") return false;
                        ParameterInfo[] p = method.GetParameters();
                        return p.Length == 3 && p[2].ParameterType == typeof(long);
                    });

                MethodInfo fromBytes = fallingEntityType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(method =>
                    {
                        if (method.Name != "FromBytes") return false;
                        ParameterInfo[] p = method.GetParameters();
                        return p.Length == 2 && p[0].ParameterType == typeof(BinaryReader) && p[1].ParameterType == typeof(bool);
                    });

                if (dustIntensityField == null)
                {
                    api.Logger.Warning(
                        "[RAM GC] Optimizador de polvo de caída no aplicado: EntityBlockFalling.dustIntensity no existe en esta versión.");
                    return;
                }

                if (initialize != null)
                {
                    harmony.Patch(
                        initialize,
                        postfix: new HarmonyMethod(GetPatchMethod(nameof(AfterFallingEntityInitialize))));
                    initializePatches++;
                }

                if (fromBytes != null)
                {
                    harmony.Patch(
                        fromBytes,
                        postfix: new HarmonyMethod(GetPatchMethod(nameof(AfterFallingEntityFromBytes))));
                    fromBytesPatches++;
                }

                api.Logger.Notification(
                    "[RAM GC] Optimizador de polvo de bloques caídos inicializado: " +
                    $"Initialize parcheado={initializePatches}; FromBytes parcheado={fromBytesPatches}; " +
                    $"campo dustIntensity encontrado={dustIntensityField != null}; " +
                    $"factor para arena/nieve={SandSnowDustScale:P0}; solo visual del cliente.");

                if (initializePatches == 0 && fromBytesPatches == 0)
                {
                    api.Logger.Warning(
                        "[RAM GC] Optimizador de polvo de caída: no se pudo parchear Initialize ni FromBytes; no se aplicará ninguna reducción.");
                }
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[RAM GC] Error al inicializar el optimizador de polvo de caída: {ex}");
            }
        }

        private static Type FindRuntimeType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = assembly.GetType(fullName, false);
                    if (type != null) return type;
                }
                catch
                {
                    // A single unrelated assembly must not prevent type discovery in the others.
                }
            }

            return null;
        }

        private static MethodInfo GetPatchMethod(string name)
        {
            return typeof(FallingBlockDustOptimizationSystem).GetMethod(
                name,
                BindingFlags.Static | BindingFlags.NonPublic);
        }

        private static void AfterFallingEntityInitialize(object __instance)
        {
            activeInstance?.TryReduceDustIntensity(__instance, "Initialize");
        }

        private static void AfterFallingEntityFromBytes(object __instance)
        {
            activeInstance?.TryReduceDustIntensity(__instance, "FromBytes");
        }

        private void TryReduceDustIntensity(object entity, string lifecycleMethod)
        {
            if (entity == null || dustIntensityField == null)
            {
                return;
            }

            try
            {
                string blockCode = GetFallingBlockCode(entity);
                if (string.IsNullOrWhiteSpace(blockCode) || !IsSandOrSnow(blockCode))
                {
                    return;
                }

                float current = Convert.ToSingle(dustIntensityField.GetValue(entity));
                float original = GetOriginalDustIntensity(entity, current);
                float reduced = original * SandSnowDustScale;
                dustIntensityField.SetValue(entity, reduced);

                bool shouldLog = false;
                lock (LoggedCodes)
                {
                    if (LoggedCodes.Count < 32)
                    {
                        shouldLog = LoggedCodes.Add(blockCode);
                    }
                }

                if (shouldLog)
                {
                    clientApi?.Logger.Notification(
                        $"[RAM GC] Polvo expansivo de bloque caído reducido: bloque={blockCode}; " +
                        $"dustIntensity={original:0.###}->{reduced:0.###}; escala={SandSnowDustScale:P0}; " +
                        $"hook={lifecycleMethod}; física intacta.");
                }
            }
            catch (Exception ex)
            {
                clientApi?.Logger.Warning(
                    $"[RAM GC] No se pudo reducir el polvo expansivo de un bloque caído ({lifecycleMethod}): {ex.Message}");
            }
        }

        private float GetOriginalDustIntensity(object entity, float currentValue)
        {
            // Prefer the synced, unscaled source value so running both lifecycle hooks never
            // compounds the 25% multiplier (for example, 1 -> .25 -> .0625).
            if (TryReadWatchedDustIntensity(entity, out float watchedValue))
            {
                lock (OriginalValuesLock)
                {
                    if (OriginalValues.TryGetValue(entity, out OriginalDustValue oldValue))
                    {
                        oldValue.Value = watchedValue;
                    }
                    else
                    {
                        OriginalValues.Add(entity, new OriginalDustValue(watchedValue));
                    }
                }

                return watchedValue;
            }

            lock (OriginalValuesLock)
            {
                if (OriginalValues.TryGetValue(entity, out OriginalDustValue oldValue))
                {
                    return oldValue.Value;
                }

                OriginalValues.Add(entity, new OriginalDustValue(currentValue));
                return currentValue;
            }
        }

        private bool TryReadWatchedDustIntensity(object entity, out float value)
        {
            value = 0f;
            try
            {
                object watched = ReadMember(entity, "WatchedAttributes");
                if (watched == null) return false;

                Type watchedType = watched.GetType();
                MethodInfo hasAttribute = watchedType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(method => method.Name == "HasAttribute"
                        && method.GetParameters().Length == 1
                        && method.GetParameters()[0].ParameterType == typeof(string));

                if (hasAttribute != null && !(bool)hasAttribute.Invoke(watched, new object[] { "dustIntensity" }))
                {
                    return false;
                }

                MethodInfo getFloat = watchedType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(method => method.Name == "GetFloat"
                        && method.GetParameters().Length >= 1
                        && method.GetParameters()[0].ParameterType == typeof(string)
                        && (method.GetParameters().Length == 1
                            || method.GetParameters().Length == 2
                                && method.GetParameters()[1].ParameterType == typeof(float)));

                if (getFloat == null) return false;

                object result = getFloat.GetParameters().Length == 1
                    ? getFloat.Invoke(watched, new object[] { "dustIntensity" })
                    : getFloat.Invoke(watched, new object[] { "dustIntensity", 0f });

                if (result == null) return false;
                value = Convert.ToSingle(result);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private string GetFallingBlockCode(object entity)
        {
            object code = null;
            if (blockCodeField != null)
            {
                code = blockCodeField.GetValue(entity);
            }

            if (code == null)
            {
                try
                {
                    object block = ReadMember(entity, "Block");
                    code = block == null ? null : ReadMember(block, "Code");
                }
                catch
                {
                    // blockCode may not have arrived yet during an early Initialize call.
                }
            }

            if (code == null) return null;
            string domain = ReadMember(code, "Domain") as string;
            string path = ReadMember(code, "Path") as string;
            if (string.IsNullOrEmpty(path)) return code.ToString();
            return string.IsNullOrEmpty(domain) ? path : domain + ":" + path;
        }

        private static object ReadMember(object instance, string name)
        {
            if (instance == null) return null;
            Type type = instance.GetType();
            PropertyInfo property = type.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                try { return property.GetValue(instance); } catch { }
            }

            FieldInfo field = type.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                try { return field.GetValue(instance); } catch { }
            }

            // Search base classes too: WatchedAttributes is inherited from Entity.
            Type baseType = type.BaseType;
            while (baseType != null)
            {
                property = baseType.GetProperty(
                    name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    try { return property.GetValue(instance); } catch { }
                }

                field = baseType.GetField(
                    name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    try { return field.GetValue(instance); } catch { }
                }

                baseType = baseType.BaseType;
            }

            return null;
        }

        private static bool IsSandOrSnow(string blockCode)
        {
            int colon = blockCode.IndexOf(':');
            string path = colon >= 0 && colon + 1 < blockCode.Length
                ? blockCode.Substring(colon + 1)
                : blockCode;

            return path.IndexOf("sand", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("snow", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public override void Dispose()
        {
            try
            {
                harmony?.UnpatchAll(HarmonyId);
            }
            catch (Exception ex)
            {
                clientApi?.Logger.Warning($"[RAM GC] Error al retirar el optimizador de polvo de caída: {ex.Message}");
            }

            if (ReferenceEquals(activeInstance, this))
            {
                activeInstance = null;
            }

            base.Dispose();
        }
    }
}
