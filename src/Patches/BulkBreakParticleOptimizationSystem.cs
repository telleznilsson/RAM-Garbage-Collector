using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace GenericStructureRAMFix.Patches
{
    /// <summary>
    /// Applies an extra particle reduction only during rapid block-breaking bursts.
    /// It is deliberately separate from ParticleOptimizationSystem and does not
    /// change block removal, drops, physics, or the general particle definitions.
    /// </summary>
    public sealed class BulkBreakParticleOptimizationSystem : ModSystem
    {
        private const string HarmonyId = "ramgarbagecollector.bulkbreakparticles";

        // This multiplier is applied only while a rapid break burst is active.
        // It composes with the existing general particle optimization.
        private const float BurstQuantityScale = 0.40f;
        private const int BurstWindowMs = 220;
        private const int BurstTriggerEvents = 5;
        private const int BurstHoldMs = 300;
        private const int MaxBurstSampleLogs = 6;

        private static readonly object BurstLock = new object();
        private static long burstWindowStartMs;
        private static long burstUntilMs;
        private static int burstEventsInWindow;
        private static BulkBreakParticleOptimizationSystem activeInstance;

        [ThreadStatic]
        private static int activeBurstProviderDepth;

        private readonly HashSet<MethodBase> patchedMethods = new HashSet<MethodBase>();
        private Harmony harmony;
        private ICoreClientAPI clientApi;
        private bool realSmokeActive;
        private bool leaveWorldSubscribed;

        private int providerMethodsPatched;
        private int cubeMethodsPatched;
        private int quantityGettersPatched;
        private int blockBreakingProviderCalls;
        private int relevantCubeCalls;
        private int scaledProviderQuantities;
        private int scaledCubeCalls;
        private int burstSampleLogs;
        private int burstWindowsTriggered;
        private int runtimeWarningsLogged;

        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return forSide == EnumAppSide.Client;
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            base.StartClientSide(api);
            clientApi = api;
            activeInstance = this;
            realSmokeActive = api.ModLoader != null && api.ModLoader.IsModEnabled("realsmoke");
            harmony = new Harmony(HarmonyId);

            try
            {
                if (api.World != null)
                {
                    PatchWorldParticleSpawners(api.World);
                }
                PatchBlockBreakingQuantityGetters();

                api.Event.LeaveWorld += OnLeaveWorld;
                leaveWorldSubscribed = true;

                api.Logger.Notification(
                    "[RAM GC] Optimizador de partículas de rotura masiva inicializado: " +
                    $"métodos SpawnParticles parcheados={providerMethodsPatched}; " +
                    $"métodos SpawnCubeParticles parcheados={cubeMethodsPatched}; " +
                    $"getters de rotura parcheados={quantityGettersPatched}; " +
                    $"umbral={BurstTriggerEvents} eventos/{BurstWindowMs} ms; " +
                    $"escala adicional durante ráfagas={BurstQuantityScale:P0}; " +
                    $"Real Smoke={realSmokeActive}.");
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[RAM GC] Error al inicializar el optimizador de rotura masiva: {ex}");
            }
        }

        private void PatchWorldParticleSpawners(IWorldAccessor world)
        {
            try
            {
                Type worldType = world.GetType();
                InterfaceMapping map = worldType.GetInterfaceMap(typeof(IWorldAccessor));
                MethodInfo providerPrefix = GetPatchMethod(nameof(BlockProviderSpawnPrefix));
                MethodInfo providerFinalizer = GetPatchMethod(nameof(BlockProviderSpawnFinalizer));
                MethodInfo cubePrefix = GetPatchMethod(nameof(CubeSpawnPrefix));

                for (int i = 0; i < map.InterfaceMethods.Length; i++)
                {
                    MethodInfo interfaceMethod = map.InterfaceMethods[i];
                    MethodInfo targetMethod = map.TargetMethods[i];
                    if (targetMethod == null || targetMethod.IsAbstract || targetMethod.ContainsGenericParameters)
                    {
                        continue;
                    }

                    ParameterInfo[] parameters = interfaceMethod.GetParameters();
                    bool isProviderSpawn = interfaceMethod.Name == nameof(IWorldAccessor.SpawnParticles)
                        && parameters.Length > 0
                        && parameters[0].ParameterType == typeof(IParticlePropertiesProvider);

                    bool isCubeSpawn = interfaceMethod.Name == nameof(IWorldAccessor.SpawnCubeParticles)
                        && parameters.Length > 3
                        && parameters[3].ParameterType == typeof(int);

                    if (!isProviderSpawn && (!isCubeSpawn || realSmokeActive))
                    {
                        continue;
                    }

                    if (!patchedMethods.Add(targetMethod))
                    {
                        continue;
                    }

                    try
                    {
                        if (isProviderSpawn)
                        {
                            harmony.Patch(
                                targetMethod,
                                prefix: new HarmonyMethod(providerPrefix),
                                finalizer: new HarmonyMethod(providerFinalizer));
                            providerMethodsPatched++;
                        }
                        else
                        {
                            harmony.Patch(targetMethod, prefix: new HarmonyMethod(cubePrefix));
                            cubeMethodsPatched++;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogWarningOnce(
                            $"No se pudo parchear {targetMethod.DeclaringType?.FullName}.{targetMethod.Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogWarningOnce("No se pudieron localizar los métodos de partículas del mundo: " + ex.Message);
            }
        }

        private void PatchBlockBreakingQuantityGetters()
        {
            MethodInfo postfix = GetPatchMethod(nameof(BlockBreakingQuantityPostfix));

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(type => type != null).ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (Type type in types)
                {
                    if (type == null || type.IsAbstract || type.ContainsGenericParameters ||
                        !typeof(BlockBreakingParticleProps).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    MethodInfo getter;
                    try
                    {
                        PropertyInfo property = type.GetProperty(
                            "Quantity",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        getter = property?.GetGetMethod(true);
                    }
                    catch
                    {
                        continue;
                    }

                    if (getter == null || getter.IsAbstract || getter.ReturnType != typeof(float) ||
                        getter.DeclaringType != type || !patchedMethods.Add(getter))
                    {
                        continue;
                    }

                    try
                    {
                        harmony.Patch(getter, postfix: new HarmonyMethod(postfix));
                        quantityGettersPatched++;
                    }
                    catch (Exception ex)
                    {
                        LogWarningOnce($"No se pudo parchear Quantity de {type.FullName}: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Counts a block-breaking provider as a burst event. Once the event rate crosses
        /// the threshold, only the provider currently being spawned receives extra scaling.
        /// </summary>
        private static void BlockProviderSpawnPrefix(IParticlePropertiesProvider __0, out bool __state)
        {
            __state = false;
            BulkBreakParticleOptimizationSystem owner = activeInstance;
            if (owner == null || __0 is not BlockBreakingParticleProps breaking ||
                breaking.blockdamage == null || breaking.blockdamage.Block == null)
            {
                return;
            }

            Interlocked.Increment(ref owner.blockBreakingProviderCalls);
            if (!RegisterBurstEvent())
            {
                return;
            }

            activeBurstProviderDepth++;
            __state = true;
        }

        private static Exception BlockProviderSpawnFinalizer(Exception __exception, bool __state)
        {
            if (__state && activeBurstProviderDepth > 0)
            {
                activeBurstProviderDepth--;
            }

            return __exception;
        }

        /// <summary>
        /// Handles cube particles generated by block-breaking code and felling tools.
        /// It does not alter arbitrary particle emitters or item/creature entities.
        /// </summary>
        private static void CubeSpawnPrefix(ref int __3, object[] __args, MethodBase __originalMethod)
        {
            BulkBreakParticleOptimizationSystem owner = activeInstance;
            if (owner == null || owner.realSmokeActive || __3 <= 1 || !IsRelevantCubeSource(__args))
            {
                return;
            }

            Interlocked.Increment(ref owner.relevantCubeCalls);
            if (!RegisterBurstEvent())
            {
                return;
            }

            int originalQuantity = __3;
            __3 = Math.Max(1, (int)Math.Round(originalQuantity * BurstQuantityScale, MidpointRounding.AwayFromZero));
            Interlocked.Increment(ref owner.scaledCubeCalls);
            owner.LogBurstSample(
                $"SpawnCubeParticles: fuente={owner.DescribeCubeSource(__args)}; " +
                $"cantidad={originalQuantity}->{__3}; escala adicional={BurstQuantityScale:P0}; " +
                $"método={__originalMethod?.DeclaringType?.Name}.{__originalMethod?.Name}.");
        }

        private static void BlockBreakingQuantityPostfix(object __instance, ref float __result)
        {
            BulkBreakParticleOptimizationSystem owner = activeInstance;
            if (owner == null || activeBurstProviderDepth <= 0 || __result <= 0f)
            {
                return;
            }

            float original = __result;
            __result *= BurstQuantityScale;
            Interlocked.Increment(ref owner.scaledProviderQuantities);

            if (__instance is BlockBreakingParticleProps breaking && breaking.blockdamage?.Block?.Code != null)
            {
                owner.LogBurstSample(
                    $"Quantity de rotura: bloque={breaking.blockdamage.Block.Code}; " +
                    $"cantidad={original:0.##}->{__result:0.##}; escala adicional={BurstQuantityScale:P0}.");
            }
        }

        private static bool IsRelevantCubeSource(object[] args)
        {
            if (args == null)
            {
                return false;
            }

            foreach (object arg in args)
            {
                // The BlockPos overload is used for block voxel effects, including blocks
                // removed during a mass break. The short burst threshold avoids reacting to
                // isolated ordinary events.
                if (arg is BlockPos)
                {
                    return true;
                }

                if (arg is ItemStack stack && stack.Collectible?.Code != null)
                {
                    string path = stack.Collectible.Code.Path ?? string.Empty;
                    if (path.StartsWith("axe-", StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith("axe_", StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith("axe", StringComparison.OrdinalIgnoreCase) &&
                        path.IndexOf("felling", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void LogBurstSample(string message)
        {
            if (Interlocked.Increment(ref burstSampleLogs) > MaxBurstSampleLogs)
            {
                return;
            }

            clientApi?.Logger.Notification("[RAM GC] Ráfaga de rotura optimizada: " + message);
        }

        private static bool RegisterBurstEvent()
        {
            long now = Environment.TickCount64;
            lock (BurstLock)
            {
                if (burstWindowStartMs == 0 || now < burstWindowStartMs || now - burstWindowStartMs > BurstWindowMs)
                {
                    burstWindowStartMs = now;
                    burstEventsInWindow = 0;
                }

                burstEventsInWindow++;
                bool wasActive = now < burstUntilMs;
                if (burstEventsInWindow >= BurstTriggerEvents)
                {
                    if (!wasActive)
                    {
                        BulkBreakParticleOptimizationSystem owner = activeInstance;
                        if (owner != null)
                        {
                            Interlocked.Increment(ref owner.burstWindowsTriggered);
                        }
                    }
                    burstUntilMs = now + BurstHoldMs;
                }

                return now < burstUntilMs;
            }
        }

        private string DescribeCubeSource(object[] args)
        {
            if (args != null)
            {
                foreach (object arg in args)
                {
                    if (arg is ItemStack stack)
                    {
                        return "item:" + (stack.Collectible?.Code?.ToString() ?? "desconocido");
                    }

                    if (arg is BlockPos pos)
                    {
                        try
                        {
                            Block block = clientApi?.World?.BlockAccessor?.GetBlock(pos);
                            return "bloque:" + (block?.Code?.ToString() ?? "desconocido");
                        }
                        catch
                        {
                            return "BlockPos";
                        }
                    }
                }
            }

            return "desconocida";
        }

        private static MethodInfo GetPatchMethod(string name)
        {
            return typeof(BulkBreakParticleOptimizationSystem).GetMethod(
                name,
                BindingFlags.Static | BindingFlags.NonPublic);
        }

        private void LogWarningOnce(string message)
        {
            if (Interlocked.Exchange(ref runtimeWarningsLogged, 1) == 0)
            {
                clientApi?.Logger.Warning("[RAM GC] Optimizador de rotura masiva: " + message);
            }
        }

        private void OnLeaveWorld()
        {
            clientApi?.Logger.Notification(
                "[RAM GC] Rotura masiva — resumen: " +
                $"proveedores de rotura={Volatile.Read(ref blockBreakingProviderCalls)}; " +
                $"SpawnCubeParticles relevantes={Volatile.Read(ref relevantCubeCalls)}; " +
                $"cantidades de proveedor escaladas durante ráfagas={Volatile.Read(ref scaledProviderQuantities)}; " +
                $"llamadas cube escaladas={Volatile.Read(ref scaledCubeCalls)}; " +
                $"ráfagas detectadas={Volatile.Read(ref burstWindowsTriggered)}.");

            ResetBurstTracker();
            Interlocked.Exchange(ref blockBreakingProviderCalls, 0);
            Interlocked.Exchange(ref relevantCubeCalls, 0);
            Interlocked.Exchange(ref scaledProviderQuantities, 0);
            Interlocked.Exchange(ref scaledCubeCalls, 0);
            Interlocked.Exchange(ref burstWindowsTriggered, 0);
            Interlocked.Exchange(ref burstSampleLogs, 0);
        }

        private static void ResetBurstTracker()
        {
            lock (BurstLock)
            {
                burstWindowStartMs = 0;
                burstUntilMs = 0;
                burstEventsInWindow = 0;
            }
        }

        public override void Dispose()
        {
            if (clientApi != null && leaveWorldSubscribed)
            {
                clientApi.Event.LeaveWorld -= OnLeaveWorld;
                leaveWorldSubscribed = false;
            }

            harmony?.UnpatchAll(HarmonyId);
            harmony = null;

            if (ReferenceEquals(activeInstance, this))
            {
                activeInstance = null;
            }

            ResetBurstTracker();
            activeBurstProviderDepth = 0;
            clientApi = null;
            base.Dispose();
        }
    }
}
