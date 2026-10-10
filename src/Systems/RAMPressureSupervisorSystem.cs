using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vintagestory.API.Common;

namespace GenericStructureRAMFix.Systems
{
    /// <summary>
    /// Supervises physical/GC memory pressure and requests a non-blocking Gen 2 collection
    /// only after sustained pressure. This does not attempt to repair objects that remain
    /// referenced by another mod (a true managed leak), or native-memory leaks.
    /// </summary>
    public sealed class RAMPressureSupervisorSystem : ModSystem
    {
        private const int SampleIntervalMs = 15000;
        private const int SamplesBeforeCollection = 3;       // 45 seconds of sustained pressure
        private const int CollectionCooldownSeconds = 300;   // at most one request every 5 minutes
        private const int StatusLogIntervalSamples = 4;      // one diagnostic line per minute
        private const int PendingResultTimeoutSeconds = 120;
        private const long MinimumManagedHeapMiB = 256;
        private const double HighPhysicalMemoryPercent = 88.0;
        private const double HighGcMemoryLoadPercent = 88.0;
        private const long MiB = 1024L * 1024L;

        private ICoreAPI api;
        private long tickListenerId;
        private Process currentProcess;
        private int consecutivePressureSamples;
        private int samplesSinceStatusLog;
        private DateTime lastCollectionRequestUtc = DateTime.MinValue;

        private bool hasPendingResult;
        private DateTime pendingSinceUtc;
        private long pendingManagedBytes;
        private long pendingWorkingSetBytes;
        private int pendingGen2Count;

        public override bool ShouldLoad(EnumAppSide forSide) => true;

        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            this.api = api;

            try
            {
                currentProcess = Process.GetCurrentProcess();
            }
            catch (Exception exception)
            {
                api.Logger.Warning("[RAM PRESSURE] No se pudo consultar la memoria del proceso: " + exception.Message);
            }

            tickListenerId = api.Event.RegisterGameTickListener(OnMonitor, SampleIntervalMs);
            api.Logger.Notification(
                "[RAM PRESSURE] Supervisor Gen 2 activo en " + api.Side +
                ". Revisa memoria cada 15 s; exige 3 muestras de presión, heap administrado mínimo 256 MiB " +
                "y espera 5 min entre solicitudes. La recolección se solicita sin compactar y sin bloquear cuando el runtime lo permite."
            );
        }

        private void OnMonitor(float dt)
        {
            ICoreAPI activeApi = api;
            if (activeApi == null) return;

            try
            {
                long managedBytes = GC.GetTotalMemory(false);
                long workingSetBytes = ReadWorkingSetBytes();
                GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();
                bool hasPhysicalMemory = TryGetPhysicalMemory(out long totalPhysicalBytes, out long availablePhysicalBytes, out int physicalLoadPercent);

                double gcLoadPercent = 0;
                if (gcInfo.HighMemoryLoadThresholdBytes > 0)
                {
                    gcLoadPercent = 100.0 * gcInfo.MemoryLoadBytes / gcInfo.HighMemoryLoadThresholdBytes;
                }

                bool physicalPressure = hasPhysicalMemory && physicalLoadPercent >= HighPhysicalMemoryPercent;
                bool gcPressure = gcInfo.HighMemoryLoadThresholdBytes > 0 && gcLoadPercent >= HighGcMemoryLoadPercent;
                bool enoughManagedHeap = managedBytes >= MinimumManagedHeapMiB * MiB;
                bool highPressure = enoughManagedHeap && (physicalPressure || gcPressure);

                ReportPendingResultIfReady(managedBytes, workingSetBytes, gcInfo);
                LogPeriodicStatus(
                    managedBytes, workingSetBytes, gcInfo, hasPhysicalMemory,
                    totalPhysicalBytes, availablePhysicalBytes, physicalLoadPercent, gcLoadPercent,
                    highPressure, enoughManagedHeap);

                if (highPressure)
                {
                    consecutivePressureSamples++;
                }
                else
                {
                    consecutivePressureSamples = 0;
                }

                if (consecutivePressureSamples < SamplesBeforeCollection) return;

                DateTime now = DateTime.UtcNow;
                if (now - lastCollectionRequestUtc < TimeSpan.FromSeconds(CollectionCooldownSeconds)) return;

                int gen2Before = GC.CollectionCount(2);
                pendingManagedBytes = managedBytes;
                pendingWorkingSetBytes = workingSetBytes;
                pendingGen2Count = gen2Before;
                pendingSinceUtc = now;
                hasPendingResult = true;
                lastCollectionRequestUtc = now;
                consecutivePressureSamples = 0;

                activeApi.Logger.Warning(
                    "[RAM PRESSURE] Presión sostenida detectada; se solicita GC Gen 2 no bloqueante. " +
                    FormatMemoryState(managedBytes, workingSetBytes, hasPhysicalMemory, physicalLoadPercent, gcLoadPercent) +
                    "; muestras consecutivas=" + SamplesBeforeCollection +
                    "; GC Gen 2 antes=" + gen2Before + "."
                );

                // With background GC enabled, the runtime can perform this concurrently.
                // If the runtime cannot do a non-blocking collection, it may still pause the caller.
                GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);
            }
            catch (Exception exception)
            {
                activeApi.Logger.Warning("[RAM PRESSURE] Error durante la supervisión: " + exception.Message);
            }
        }

        private void ReportPendingResultIfReady(long managedBytes, long workingSetBytes, GCMemoryInfo gcInfo)
        {
            if (!hasPendingResult) return;

            int gen2Now = GC.CollectionCount(2);
            DateTime now = DateTime.UtcNow;
            if (gen2Now > pendingGen2Count)
            {
                long managedDelta = managedBytes - pendingManagedBytes;
                long workingSetDelta = workingSetBytes - pendingWorkingSetBytes;
                api?.Logger.Notification(
                    "[RAM PRESSURE] Medición posterior a la solicitud (puede incluir otras recolecciones): " +
                    "heap administrado " + FormatDeltaMiB(managedDelta) +
                    " MiB; working set " + FormatDeltaMiB(workingSetDelta) +
                    " MiB; Gen 2 acumuladas +" + (gen2Now - pendingGen2Count) +
                    "; heap actual=" + FormatMiB(managedBytes) + " MiB; working set actual=" + FormatMiB(workingSetBytes) + " MiB."
                );
                hasPendingResult = false;
            }
            else if (now - pendingSinceUtc >= TimeSpan.FromSeconds(PendingResultTimeoutSeconds))
            {
                api?.Logger.Warning(
                    "[RAM PRESSURE] No se observó un incremento de GC Gen 2 dentro de 120 s tras la solicitud. " +
                    "Heap administrado actual=" + FormatMiB(managedBytes) + " MiB; " +
                    "heap según GC=" + FormatMiB(gcInfo.HeapSizeBytes) + " MiB."
                );
                hasPendingResult = false;
            }
        }

        private void LogPeriodicStatus(
            long managedBytes,
            long workingSetBytes,
            GCMemoryInfo gcInfo,
            bool hasPhysicalMemory,
            long totalPhysicalBytes,
            long availablePhysicalBytes,
            int physicalLoadPercent,
            double gcLoadPercent,
            bool highPressure,
            bool enoughManagedHeap)
        {
            samplesSinceStatusLog++;
            if (samplesSinceStatusLog < StatusLogIntervalSamples) return;
            samplesSinceStatusLog = 0;

            string physical = hasPhysicalMemory
                ? physicalLoadPercent + "% usado (disponible=" + FormatMiB(availablePhysicalBytes) +
                  " MiB / total=" + FormatMiB(totalPhysicalBytes) + " MiB)"
                : "no disponible en esta plataforma";

            api?.Logger.Notification(
                "[RAM PRESSURE] Estado: RAM física=" + physical +
                "; carga GC=" + gcLoadPercent.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
                "% de su umbral; heap administrado=" + FormatMiB(managedBytes) +
                " MiB; heap GC=" + FormatMiB(gcInfo.HeapSizeBytes) +
                " MiB; working set=" + FormatMiB(workingSetBytes) +
                " MiB; presión=" + (highPressure ? "ALTA" : "normal") +
                "; heap mínimo=" + (enoughManagedHeap ? "cumplido" : "no alcanzado") + "."
            );
        }

        private long ReadWorkingSetBytes()
        {
            try
            {
                if (currentProcess != null && !currentProcess.HasExited)
                    return currentProcess.WorkingSet64;
            }
            catch { }
            return 0;
        }

        private static string FormatMemoryState(
            long managedBytes,
            long workingSetBytes,
            bool hasPhysicalMemory,
            int physicalLoadPercent,
            double gcLoadPercent)
        {
            string physical = hasPhysicalMemory ? physicalLoadPercent + "% RAM física" : "RAM física N/D";
            return physical + "; carga GC=" +
                   gcLoadPercent.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
                   "%; heap administrado=" + FormatMiB(managedBytes) +
                   " MiB; working set=" + FormatMiB(workingSetBytes) + " MiB";
        }

        private static string FormatMiB(long bytes) => (bytes / (double)MiB).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        private static string FormatDeltaMiB(long bytes) => (bytes / (double)MiB).ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture);

        private static bool TryGetPhysicalMemory(out long totalBytes, out long availableBytes, out int loadPercent)
        {
            totalBytes = 0;
            availableBytes = 0;
            loadPercent = 0;
            if (!OperatingSystem.IsWindows()) return false;

            try
            {
                MEMORYSTATUSEX status = new MEMORYSTATUSEX
                {
                    dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
                };

                if (!GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0) return false;

                totalBytes = (long)status.ullTotalPhys;
                availableBytes = (long)status.ullAvailPhys;
                loadPercent = (int)status.dwMemoryLoad;
                return true;
            }
            catch
            {
                return false;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        public override void Dispose()
        {
            if (api != null && tickListenerId != 0)
            {
                try { api.Event.UnregisterGameTickListener(tickListenerId); }
                catch { }
            }

            try { currentProcess?.Dispose(); }
            catch { }

            currentProcess = null;
            api = null;
            tickListenerId = 0;
            hasPendingResult = false;
            base.Dispose();
        }
    }
}
