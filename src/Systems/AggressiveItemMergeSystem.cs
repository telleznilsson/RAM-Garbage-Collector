using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace GenericStructureRAMFix.Systems
{
    /// <summary>
    /// Server-side item-stack merger designed for bursts of drops (for example, large tree felling).
    /// Work is queued and bounded per tick. Only matching, stackable, nearly stationary EntityItem
    /// instances are merged. Creatures, corpses, block states, drops and physics are not modified.
    /// </summary>
    public sealed class AggressiveItemMergeSystem : ModSystem
    {
        private const string LogPrefix = "[Item Merge] ";

        private const int TickIntervalMs = 100;
        private const int InitialSettleDelayMs = 300;
        private const int RetryDelayMs = 250;
        private const int MaxStabilityRetries = 28;

        // These caps keep this subsystem from taking over a server tick when hundreds of drops appear.
        private const int MaxCandidatesPerTick = 16;
        private const int MaxQueueEntriesScannedPerTick = 96;
        private const double MaxWorkBudgetMsPerTick = 2.0;

        private const float MergeHorizontalRadius = 2.0f;
        private const float MergeVerticalRadius = 1.5f;
        private const double StableMotionThreshold = 0.055;

        private ICoreServerAPI serverApi;
        private long tickListenerId;
        private long lastSummaryMs;
        private bool warningLogged;

        private readonly object queueLock = new object();
        private readonly Queue<PendingItem> pendingItems = new Queue<PendingItem>();
        private readonly HashSet<long> pendingEntityIds = new HashSet<long>();

        private long itemsDequeued;
        private long itemSearches;
        private long stackMergeOperations;
        private long itemEntitiesRemoved;
        private long itemUnitsTransferred;
        private long stabilityRetries;
        private long droppedQueueEntries;
        private long lastReportedMergeOperations;
        private long lastReportedRemoved;

        private sealed class PendingItem
        {
            public readonly EntityItem Item;
            public readonly long FirstQueuedAtMs;
            public long NextAttemptAtMs;
            public int StabilityRetryCount;

            public PendingItem(EntityItem item, long now)
            {
                Item = item;
                FirstQueuedAtMs = now;
                NextAttemptAtMs = now + InitialSettleDelayMs;
            }
        }

        private enum CandidateResult
        {
            Complete,
            RetryUntilSettled
        }

        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return forSide == EnumAppSide.Server;
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            serverApi = api;
            lastSummaryMs = Environment.TickCount64;
            api.Event.OnEntitySpawn += OnEntitySpawn;
            tickListenerId = api.Event.RegisterGameTickListener(
                ProcessPendingItems,
                TickIntervalMs,
                TickIntervalMs);

            api.Logger.Notification(
                LogPrefix + "sistema nuevo activo: fusión diferida de objetos idénticos; " +
                $"radio={MergeHorizontalRadius:0.#} horizontal/{MergeVerticalRadius:0.#} vertical; " +
                $"máximo={MaxCandidatesPerTick} candidatos por tick; " +
                $"presupuesto={MaxWorkBudgetMsPerTick:0.#} ms por tick; " +
                $"espera inicial={InitialSettleDelayMs} ms; " +
                "solo EntityItem estable y pilas compatibles.");
        }

        private void OnEntitySpawn(Entity entity)
        {
            if (!(entity is EntityItem item) || item.EntityId <= 0)
            {
                return;
            }

            long now = Environment.TickCount64;
            lock (queueLock)
            {
                if (pendingEntityIds.Add(item.EntityId))
                {
                    pendingItems.Enqueue(new PendingItem(item, now));
                }
            }
        }

        private void ProcessPendingItems(float dt)
        {
            ICoreServerAPI api = serverApi;
            if (api?.World == null)
            {
                return;
            }

            long now = Environment.TickCount64;
            List<PendingItem> batch = new List<PendingItem>(MaxCandidatesPerTick);

            // Inspect a bounded number of queue entries. Deferred/falling objects go to the back
            // with a future attempt time, so they don't repeatedly trigger nearby-entity searches.
            lock (queueLock)
            {
                int scanCount = Math.Min(pendingItems.Count, MaxQueueEntriesScannedPerTick);
                int scanned = 0;
                while (scanned < scanCount && batch.Count < MaxCandidatesPerTick && pendingItems.Count > 0)
                {
                    PendingItem entry = pendingItems.Dequeue();
                    scanned++;

                    if (entry == null || entry.Item == null)
                    {
                        continue;
                    }

                    if (entry.NextAttemptAtMs > now)
                    {
                        pendingItems.Enqueue(entry);
                        continue;
                    }

                    batch.Add(entry);
                }
            }

            long workStartTicks = Stopwatch.GetTimestamp();
            double workBudgetTicks = Stopwatch.Frequency * MaxWorkBudgetMsPerTick / 1000.0;

            for (int i = 0; i < batch.Count; i++)
            {
                // A fixed candidate cap alone is not sufficient when the local entity list is
                // unusually dense. Defer remaining work once this tick's time budget is spent.
                if (i > 0 && Stopwatch.GetTimestamp() - workStartTicks >= workBudgetTicks)
                {
                    lock (queueLock)
                    {
                        for (int pendingIndex = i; pendingIndex < batch.Count; pendingIndex++)
                        {
                            pendingItems.Enqueue(batch[pendingIndex]);
                        }
                    }
                    break;
                }

                PendingItem entry = batch[i];
                CandidateResult result = CandidateResult.Complete;
                try
                {
                    result = ProcessCandidate(entry, now);
                }
                catch (Exception ex)
                {
                    LogWarningOnce("Se omitió una fusión tras un error. El servidor continúa: " + ex);
                }

                bool retry = result == CandidateResult.RetryUntilSettled &&
                    entry.StabilityRetryCount < MaxStabilityRetries;

                lock (queueLock)
                {
                    long entityId = entry.Item?.EntityId ?? 0;
                    if (retry && entityId > 0 && pendingEntityIds.Contains(entityId))
                    {
                        entry.StabilityRetryCount++;
                        entry.NextAttemptAtMs = now + RetryDelayMs;
                        pendingItems.Enqueue(entry);
                        Interlocked.Increment(ref stabilityRetries);
                    }
                    else if (entityId > 0)
                    {
                        pendingEntityIds.Remove(entityId);
                        if (result == CandidateResult.RetryUntilSettled)
                        {
                            Interlocked.Increment(ref droppedQueueEntries);
                        }
                    }
                }
            }

            MaybeLogSummary(now);
        }

        private CandidateResult ProcessCandidate(PendingItem entry, long now)
        {
            EntityItem source = entry?.Item;
            ICoreServerAPI api = serverApi;
            if (source == null || api?.World == null || !source.Alive)
            {
                return CandidateResult.Complete;
            }

            Entity current = api.World.GetEntityById(source.EntityId);
            if (!ReferenceEquals(current, source))
            {
                return CandidateResult.Complete;
            }

            ItemStack sourceStack = source.Itemstack;
            if (sourceStack?.Collectible == null || sourceStack.StackSize <= 0)
            {
                return CandidateResult.Complete;
            }

            int sourceMaxStackSize = sourceStack.Collectible.MaxStackSize;
            if (sourceMaxStackSize <= 1)
            {
                // Non-stackable drops need no settling check or spatial scan.
                return CandidateResult.Complete;
            }

            if (now - entry.FirstQueuedAtMs < InitialSettleDelayMs || !IsNearlyStationary(source))
            {
                return CandidateResult.RetryUntilSettled;
            }

            Interlocked.Increment(ref itemSearches);
            Entity[] nearbyEntities = api.World.GetEntitiesAround(
                source.Pos.XYZ,
                MergeHorizontalRadius,
                MergeVerticalRadius,
                entity => entity is EntityItem && entity.EntityId != source.EntityId);

            if (nearbyEntities == null || nearbyEntities.Length == 0)
            {
                Interlocked.Increment(ref itemsDequeued);
                return CandidateResult.Complete;
            }

            bool sourceChanged = false;
            foreach (Entity nearby in nearbyEntities)
            {
                if (sourceStack.StackSize <= 0)
                {
                    break;
                }

                if (!(nearby is EntityItem target) || ReferenceEquals(target, source) || !target.Alive)
                {
                    continue;
                }

                // Do not merge while either entity is bouncing, falling or sliding. The moving
                // entity's own queued entry will retry once it settles.
                if (!IsNearlyStationary(target))
                {
                    continue;
                }

                ItemStack targetStack = target.Itemstack;
                if (targetStack?.Collectible == null || targetStack.StackSize <= 0)
                {
                    continue;
                }

                int targetMaxStackSize = targetStack.Collectible.MaxStackSize;
                if (targetMaxStackSize <= 1 || targetStack.StackSize >= targetMaxStackSize)
                {
                    continue;
                }

                // ItemStack.Equals compares collectible identity and attributes, but ignores stack size.
                if (!targetStack.Equals(api.World, sourceStack))
                {
                    continue;
                }

                int freeSpace = targetMaxStackSize - targetStack.StackSize;
                int amountToMove = Math.Min(freeSpace, sourceStack.StackSize);
                if (amountToMove <= 0)
                {
                    continue;
                }

                targetStack.StackSize += amountToMove;
                sourceStack.StackSize -= amountToMove;

                // Reassign to refresh EntityItem's slot reference, then flag both attributes for sync.
                target.Itemstack = targetStack;
                target.WatchedAttributes.MarkPathDirty("itemstack");
                sourceChanged = true;

                Interlocked.Increment(ref stackMergeOperations);
                Interlocked.Add(ref itemUnitsTransferred, amountToMove);
            }

            if (sourceChanged)
            {
                if (sourceStack.StackSize <= 0)
                {
                    DespawnMergedItem(source);
                    Interlocked.Increment(ref itemEntitiesRemoved);
                    Interlocked.Increment(ref itemsDequeued);
                    return CandidateResult.Complete;
                }

                source.Itemstack = sourceStack;
                source.WatchedAttributes.MarkPathDirty("itemstack");
            }

            Interlocked.Increment(ref itemsDequeued);
            return CandidateResult.Complete;
        }

        private static bool IsNearlyStationary(EntityItem item)
        {
            if (item?.Pos?.Motion == null)
            {
                return false;
            }

            return Math.Abs(item.Pos.Motion.X) <= StableMotionThreshold &&
                   Math.Abs(item.Pos.Motion.Y) <= StableMotionThreshold &&
                   Math.Abs(item.Pos.Motion.Z) <= StableMotionThreshold;
        }

        private void DespawnMergedItem(EntityItem item)
        {
            if (item == null)
            {
                return;
            }

            if (serverApi?.World is IServerWorldAccessor serverWorld)
            {
                serverWorld.DespawnEntity(item, new EntityDespawnData
                {
                    Reason = EnumDespawnReason.Removed
                });
            }
            else
            {
                // Fallback still targets only an EntityItem whose complete stack was transferred.
                item.Die(EnumDespawnReason.Removed);
            }
        }

        private void MaybeLogSummary(long now)
        {
            if (now - lastSummaryMs < 10000)
            {
                return;
            }

            lastSummaryMs = now;
            long merges = Interlocked.Read(ref stackMergeOperations);
            long removed = Interlocked.Read(ref itemEntitiesRemoved);
            if (merges == lastReportedMergeOperations && removed == lastReportedRemoved)
            {
                return;
            }

            lastReportedMergeOperations = merges;
            lastReportedRemoved = removed;
            serverApi?.Logger.Notification(
                LogPrefix + "resumen: objetos procesados=" + Interlocked.Read(ref itemsDequeued) +
                "; búsquedas cercanas=" + Interlocked.Read(ref itemSearches) +
                "; operaciones de fusión=" + merges +
                "; entidades retiradas=" + removed +
                "; unidades transferidas=" + Interlocked.Read(ref itemUnitsTransferred) +
                "; reintentos por movimiento=" + Interlocked.Read(ref stabilityRetries) +
                "; entradas descartadas tras esperar=" + Interlocked.Read(ref droppedQueueEntries) + ".");
        }

        private void LogWarningOnce(string message)
        {
            if (warningLogged)
            {
                return;
            }

            warningLogged = true;
            serverApi?.Logger.Warning(LogPrefix + message);
        }

        public override void Dispose()
        {
            if (serverApi != null)
            {
                serverApi.Event.OnEntitySpawn -= OnEntitySpawn;
                if (tickListenerId != 0)
                {
                    serverApi.Event.UnregisterGameTickListener(tickListenerId);
                    tickListenerId = 0;
                }

                serverApi.Logger.Notification(
                    LogPrefix + "cierre: procesados=" + Interlocked.Read(ref itemsDequeued) +
                    "; búsquedas=" + Interlocked.Read(ref itemSearches) +
                    "; fusiones=" + Interlocked.Read(ref stackMergeOperations) +
                    "; entidades retiradas=" + Interlocked.Read(ref itemEntitiesRemoved) +
                    "; unidades transferidas=" + Interlocked.Read(ref itemUnitsTransferred) + ".");
            }

            lock (queueLock)
            {
                pendingItems.Clear();
                pendingEntityIds.Clear();
            }

            serverApi = null;
            base.Dispose();
        }
    }
}
