using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace GenericStructureRAMFix.Patches
{
    /// <summary>
    /// Client-side particle quantity reduction. This system is intentionally isolated
    /// from RAMGarbageCollectorMod.cs and StructureMemoryPatch.cs.
    ///
    /// It scales data-driven AdvancedParticleProperties once, scales runtime
    /// SimpleParticleProperties once, reduces direct IWorldAccessor particle-spawn
    /// quantities, and halves dynamic quantities from CollectibleParticleProperties.
    /// </summary>
    public sealed class ParticleOptimizationSystem : ModSystem
    {
        private const string HarmonyId = "ramgarbagecollector.particles50";
        private const float QuantityScale = 0.5f;
        private const string RealSmokeDomain = "realsmoke";

        private static readonly object ProcessedLock = new object();
        private static readonly ConditionalWeakTable<object, ProcessedMarker> ProcessedProviders =
            new ConditionalWeakTable<object, ProcessedMarker>();

        [ThreadStatic]
        private static int spawnInterceptDepth;

        private static ParticleOptimizationSystem activeInstance;
        private static bool realSmokeActive;
        private static int advancedPropertiesScaled;
        private static int simplePropertiesScaled;
        private static int runtimeProvidersExcluded;
        private static int runtimeErrorsLogged;
        private static int providerSpawnCalls;
        private static int directQuantitySpawnCalls;
        private static int cubeQuantitySpawnCalls;
        private static int dynamicQuantityGetterCalls;
        private static int directSpawnSampleLogs;
        private static int cubeSpawnSampleLogs;
        private static readonly HashSet<string> RuntimeTypesLogged = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> DynamicQuantityValueContextsLogged = new HashSet<string>(StringComparer.Ordinal);

        private readonly HashSet<MethodBase> patchedMethods = new HashSet<MethodBase>();
        private Harmony particleHarmony;
        private ICoreClientAPI clientApi;
        private bool definitionsScanned;
        private bool waitingForClientAssets;
        private bool runtimeStatsLogged;
        private int propertiesSeen;
        private int excludedCollectibles;
        private int dynamicQuantityGettersPatched;
        private int worldSpawnMethodsPatched;

        private sealed class ProcessedMarker
        {
            public readonly bool Excluded;

            public ProcessedMarker(bool excluded)
            {
                Excluded = excluded;
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
            realSmokeActive = api.ModLoader.IsModEnabled(RealSmokeDomain);
            particleHarmony = new Harmony(HarmonyId);

            try
            {
                PatchWorldParticleSpawners(api.World);
                PatchDynamicCollectibleParticleQuantities();

                // Collectible particle definitions must be scanned only after the client has
                // received and registered the world's blocks/items. AssetsLoaded() can happen
                // before StartClientSide(), so scanning there while clientApi is null silently
                // skipped the entire static pass in the previous build.
                api.Event.BlockTexturesLoaded += OnClientAssetsReady;
                api.Event.LevelFinalize += OnClientAssetsReady;
                api.Event.LeaveWorld += LogRuntimeStats;
                waitingForClientAssets = true;

                api.Logger.Notification(
                    "[RAM GC] Optimizador de particulas al 50% inicializado; " +
                    $"metodos de spawn parcheados={worldSpawnMethodsPatched}; " +
                    $"getters dinamicos parcheados={dynamicQuantityGettersPatched}; " +
                    $"Real Smoke activo={realSmokeActive}; esperando assets del cliente para escanear definiciones.");
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[RAM GC] Error al inicializar el optimizador de particulas: {ex}");
            }
        }

        private void OnClientAssetsReady()
        {
            if (definitionsScanned || clientApi == null)
            {
                return;
            }

            try
            {
                ScanCollectibleParticleDefinitions(clientApi.World);
                definitionsScanned = true;
                waitingForClientAssets = false;

                clientApi.Event.BlockTexturesLoaded -= OnClientAssetsReady;
                clientApi.Event.LevelFinalize -= OnClientAssetsReady;

                clientApi.Logger.Notification(
                    "[RAM GC] Optimizador de particulas aplicado: cantidad al 50% del original; " +
                    $"propiedades avanzadas ajustadas={Volatile.Read(ref advancedPropertiesScaled)}; " +
                    $"propiedades simples ajustadas={Volatile.Read(ref simplePropertiesScaled)}; " +
                    $"propiedades revisadas={propertiesSeen}; " +
                    $"emisores excluidos={excludedCollectibles}; " +
                    $"proveedores omitidos en runtime={Volatile.Read(ref runtimeProvidersExcluded)}; " +
                    $"metodos de spawn={worldSpawnMethodsPatched}; " +
                    $"getters dinamicos={dynamicQuantityGettersPatched}; " +
                    $"Real Smoke={realSmokeActive}.");
            }
            catch (Exception ex)
            {
                // Keep the second event subscribed as a fallback if the first event fires
                // while registries are still being finalized.
                clientApi.Logger.Error($"[RAM GC] Error al revisar las definiciones de particulas: {ex}");
            }
        }

        private void LogRuntimeStats()
        {
            if (runtimeStatsLogged || clientApi == null)
            {
                return;
            }

            runtimeStatsLogged = true;
            clientApi.Logger.Notification(
                "[RAM GC] Diagnostico de particulas antes de salir del mundo: " +
                $"spawns proveedor={Volatile.Read(ref providerSpawnCalls)}; " +
                $"spawns cantidad float={Volatile.Read(ref directQuantitySpawnCalls)}; " +
                $"spawns cubo={Volatile.Read(ref cubeQuantitySpawnCalls)}; " +
                $"llamadas getter Quantity={Volatile.Read(ref dynamicQuantityGetterCalls)}; " +
                $"tipos runtime registrados={RuntimeTypesLogged.Count}; " +
                $"escaneo definiciones completado={definitionsScanned}; " +
                $"esperando assets={waitingForClientAssets}.");
        }

        private void PatchWorldParticleSpawners(IWorldAccessor world)
        {
            Type worldType = world.GetType();
            InterfaceMapping map = worldType.GetInterfaceMap(typeof(IWorldAccessor));

            MethodInfo providerPrefix = GetPatchMethod(nameof(ProviderSpawnPrefix));
            MethodInfo directQuantityPrefix = GetPatchMethod(nameof(DirectQuantitySpawnPrefix));
            MethodInfo cubeQuantityPrefix = GetPatchMethod(nameof(CubeQuantitySpawnPrefix));
            MethodInfo finalizer = GetPatchMethod(nameof(SpawnFinalizer));

            for (int i = 0; i < map.InterfaceMethods.Length; i++)
            {
                MethodInfo interfaceMethod = map.InterfaceMethods[i];
                MethodInfo targetMethod = map.TargetMethods[i];

                if (targetMethod == null || targetMethod.IsAbstract || targetMethod.ContainsGenericParameters)
                {
                    continue;
                }

                MethodInfo prefix = null;
                ParameterInfo[] parameters = interfaceMethod.GetParameters();

                if (interfaceMethod.Name == nameof(IWorldAccessor.SpawnParticles) && parameters.Length > 0)
                {
                    if (parameters[0].ParameterType == typeof(IParticlePropertiesProvider))
                    {
                        prefix = providerPrefix;
                    }
                    else if (parameters[0].ParameterType == typeof(float))
                    {
                        // Direct numeric particle calls cannot be attributed to an emitter.
                        // When Real Smoke is installed, leave these calls alone for safety.
                        if (!realSmokeActive)
                        {
                            prefix = directQuantityPrefix;
                        }
                    }
                }
                else if (interfaceMethod.Name == nameof(IWorldAccessor.SpawnCubeParticles))
                {
                    // These methods use an integer quantity at argument index 3.
                    // Real Smoke can replace vanilla emitters without exposing their source.
                    if (!realSmokeActive && parameters.Length > 3 && parameters[3].ParameterType == typeof(int))
                    {
                        prefix = cubeQuantityPrefix;
                    }
                }

                if (prefix == null || !patchedMethods.Add(targetMethod))
                {
                    continue;
                }

                try
                {
                    particleHarmony.Patch(
                        targetMethod,
                        prefix: new HarmonyMethod(prefix),
                        finalizer: new HarmonyMethod(finalizer));
                    worldSpawnMethodsPatched++;
                }
                catch (Exception ex)
                {
                    clientApi.Logger.Warning(
                        $"[RAM GC] No se pudo parchear el metodo de particulas {targetMethod.DeclaringType?.FullName}.{targetMethod.Name}: {ex.Message}");
                }
            }
        }

        private void PatchDynamicCollectibleParticleQuantities()
        {
            MethodInfo postfix = GetPatchMethod(nameof(DynamicCollectibleQuantityPostfix));
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

            foreach (Assembly assembly in assemblies)
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
                    if (type == null || type.IsAbstract || type.ContainsGenericParameters)
                    {
                        continue;
                    }

                    if (!typeof(CollectibleParticleProperties).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    // Do not halve providers owned by Real Smoke or recognizable smoke emitters
                    // when that mod is active.
                    if (IsRealSmokeType(type) || (realSmokeActive && IsRecognizableSmokeType(type)))
                    {
                        continue;
                    }

                    MethodInfo getter;
                    try
                    {
                        PropertyInfo quantityProperty = type.GetProperty(
                            "Quantity",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        getter = quantityProperty?.GetGetMethod(true);
                    }
                    catch
                    {
                        // A third-party type with a hidden/ambiguous Quantity property should
                        // not prevent the remaining vanilla and mod particle providers being patched.
                        continue;
                    }

                    if (getter == null || getter.IsAbstract || getter.ReturnType != typeof(float))
                    {
                        continue;
                    }

                    // Patch only getters actually declared on this type; inherited getters are
                    // picked up from their declaring type and are deduplicated below.
                    if (getter.DeclaringType != type || !patchedMethods.Add(getter))
                    {
                        continue;
                    }

                    try
                    {
                        particleHarmony.Patch(getter, postfix: new HarmonyMethod(postfix));
                        dynamicQuantityGettersPatched++;
                    }
                    catch (Exception ex)
                    {
                        clientApi.Logger.Warning(
                            $"[RAM GC] No se pudo parchear la cantidad de particulas de {type.FullName}: {ex.Message}");
                    }
                }
            }
        }

        private void ScanCollectibleParticleDefinitions(IWorldAccessor world)
        {
            foreach (CollectibleObject collectible in world.Collectibles.ToArray())
            {
                if (collectible == null || collectible.ParticleProperties == null || collectible.ParticleProperties.Length == 0)
                {
                    continue;
                }

                AssetLocation code = collectible.Code;
                bool exclude = IsExcludedEmitterCode(code);

                if (exclude)
                {
                    excludedCollectibles++;
                }

                foreach (AdvancedParticleProperties properties in collectible.ParticleProperties)
                {
                    if (properties == null)
                    {
                        continue;
                    }

                    propertiesSeen++;

                    if (exclude)
                    {
                        MarkExcludedTree(properties);
                    }
                    else
                    {
                        ProcessProvider(properties, code);
                    }
                }
            }
        }

        private static void ProviderSpawnPrefix(IParticlePropertiesProvider __0, out int __state)
        {
            __state = EnterSpawnScope();
            if (__state != 0 || __0 == null)
            {
                return;
            }

            Interlocked.Increment(ref providerSpawnCalls);
            LogRuntimeTypeOnce("SpawnParticles(provider)", __0);

            try
            {
                ProcessProvider(__0, null);
            }
            catch (Exception ex)
            {
                LogRuntimeErrorOnce("No se pudo ajustar un proveedor de particulas en tiempo de ejecucion", ex);
            }
        }

        private static void DirectQuantitySpawnPrefix(ref float __0, object[] __args, MethodBase __originalMethod, out int __state)
        {
            __state = EnterSpawnScope();
            if (__state != 0)
            {
                return;
            }

            Interlocked.Increment(ref directQuantitySpawnCalls);
            float originalQuantity = __0;
            if (!realSmokeActive && __0 > 0f)
            {
                __0 *= QuantityScale;
            }

            if (Interlocked.Increment(ref directSpawnSampleLogs) <= 8)
            {
                activeInstance?.clientApi?.Logger.Notification(
                    $"[RAM GC] Diagnostico SpawnParticles(float): metodo={__originalMethod?.DeclaringType?.FullName}.{__originalMethod?.Name}; cantidad={originalQuantity:0.##}->{__0:0.##}; Real Smoke={realSmokeActive}.");
            }
        }

        private static void CubeQuantitySpawnPrefix(ref int __3, object[] __args, MethodBase __originalMethod, out int __state)
        {
            __state = EnterSpawnScope();
            if (__state != 0)
            {
                return;
            }

            Interlocked.Increment(ref cubeQuantitySpawnCalls);
            int originalQuantity = __3;
            if (!realSmokeActive && __3 > 1)
            {
                __3 = Math.Max(1, (int)Math.Round(__3 * QuantityScale, MidpointRounding.AwayFromZero));
            }

            if (Interlocked.Increment(ref cubeSpawnSampleLogs) <= 12)
            {
                string source = DescribeCubeParticleSource(__args);
                activeInstance?.clientApi?.Logger.Notification(
                    $"[RAM GC] Diagnostico SpawnCubeParticles: metodo={__originalMethod?.DeclaringType?.FullName}.{__originalMethod?.Name}; fuente={source}; cantidad={originalQuantity}->{__3}; Real Smoke={realSmokeActive}.");
            }
        }

        private static string DescribeCubeParticleSource(object[] args)
        {
            if (args == null)
            {
                return "desconocida";
            }

            try
            {
                if (args.Length > 0 && args[0] is BlockPos blockPos && activeInstance?.clientApi?.World != null)
                {
                    Block block = activeInstance.clientApi.World.BlockAccessor.GetBlock(blockPos);
                    return "bloque:" + (block?.Code?.ToString() ?? "desconocido");
                }

                if (args.Length > 1 && args[1] is ItemStack stack)
                {
                    return "item:" + (stack.Collectible?.Code?.ToString() ?? "desconocido");
                }
            }
            catch
            {
                // Diagnostics must never interfere with particle emission.
            }

            return args.Length > 0 ? "arg0:" + (args[0]?.GetType().Name ?? "null") : "sin-argumentos";
        }

        private static Exception SpawnFinalizer(Exception __exception, int __state)
        {
            spawnInterceptDepth = __state;
            return __exception;
        }

        private static void DynamicCollectibleQuantityPostfix(object __instance, ref float __result)
        {
            Interlocked.Increment(ref dynamicQuantityGetterCalls);
            LogRuntimeTypeOnce("Quantity getter", __instance);

            // Some block-breaking particle providers are created dynamically. When Real Smoke
            // is active, leave recognizable smoke-related blocks untouched as well.
            if (realSmokeActive && __instance is BlockBreakingParticleProps breaking
                && breaking.blockdamage != null
                && breaking.blockdamage.Block != null
                && IsExcludedEmitterCode(breaking.blockdamage.Block.Code))
            {
                return;
            }

            float originalQuantity = __result;
            if (__result > 0f)
            {
                __result *= QuantityScale;
            }

            // Log one before/after sample per block/provider context. This lets us verify that
            // sand and snow breaking providers are not only intercepted, but actually scaled.
            if (__instance is BlockBreakingParticleProps blockBreaking
                && blockBreaking.blockdamage?.Block?.Code != null
                && activeInstance?.clientApi != null)
            {
                string typeName = __instance.GetType().FullName ?? __instance.GetType().Name;
                string blockCode = blockBreaking.blockdamage.Block.Code.ToString();
                string key = typeName + "|" + blockCode;
                bool shouldLog;

                lock (ProcessedLock)
                {
                    shouldLog = DynamicQuantityValueContextsLogged.Count < 40
                        && DynamicQuantityValueContextsLogged.Add(key);
                }

                if (shouldLog)
                {
                    activeInstance.clientApi.Logger.Notification(
                        $"[RAM GC] Cantidad dinamica ajustada: tipo={typeName}; bloque={blockCode}; " +
                        $"cantidad={originalQuantity:0.##}->{__result:0.##}; escala={QuantityScale:P0}; " +
                        $"Real Smoke={realSmokeActive}.");
                }
            }
        }

        private static void LogRuntimeTypeOnce(string route, object instance)
        {
            if (instance == null || activeInstance?.clientApi == null)
            {
                return;
            }

            string typeName = instance.GetType().FullName ?? instance.GetType().Name;
            string context = string.Empty;

            if (instance is BlockBreakingParticleProps breaking && breaking.blockdamage?.Block != null)
            {
                context = "; bloque=" + (breaking.blockdamage.Block.Code?.ToString() ?? "desconocido");
            }
            else if (instance is AdvancedParticleProperties advanced && advanced.block != null)
            {
                context = "; bloque=" + (advanced.block.Code?.ToString() ?? "desconocido");
            }

            string key = route + "|" + typeName + context;
            bool shouldLog;

            lock (ProcessedLock)
            {
                // Keep diagnostics useful without flooding the log in particle-heavy scenes.
                shouldLog = RuntimeTypesLogged.Count < 20 && RuntimeTypesLogged.Add(key);
            }

            if (shouldLog)
            {
                activeInstance.clientApi.Logger.Notification(
                    $"[RAM GC] Diagnostico de particulas runtime: ruta={route}; tipo={typeName}{context}.");
            }
        }

        private static int EnterSpawnScope()
        {
            int previous = spawnInterceptDepth;
            spawnInterceptDepth = previous + 1;
            return previous;
        }

        private static void ProcessProvider(IParticlePropertiesProvider provider, AssetLocation ownerCode)
        {
            if (provider == null)
            {
                return;
            }

            if (IsExcludedProvider(provider, ownerCode))
            {
                MarkExcludedTree(provider);
                return;
            }

            if (!TryMarkProcessed(provider, excluded: false))
            {
                return;
            }

            if (provider is AdvancedParticleProperties advanced)
            {
                ScaleNatFloat(advanced.Quantity);
                Interlocked.Increment(ref advancedPropertiesScaled);
            }
            else if (provider is SimpleParticleProperties simple)
            {
                // With Real Smoke active, a SimpleParticleProperties instance usually carries
                // no reliable owner/domain. We conservatively avoid changing it rather than risk
                // reducing Real Smoke's runtime-created smoke.
                if (realSmokeActive)
                {
                    Interlocked.Increment(ref runtimeProvidersExcluded);
                    MarkChildrenExcluded(provider);
                    return;
                }

                simple.MinQuantity *= QuantityScale;
                simple.AddQuantity *= QuantityScale;
                Interlocked.Increment(ref simplePropertiesScaled);
            }

            ProcessChildren(provider, ownerCode);
        }

        private static void ScaleNatFloat(NatFloat quantity)
        {
            if (quantity == null)
            {
                return;
            }

            quantity.avg *= QuantityScale;
            quantity.var *= QuantityScale;
            quantity.offset *= QuantityScale;
        }

        private static bool IsExcludedProvider(IParticlePropertiesProvider provider, AssetLocation ownerCode)
        {
            if (IsExcludedEmitterCode(ownerCode))
            {
                return true;
            }

            if (provider is AdvancedParticleProperties advanced && advanced.block != null && IsExcludedEmitterCode(advanced.block.Code))
            {
                return true;
            }

            if (provider is SimpleParticleProperties simple)
            {
                if (simple.ColorByBlock != null && IsExcludedEmitterCode(simple.ColorByBlock.Code))
                {
                    return true;
                }

                if (simple.ColorByItem != null && IsExcludedEmitterCode(simple.ColorByItem.Code))
                {
                    return true;
                }

                if (realSmokeActive)
                {
                    return true;
                }
            }

            return IsRealSmokeType(provider.GetType()) || (realSmokeActive && IsRecognizableSmokeType(provider.GetType()));
        }

        private static bool IsExcludedEmitterCode(AssetLocation code)
        {
            if (code == null)
            {
                return false;
            }

            if (string.Equals(code.Domain, RealSmokeDomain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!realSmokeActive || string.IsNullOrEmpty(code.Path))
            {
                return false;
            }

            string path = code.Path.ToLowerInvariant();
            return path.Contains("smoke")
                || path.Contains("chimney")
                || path.Contains("charcoalpit")
                || path.Contains("charcoal-pit")
                || path.Contains("torch");
        }

        private static bool IsRealSmokeType(Type type)
        {
            string assemblyName = type.Assembly.GetName().Name ?? string.Empty;
            string fullName = type.FullName ?? type.Name;
            return assemblyName.IndexOf(RealSmokeDomain, StringComparison.OrdinalIgnoreCase) >= 0
                || fullName.IndexOf(RealSmokeDomain, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsRecognizableSmokeType(Type type)
        {
            string name = (type.FullName ?? type.Name).ToLowerInvariant();
            return name.Contains("smoke") || name.Contains("chimney") || name.Contains("charcoalpit");
        }

        private static void ProcessChildren(IParticlePropertiesProvider provider, AssetLocation ownerCode)
        {
            IParticlePropertiesProvider[] secondary = null;
            IParticlePropertiesProvider[] death = null;

            try { secondary = provider.SecondaryParticles; } catch { }
            try { death = provider.DeathParticles; } catch { }

            if (secondary != null)
            {
                foreach (IParticlePropertiesProvider child in secondary)
                {
                    ProcessProvider(child, ownerCode);
                }
            }

            if (death != null)
            {
                foreach (IParticlePropertiesProvider child in death)
                {
                    ProcessProvider(child, ownerCode);
                }
            }
        }

        private static void MarkChildrenExcluded(IParticlePropertiesProvider provider)
        {
            IParticlePropertiesProvider[] secondary = null;
            IParticlePropertiesProvider[] death = null;

            try { secondary = provider.SecondaryParticles; } catch { }
            try { death = provider.DeathParticles; } catch { }

            if (secondary != null)
            {
                foreach (IParticlePropertiesProvider child in secondary)
                {
                    MarkExcludedTree(child);
                }
            }

            if (death != null)
            {
                foreach (IParticlePropertiesProvider child in death)
                {
                    MarkExcludedTree(child);
                }
            }
        }

        private static void MarkExcludedTree(IParticlePropertiesProvider provider)
        {
            if (provider == null || !TryMarkProcessed(provider, excluded: true))
            {
                return;
            }

            Interlocked.Increment(ref runtimeProvidersExcluded);
            MarkChildrenExcluded(provider);
        }

        private static bool TryMarkProcessed(object provider, bool excluded)
        {
            lock (ProcessedLock)
            {
                if (ProcessedProviders.TryGetValue(provider, out _))
                {
                    return false;
                }

                ProcessedProviders.Add(provider, new ProcessedMarker(excluded));
                return true;
            }
        }

        private static void LogRuntimeErrorOnce(string message, Exception ex)
        {
            if (Interlocked.Exchange(ref runtimeErrorsLogged, 1) == 0)
            {
                activeInstance?.clientApi?.Logger.Warning($"[RAM GC] {message}: {ex.Message}");
            }
        }

        private static MethodInfo GetPatchMethod(string name)
        {
            return typeof(ParticleOptimizationSystem).GetMethod(
                name,
                BindingFlags.Static | BindingFlags.NonPublic);
        }

        public override void Dispose()
        {
            try
            {
                if (clientApi != null)
                {
                    clientApi.Event.BlockTexturesLoaded -= OnClientAssetsReady;
                    clientApi.Event.LevelFinalize -= OnClientAssetsReady;
                    clientApi.Event.LeaveWorld -= LogRuntimeStats;
                }

                if (!runtimeStatsLogged)
                {
                    LogRuntimeStats();
                }

                particleHarmony?.UnpatchAll(HarmonyId);
            }
            catch (Exception ex)
            {
                clientApi?.Logger.Warning($"[RAM GC] Error al retirar los parches de particulas: {ex.Message}");
            }

            if (ReferenceEquals(activeInstance, this))
            {
                activeInstance = null;
            }

            base.Dispose();
        }
    }
}
