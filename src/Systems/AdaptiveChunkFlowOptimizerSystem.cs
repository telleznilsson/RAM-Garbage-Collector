using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace AdaptiveChunkFlowOptimizer
{
    /// <summary>
    /// Experimental, server-side adaptive chunk-generation budget controller for Vintage Story 1.22.7.
    ///
    /// Learns a latency reference from valid baseline-budget windows. When queue demand persists
    /// while timings remain healthy relative to that reference, it raises the generation budget
    /// in 10% steps up to 130% of baseline. Sustained severe latency without queue pressure can
    /// lower the budget to 80%. It leaves the send budget at baseline and never changes
    /// MaxWorldgenThreads or persistent configuration. This is not a worldgen algorithm rewrite.
    /// </summary>
    public sealed class AdaptiveChunkFlowOptimizerSystem : ModSystem
    {
        private const string ModTag = "[AdaptiveChunkFlow]";

        // Safety guardrails are heuristics, not direct CPU-utilization measurements.
        private const double SevereAverageMs = 35.0;
        private const double SevereMaximumMs = 100.0;
        private const double BoostAverageCeilingMs = 30.0;
        private const double BoostMaximumCeilingMs = 100.0;
        private const double HealthyAverageMs = 27.0;
        private const double HealthyMaximumMs = 85.0;

        private const int HighBacklog = 180;
        private const int BacklogTrendFloor = 120;
        private const int MinimumQueueGrowthPerWindow = 30;
        private const int MinimumQueueFallPerWindow = 30;
        private const int BacklogRecoveryThreshold = 120;
        private const int MinimumReferenceWindows = 3;
        private const int MaximumReferenceWindows = 10;
        private const int MinimumTimedLoadsForDecision = 10;
        private const int ReportIntervalMs = 15000;
        private const int MinimumChunksToSendPerTick = 16;
        private const int MinimumColumnsToGeneratePerTick = 2;
        private const int GenerationBudgetStepPercent = 10;
        private const int MaximumGenerationBudgetPercent = 130;
        private const int ProtectionGenerationBudgetPercent = 80;

        private const int StaleMeasurementMs = 120000;
        private int consecutiveSevereWindows;
        private int consecutiveHealthyWindows;
        private int consecutiveGrowingBacklogWindows;
        private int consecutiveDemandWindows;
        private int previousGeneratingQueue = -1;
        private int currentGenerationBudgetPercent = 100;
        private string currentMode = "BASE";
        private int latencyReferenceWindows;
        private double latencyReferenceAverageMs;
        private double latencyReferenceMaximumMs;
        private const int MaxObjectGraphDepth = 4;
        private const int MaxVisitedObjects = 2500;

        private static readonly HashSet<string> TargetSettingNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "ChunksToSendPerTick",
            "ChunkColumnsToGeneratePerThreadTick"
        };

        private ICoreServerAPI serverApi;
        private long reportListenerId;
        private readonly ConcurrentDictionary<long, long> columnStarts = new ConcurrentDictionary<long, long>();
        private readonly List<AppliedSetting> appliedSettings = new List<AppliedSetting>();

        private long intervalBeginEvents;
        private long intervalLoadedEvents;
        private long intervalTimedLoads;
        private long intervalUnmatchedLoads;
        private long intervalElapsedTicks;
        private long intervalMaxTicks;

        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return forSide == EnumAppSide.Server;
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            base.StartServerSide(api);
            serverApi = api;

            CaptureBaselineSettings(api);

            api.Event.BeginChunkColumnLoadChunkThread += OnBeginChunkColumnLoad;
            api.Event.ChunkColumnLoaded += OnChunkColumnLoaded;
            reportListenerId = api.Event.RegisterGameTickListener(OnReportTimer, ReportIntervalMs);

            api.Logger.Notification(
                ModTag + " activo (0.3.0). Empieza con los valores capturados; aumenta el presupuesto de generación " +
                "en pasos del 10% si la cola crece y los tiempos siguen saludables (máximo 130% de base). " +
                "Puede reducirlo temporalmente al 80% ante latencia elevada sostenida sin presión de cola. " +
                "ChunksToSendPerTick queda en su valor base por no medirse una cola de envío independiente. " +
                "No cambia MaxWorldgenThreads ni escribe configuración persistente."
            );
        }

        private void CaptureBaselineSettings(ICoreServerAPI api)
        {
            List<SettingAccessor> candidates = DiscoverSettingAccessors(api);
            CaptureOneSetting(api, candidates, "ChunksToSendPerTick", MinimumChunksToSendPerTick);
            CaptureOneSetting(api, candidates, "ChunkColumnsToGeneratePerThreadTick", MinimumColumnsToGeneratePerTick);

            if (appliedSettings.Count == 0)
            {
                api.Logger.Warning(
                    ModTag + " No se encontraron parámetros de carga modificables. El diagnóstico seguirá activo, " +
                    "pero el controlador no podrá ajustar el presupuesto."
                );
            }
            else
            {
                api.Logger.Notification(
                    ModTag + " valores base capturados: " + string.Join(", ", appliedSettings.Select(setting =>
                        setting.Accessor.Name + "=" + setting.OriginalValue)) + ". No se aplica ajuste inicial."
                );
                api.Logger.Notification(
                    ModTag + " ChunksToSendPerTick permanece en su valor base; el escalado adaptativo se aplica solo a " +
                    "ChunkColumnsToGeneratePerThreadTick, porque no se mide una cola de envío independiente."
                );
            }
        }

        private void CaptureOneSetting(ICoreServerAPI api, List<SettingAccessor> candidates, string settingName, int minimum)
        {
            List<SettingAccessor> matches = candidates
                .Where(candidate => candidate.Name == settingName)
                .OrderBy(candidate => candidate.Priority)
                .ToList();

            foreach (SettingAccessor accessor in matches)
            {
                try
                {
                    int original = accessor.Read();
                    if (original < minimum) continue;

                    appliedSettings.Add(new AppliedSetting(accessor, original, original));
                    api.Logger.Notification(
                        ModTag + " parámetro monitorizado: " + settingName + "=" + original +
                        " (" + accessor.Label + ")."
                    );
                    return;
                }
                catch (Exception exception)
                {
                    api.Logger.Debug(
                        ModTag + " No se pudo leer " + settingName + " mediante " + accessor.Label + ": " + exception.Message
                    );
                }
            }

            api.Logger.Warning(ModTag + " No se pudo capturar un valor base para " + settingName + ".");
        }

        private void UpdateLatencyReference(long timedLoads, double averageMs, double maxMs)
        {
            // Learn from baseline-budget intervals whose timings are within the initial
            // conservative guardrails. A rising queue can still be a useful sample when
            // completed columns remain healthy; otherwise the controller could never
            // finish calibration during an extended exploration session.
            if (timedLoads < MinimumTimedLoadsForDecision ||
                currentGenerationBudgetPercent != 100 ||
                averageMs <= 0 || averageMs > BoostAverageCeilingMs ||
                maxMs <= 0 || maxMs > BoostMaximumCeilingMs)
            {
                return;
            }

            if (latencyReferenceWindows == 0)
            {
                latencyReferenceAverageMs = averageMs;
                latencyReferenceMaximumMs = maxMs;
                latencyReferenceWindows = 1;
                return;
            }

            // Start with a cumulative average, then use a slow EMA once enough samples exist.
            double alpha = latencyReferenceWindows < 5
                ? 1.0 / (latencyReferenceWindows + 1.0)
                : 0.20;

            latencyReferenceAverageMs += (averageMs - latencyReferenceAverageMs) * alpha;
            latencyReferenceMaximumMs += (maxMs - latencyReferenceMaximumMs) * alpha;
            if (latencyReferenceWindows < MaximumReferenceWindows)
            {
                latencyReferenceWindows++;
            }
        }

        private double BoostAverageLimitMs()
        {
            if (latencyReferenceWindows < MinimumReferenceWindows) return BoostAverageCeilingMs;
            return Clamp(latencyReferenceAverageMs * 1.5, 12.0, BoostAverageCeilingMs);
        }

        private double BoostMaximumLimitMs()
        {
            if (latencyReferenceWindows < MinimumReferenceWindows) return BoostMaximumCeilingMs;
            return Clamp(latencyReferenceMaximumMs * 1.5, 50.0, BoostMaximumCeilingMs);
        }

        private double SevereAverageLimitMs()
        {
            if (latencyReferenceWindows < MinimumReferenceWindows) return SevereAverageMs;
            return Clamp(latencyReferenceAverageMs * 1.8, 18.0, 60.0);
        }

        private double SevereMaximumLimitMs()
        {
            if (latencyReferenceWindows < MinimumReferenceWindows) return SevereMaximumMs;
            return Clamp(latencyReferenceMaximumMs * 1.8, 65.0, 160.0);
        }

        private double HealthyAverageLimitMs()
        {
            if (latencyReferenceWindows < MinimumReferenceWindows) return HealthyAverageMs;
            return Math.Min(HealthyAverageMs, Math.Max(8.0, latencyReferenceAverageMs * 1.35));
        }

        private double HealthyMaximumLimitMs()
        {
            if (latencyReferenceWindows < MinimumReferenceWindows) return HealthyMaximumMs;
            return Math.Min(HealthyMaximumMs, Math.Max(40.0, latencyReferenceMaximumMs * 1.5));
        }

        private static double Clamp(double value, double min, double max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        private void UpdateAdaptiveBudget(
            ICoreServerAPI api,
            long timedLoads,
            double averageMs,
            double maxMs,
            int generatingQueue,
            bool hasQueueComparison,
            int queueDelta,
            bool queueGrowing,
            bool backlogPressure)
        {
            // A growing queue with healthy measured durations suggests the existing
            // per-thread generation budget may be the limiting factor. Increase it only
            // after repeated evidence, in small steps, and cap it at 130% of baseline.
            // This is not a CPU meter: column lifecycle duration can include other work.
            if (timedLoads < MinimumTimedLoadsForDecision)
            {
                consecutiveSevereWindows = 0;
                consecutiveHealthyWindows = 0;
                consecutiveDemandWindows = 0;
                return;
            }

            bool hasQueueData = generatingQueue >= 0 && hasQueueComparison;
            bool queueDemand = hasQueueData && (
                (generatingQueue >= BacklogTrendFloor && queueDelta >= MinimumQueueGrowthPerWindow) ||
                (generatingQueue >= HighBacklog && queueDelta >= -5));

            double boostAverageLimit = BoostAverageLimitMs();
            double boostMaximumLimit = BoostMaximumLimitMs();
            double severeAverageLimit = SevereAverageLimitMs();
            double severeMaximumLimit = SevereMaximumLimitMs();
            double healthyAverageLimit = HealthyAverageLimitMs();
            double healthyMaximumLimit = HealthyMaximumLimitMs();

            bool latencySafeForBoost =
                averageMs <= boostAverageLimit &&
                maxMs <= boostMaximumLimit;

            bool severe =
                averageMs >= severeAverageLimit &&
                maxMs >= severeMaximumLimit;

            bool queueRecovering = hasQueueData && (
                generatingQueue < BacklogRecoveryThreshold ||
                (generatingQueue < HighBacklog && queueDelta <= -MinimumQueueFallPerWindow));

            bool healthy =
                averageMs <= healthyAverageLimit &&
                maxMs <= healthyMaximumLimit &&
                queueRecovering &&
                !queueGrowing;

            if (severe)
            {
                consecutiveSevereWindows++;
                consecutiveDemandWindows = 0;
                consecutiveHealthyWindows = 0;
            }
            else
            {
                consecutiveSevereWindows = 0;
                consecutiveDemandWindows = queueDemand && latencySafeForBoost
                    ? consecutiveDemandWindows + 1
                    : 0;
                consecutiveHealthyWindows = healthy ? consecutiveHealthyWindows + 1 : 0;
            }

            // Protection is deliberately not used while the queue is high/growing:
            // throttling generation in that situation could make terrain appear slower.
            if (consecutiveSevereWindows >= 2)
            {
                if (backlogPressure || generatingQueue >= HighBacklog)
                {
                    if (currentGenerationBudgetPercent != 100)
                    {
                        SetGenerationBudget(api, 100, "BASE",
                            "latencia elevada con cola alta; se cancela cualquier ajuste y se conserva el presupuesto base");
                    }
                }
                else
                {
                    SetGenerationBudget(api, ProtectionGenerationBudgetPercent, "PROTECCIÓN",
                        "latencia elevada en dos intervalos consecutivos y cola sin presión");
                }

                consecutiveSevereWindows = 0;
                consecutiveHealthyWindows = 0;
                consecutiveDemandWindows = 0;
                return;
            }

            // If an above-base boost coincides with timings outside the learned safe range,
            // cancel the boost rather than waiting for the backlog threshold to be crossed.
            if (currentGenerationBudgetPercent > 100 && !latencySafeForBoost)
            {
                consecutiveDemandWindows = 0;
                consecutiveHealthyWindows = 0;
                SetGenerationBudget(api, 100, "BASE",
                    "latencia fuera del rango seguro; se cancela el impulso y se vuelve al presupuesto base");
                return;
            }

            // When backlog exists but the timings no longer look safe, do not raise the
            // budget. Protection is also removed in favor of baseline when generation is behind.
            if (backlogPressure && !latencySafeForBoost)
            {
                consecutiveDemandWindows = 0;
                consecutiveHealthyWindows = 0;
                if (currentGenerationBudgetPercent != 100)
                {
                    SetGenerationBudget(api, 100, "BASE",
                        "cola alta con latencia fuera del rango seguro; se vuelve al presupuesto base");
                }
                return;
            }

            if (queueDemand && latencySafeForBoost)
            {
                consecutiveHealthyWindows = 0;

                // A backlog signal cancels protection first. Boosting begins only after
                // the controller has returned to baseline and demand persists again.
                if (currentGenerationBudgetPercent < 100)
                {
                    SetGenerationBudget(api, 100, "BASE",
                        "cola creciente con tiempos saludables; se cancela la protección");
                    consecutiveDemandWindows = 0;
                    return;
                }

                if (consecutiveDemandWindows >= 2 && currentGenerationBudgetPercent < MaximumGenerationBudgetPercent)
                {
                    int nextPercent = Math.Min(
                        MaximumGenerationBudgetPercent,
                        currentGenerationBudgetPercent + GenerationBudgetStepPercent);

                    SetGenerationBudget(api, nextPercent, "IMPULSO-" + nextPercent,
                        "cola alta/creciente con tiempos de carga saludables durante dos intervalos");
                    consecutiveDemandWindows = 0;
                }
                return;
            }

            if (healthy)
            {
                if (consecutiveHealthyWindows >= 2 && currentGenerationBudgetPercent != 100)
                {
                    SetGenerationBudget(api, 100, "BASE",
                        "dos intervalos saludables y cola baja o en descenso");
                    consecutiveHealthyWindows = 0;
                    consecutiveDemandWindows = 0;
                }
                return;
            }

            // Inconclusive intervals must not cause the budget to oscillate.
            consecutiveHealthyWindows = 0;
            if (!queueDemand) consecutiveDemandWindows = 0;
        }

        private void SetGenerationBudget(ICoreServerAPI api, int targetPercent, string mode, string reason)
        {
            AppliedSetting setting = appliedSettings.FirstOrDefault(candidate =>
                candidate.Accessor.Name == "ChunkColumnsToGeneratePerThreadTick");

            if (setting == null || !setting.CanManage)
            {
                return;
            }

            try
            {
                int actual = setting.Accessor.Read();
                if (actual != setting.AppliedValue)
                {
                    setting.CanManage = false;
                    api.Logger.Warning(
                        ModTag + " deja de controlar " + setting.Accessor.Name + " porque otro sistema cambió el valor " +
                        setting.AppliedValue + " -> " + actual + ". No se sobrescribirá ese cambio."
                    );
                    return;
                }

                int target = Math.Max(
                    MinimumColumnsToGeneratePerTick,
                    (int)Math.Floor(setting.OriginalValue * (targetPercent / 100.0)));

                if (target != actual)
                {
                    setting.Accessor.Write(target);
                    int verified = setting.Accessor.Read();
                    if (verified != target)
                    {
                        setting.CanManage = false;
                        api.Logger.Warning(ModTag + " no se pudo verificar el nuevo valor de " + setting.Accessor.Name + ".");
                        return;
                    }
                    setting.AppliedValue = target;
                    api.Logger.Notification(
                        ModTag + " ajuste adaptativo: " + setting.Accessor.Name + " " + actual + " -> " + target +
                        " (" + mode + ", " + targetPercent + "% de base)."
                    );
                }

                bool modeChanged = !string.Equals(currentMode, mode, StringComparison.Ordinal);
                currentGenerationBudgetPercent = targetPercent;
                currentMode = mode;

                if (modeChanged || target == actual)
                {
                    api.Logger.Notification(
                        ModTag + " modo actual: " + currentMode + "; presupuesto de generación=" +
                        target + "/" + setting.OriginalValue + " (" + currentGenerationBudgetPercent + "% de base). " +
                        "Motivo: " + reason + "."
                    );
                }
            }
            catch (Exception exception)
            {
                setting.CanManage = false;
                api.Logger.Warning(ModTag + " deja de controlar " + setting.Accessor.Name + ": " + exception.Message);
            }
        }

        private static List<SettingAccessor> DiscoverSettingAccessors(ICoreServerAPI api)
        {
            var result = new List<SettingAccessor>();
            var candidateKeys = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<object>(ReferenceComparer.Instance);
            int visitedObjects = 0;

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (Assembly assembly in assemblies)
            {
                if (!IsVintageStoryAssembly(assembly)) continue;

                foreach (Type type in GetLoadableTypes(assembly))
                {
                    const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

                    FieldInfo[] fields;
                    try { fields = type.GetFields(staticFlags); }
                    catch { continue; }

                    foreach (FieldInfo field in fields)
                    {
                        string logicalName = LogicalMemberName(field.Name);
                        if (TargetSettingNames.Contains(logicalName) && field.FieldType == typeof(int) && !field.IsInitOnly && !field.IsLiteral)
                        {
                            AddFieldAccessor(result, candidateKeys, field, null);
                            continue;
                        }

                        if (field.IsStatic && IsMagicOrChunkHolder(field))
                        {
                            try
                            {
                                object value = field.GetValue(null);
                                CollectInstanceGraph(value, 1, result, candidateKeys, visited, ref visitedObjects);
                            }
                            catch { }
                        }
                    }

                    PropertyInfo[] properties;
                    try { properties = type.GetProperties(staticFlags); }
                    catch { continue; }

                    foreach (PropertyInfo property in properties)
                    {
                        if (!TargetSettingNames.Contains(property.Name) || property.PropertyType != typeof(int)) continue;
                        MethodInfo getter = property.GetGetMethod(true);
                        MethodInfo setter = property.GetSetMethod(true);
                        if (getter == null || setter == null || !getter.IsStatic || !setter.IsStatic) continue;
                        AddPropertyAccessor(result, candidateKeys, property, null);
                    }
                }
            }

            // Fallback for versions where the server magic numbers are held by an
            // instance instead of static fields. Traversal is bounded and follows
            // only Vintage Story objects (not entity lists, assets, or collections).
            object[] roots = { api, api.Server, api.WorldManager, api.World };
            foreach (object root in roots)
            {
                CollectInstanceGraph(root, 0, result, candidateKeys, visited, ref visitedObjects);
            }

            return result;
        }

        private static void CollectInstanceGraph(
            object instance,
            int depth,
            List<SettingAccessor> result,
            HashSet<string> candidateKeys,
            HashSet<object> visited,
            ref int visitedObjects)
        {
            if (instance == null || depth > MaxObjectGraphDepth || visitedObjects >= MaxVisitedObjects) return;
            Type runtimeType = instance.GetType();
            if (!IsVintageStoryAssembly(runtimeType.Assembly)) return;
            if (runtimeType.IsPrimitive || runtimeType.IsEnum || runtimeType.IsValueType || runtimeType == typeof(string) || runtimeType.IsArray) return;
            if (!visited.Add(instance)) return;
            visitedObjects++;

            for (Type type = runtimeType; type != null && IsVintageStoryAssembly(type.Assembly); type = type.BaseType)
            {
                FieldInfo[] fields;
                try
                {
                    fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch { continue; }

                foreach (FieldInfo field in fields)
                {
                    string logicalName = LogicalMemberName(field.Name);
                    if (TargetSettingNames.Contains(logicalName) && field.FieldType == typeof(int) && !field.IsInitOnly)
                    {
                        AddFieldAccessor(result, candidateKeys, field, instance);
                        continue;
                    }

                    if (depth >= MaxObjectGraphDepth || field.IsStatic) continue;
                    object value;
                    try { value = field.GetValue(instance); }
                    catch { continue; }
                    if (value == null) continue;
                    Type valueType = value.GetType();
                    if (!IsVintageStoryAssembly(valueType.Assembly)) continue;
                    if (valueType.IsPrimitive || valueType.IsEnum || valueType == typeof(string) || valueType.IsArray) continue;
                    if (value is Delegate) continue;
                    CollectInstanceGraph(value, depth + 1, result, candidateKeys, visited, ref visitedObjects);
                }

                PropertyInfo[] properties;
                try
                {
                    properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch { continue; }

                foreach (PropertyInfo property in properties)
                {
                    if (!TargetSettingNames.Contains(property.Name) || property.PropertyType != typeof(int)) continue;
                    MethodInfo getter = property.GetGetMethod(true);
                    MethodInfo setter = property.GetSetMethod(true);
                    if (getter == null || setter == null || getter.IsStatic || setter.IsStatic) continue;
                    AddPropertyAccessor(result, candidateKeys, property, instance);
                }
            }
        }

        private static void AddFieldAccessor(List<SettingAccessor> result, HashSet<string> keys, FieldInfo field, object owner)
        {
            bool isStatic = field.IsStatic;
            string label = (field.DeclaringType == null ? "?" : field.DeclaringType.FullName) + "::" + field.Name;
            if (!isStatic) label += " @" + RuntimeHelpers.GetHashCode(owner).ToString("x");
            string key = label;
            if (!keys.Add(key)) return;

            result.Add(new SettingAccessor(
                LogicalMemberName(field.Name),
                label,
                GetPriority(field.DeclaringType, isStatic),
                () => (int)field.GetValue(isStatic ? null : owner),
                value => field.SetValue(isStatic ? null : owner, value)
            ));
        }

        private static void AddPropertyAccessor(List<SettingAccessor> result, HashSet<string> keys, PropertyInfo property, object owner)
        {
            MethodInfo getter = property.GetGetMethod(true);
            MethodInfo setter = property.GetSetMethod(true);
            bool isStatic = getter != null && getter.IsStatic;
            string label = (property.DeclaringType == null ? "?" : property.DeclaringType.FullName) + "::" + property.Name;
            if (!isStatic) label += " @" + RuntimeHelpers.GetHashCode(owner).ToString("x");
            string key = label;
            if (!keys.Add(key)) return;

            result.Add(new SettingAccessor(
                property.Name,
                label,
                GetPriority(property.DeclaringType, isStatic),
                () => (int)property.GetValue(isStatic ? null : owner, null),
                value => property.SetValue(isStatic ? null : owner, value, null)
            ));
        }

        private static int GetPriority(Type declaringType, bool isStatic)
        {
            string name = declaringType == null ? string.Empty : declaringType.FullName ?? declaringType.Name;
            int priority = isStatic ? -1 : 0;
            if (name.IndexOf("Magic", StringComparison.OrdinalIgnoreCase) >= 0) priority -= 10;
            if (name.IndexOf("Server", StringComparison.OrdinalIgnoreCase) >= 0) priority -= 3;
            if (name.IndexOf("Chunk", StringComparison.OrdinalIgnoreCase) >= 0) priority -= 1;
            return priority;
        }

        private static bool IsMagicOrChunkHolder(FieldInfo field)
        {
            string fieldName = field.Name ?? string.Empty;
            string typeName = field.FieldType == null ? string.Empty : field.FieldType.Name;
            return fieldName.IndexOf("Magic", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   typeName.IndexOf("Magic", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fieldName.IndexOf("Chunk", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string LogicalMemberName(string fieldName)
        {
            if (string.IsNullOrEmpty(fieldName)) return fieldName;
            if (fieldName[0] == '<')
            {
                int end = fieldName.IndexOf('>');
                if (end > 1) return fieldName.Substring(1, end - 1);
            }
            return fieldName;
        }

        private static bool IsVintageStoryAssembly(Assembly assembly)
        {
            string name = assembly == null || assembly.GetName() == null ? string.Empty : assembly.GetName().Name;
            return !string.IsNullOrEmpty(name) && name.IndexOf("Vintagestory", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException exception) { return exception.Types.Where(type => type != null); }
            catch { return Array.Empty<Type>(); }
        }

        private void OnBeginChunkColumnLoad(IServerMapChunk mapChunk, int chunkX, int chunkZ, IWorldChunk[] chunks)
        {
            long key = MakeColumnKey(chunkX, chunkZ);
            columnStarts[key] = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref intervalBeginEvents);
        }

        private void OnChunkColumnLoaded(Vec2i chunkCoord, IWorldChunk[] chunks)
        {
            Interlocked.Increment(ref intervalLoadedEvents);
            long key = MakeColumnKey(chunkCoord.X, chunkCoord.Y);
            if (!columnStarts.TryRemove(key, out long startTimestamp))
            {
                Interlocked.Increment(ref intervalUnmatchedLoads);
                return;
            }

            long elapsedTicks = Math.Max(0, Stopwatch.GetTimestamp() - startTimestamp);
            Interlocked.Increment(ref intervalTimedLoads);
            Interlocked.Add(ref intervalElapsedTicks, elapsedTicks);
            UpdateMaximum(ref intervalMaxTicks, elapsedTicks);
        }

        private void OnReportTimer(float dt)
        {
            ICoreServerAPI api = serverApi;
            if (api == null) return;

            long begins = Interlocked.Exchange(ref intervalBeginEvents, 0);
            long loaded = Interlocked.Exchange(ref intervalLoadedEvents, 0);
            long timed = Interlocked.Exchange(ref intervalTimedLoads, 0);
            long unmatched = Interlocked.Exchange(ref intervalUnmatchedLoads, 0);
            long elapsed = Interlocked.Exchange(ref intervalElapsedTicks, 0);
            long maxElapsed = Interlocked.Exchange(ref intervalMaxTicks, 0);
            long stale = 0;

            long staleCutoff = Stopwatch.GetTimestamp() - (long)(StaleMeasurementMs * (double)Stopwatch.Frequency / 1000.0);
            foreach (KeyValuePair<long, long> pair in columnStarts)
            {
                if (pair.Value < staleCutoff && columnStarts.TryRemove(pair.Key, out _))
                {
                    stale++;
                }
            }

            int generatingQueue = -1;
            try { generatingQueue = api.WorldManager.CurrentGeneratingChunkCount; }
            catch { }

            int queueDelta = 0;
            bool hasQueueComparison = previousGeneratingQueue >= 0 && generatingQueue >= 0;
            if (hasQueueComparison)
            {
                queueDelta = generatingQueue - previousGeneratingQueue;
            }

            bool queueGrowing =
                hasQueueComparison &&
                generatingQueue >= BacklogTrendFloor &&
                queueDelta >= MinimumQueueGrowthPerWindow;

            if (queueGrowing)
            {
                consecutiveGrowingBacklogWindows++;
            }
            else
            {
                consecutiveGrowingBacklogWindows = 0;
            }

            if (generatingQueue >= 0)
            {
                previousGeneratingQueue = generatingQueue;
            }

            bool backlogPressure =
                generatingQueue >= HighBacklog ||
                consecutiveGrowingBacklogWindows >= 2;

            double averageMs = timed == 0 ? 0 : elapsed * 1000.0 / Stopwatch.Frequency / timed;
            double maxMs = maxElapsed * 1000.0 / Stopwatch.Frequency;

            UpdateLatencyReference(timed, averageMs, maxMs);

            UpdateAdaptiveBudget(
                api, timed, averageMs, maxMs, generatingQueue,
                hasQueueComparison, queueDelta, queueGrowing, backlogPressure);

            string queueTrend = generatingQueue < 0
                ? "no disponible"
                : !hasQueueComparison
                    ? "sin referencia"
                    : queueGrowing
                        ? "creciente"
                        : queueDelta <= -MinimumQueueGrowthPerWindow
                            ? "bajando"
                            : "estable";

            api.Logger.Notification(
                ModTag + " últimos 15 s: begin=" + begins +
                ", columnas cargadas=" + loaded +
                ", tiempos medidos=" + timed +
                ", media=" + averageMs.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " ms" +
                ", máximo=" + maxMs.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " ms" +
                ", sin inicio emparejado=" + unmatched +
                ", mediciones caducadas=" + stale +
                ", CurrentGeneratingChunkCount=" + generatingQueue +
                ", delta cola=" + (hasQueueComparison ? (queueDelta >= 0 ? "+" : string.Empty) + queueDelta.ToString(System.Globalization.CultureInfo.InvariantCulture) : "N/A") +
                ", tendencia cola=" + queueTrend +
                ", ventanas de crecimiento=" + consecutiveGrowingBacklogWindows +
                ", presión de cola=" + (backlogPressure ? "sí" : "no") +
                ", medidas pendientes=" + columnStarts.Count +
                ", referencia ventanas=" + latencyReferenceWindows +
                ", referencia media=" + (latencyReferenceWindows == 0 ? "N/A" : latencyReferenceAverageMs.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " ms") +
                ", referencia max=" + (latencyReferenceWindows == 0 ? "N/A" : latencyReferenceMaximumMs.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " ms") +
                ", límites impulso=" + BoostAverageLimitMs().ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "/" + BoostMaximumLimitMs().ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " ms" +
                ", límites severos=" + SevereAverageLimitMs().ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "/" + SevereMaximumLimitMs().ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " ms" +
                ", límites saludables=" + HealthyAverageLimitMs().ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "/" + HealthyMaximumLimitMs().ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " ms" +
                ", modo=" + currentMode +
                ", presupuesto generacion=" + currentGenerationBudgetPercent + "% de base."
            );
        }

        private static long MakeColumnKey(int x, int z)
        {
            return ((long)x << 32) | (uint)z;
        }

        private static void UpdateMaximum(ref long location, long value)
        {
            long current;
            do
            {
                current = Interlocked.Read(ref location);
                if (value <= current) return;
            }
            while (Interlocked.CompareExchange(ref location, value, current) != current);
        }

        public override void Dispose()
        {
            ICoreServerAPI api = serverApi;
            if (api != null)
            {
                try { api.Event.BeginChunkColumnLoadChunkThread -= OnBeginChunkColumnLoad; } catch { }
                try { api.Event.ChunkColumnLoaded -= OnChunkColumnLoaded; } catch { }
                if (reportListenerId != 0)
                {
                    try { api.Event.UnregisterGameTickListener(reportListenerId); } catch { }
                    reportListenerId = 0;
                }

                for (int i = appliedSettings.Count - 1; i >= 0; i--)
                {
                    AppliedSetting applied = appliedSettings[i];
                    try
                    {
                        // Avoid overwriting a value changed by another mod.
                        if (applied.CanManage && applied.Accessor.Read() == applied.AppliedValue &&
                            applied.AppliedValue != applied.OriginalValue)
                        {
                            applied.Accessor.Write(applied.OriginalValue);
                            api.Logger.Notification(
                                ModTag + " restaurado en memoria: " + applied.Accessor.Name + " " + applied.AppliedValue +
                                " -> " + applied.OriginalValue + "."
                            );
                        }
                    }
                    catch { }
                }
            }

            appliedSettings.Clear();
            columnStarts.Clear();
            serverApi = null;
            base.Dispose();
        }

        private sealed class SettingAccessor
        {
            public readonly string Name;
            public readonly string Label;
            public readonly int Priority;
            private readonly Func<int> reader;
            private readonly Action<int> writer;

            public SettingAccessor(string name, string label, int priority, Func<int> reader, Action<int> writer)
            {
                Name = name;
                Label = label;
                Priority = priority;
                this.reader = reader;
                this.writer = writer;
            }

            public int Read() => reader();
            public void Write(int value) => writer(value);
        }

        private sealed class AppliedSetting
        {
            public readonly SettingAccessor Accessor;
            public readonly int OriginalValue;
            public int AppliedValue;
            public bool CanManage = true;

            public AppliedSetting(SettingAccessor accessor, int originalValue, int appliedValue)
            {
                Accessor = accessor;
                OriginalValue = originalValue;
                AppliedValue = appliedValue;
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
