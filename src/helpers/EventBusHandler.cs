using System;
using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API.Core;

namespace OSBase.Helpers;

/// <summary>
/// Central event bus handler that manages module subscriptions and dispatches events.
/// Decouples modules from the global event system to prevent duplicate handlers on reload.
/// </summary>
public class EventBusHandler {
    private readonly Dictionary<string, List<Delegate>> eventBus = new(StringComparer.Ordinal);
    private readonly BasePlugin plugin;
    private readonly Dictionary<string, DateTime> duplicateWarnTimestamps = new(StringComparer.Ordinal);

    private static readonly TimeSpan DuplicateWarnCooldown = TimeSpan.FromSeconds(30);

    public EventBusHandler(BasePlugin plugin) {
        this.plugin = plugin;
    }

    /// <summary>Subscribe a module's event handler to the event bus</summary>
    public void SubscribeToEvent<TEvent>(Func<TEvent, HookResult> handler) {
        var eventName = typeof(TEvent).Name;
        
        if (!eventBus.ContainsKey(eventName)) {
            eventBus[eventName] = new();
        }

        // Guard against duplicate subscriptions on reloads.
        // Delegate equality works for same target instance + method pair.
        if (eventBus[eventName].Contains(handler)) {
            Console.WriteLine($"[WARN] OSBase EventBus: Duplicate subscribe ignored for {eventName} ({handler.Method.Name}).");
            WarnAdminsDuplicateSubscription(eventName, handler);
            return;
        }

        eventBus[eventName].Add(handler);
    }

    /// <summary>Unsubscribe a module's event handler from the event bus</summary>
    public void UnsubscribeFromEvent<TEvent>(Func<TEvent, HookResult> handler) {
        var eventName = typeof(TEvent).Name;
        
        if (eventBus.ContainsKey(eventName)) {
            // Remove all matching delegates in case duplicates slipped in from old builds.
            eventBus[eventName].RemoveAll(d => d.Equals(handler));

            // Keep dictionary clean when no subscribers remain.
            if (eventBus[eventName].Count == 0) {
                eventBus.Remove(eventName);
            }
        }
    }

    /// <summary>Dispatch event to all subscribed modules</summary>
    public void DispatchToEventBus<TEvent>(TEvent e) {
        var eventName = typeof(TEvent).Name;
        
        if (!eventBus.TryGetValue(eventName, out var handlers)) {
            return;
        }

        // Snapshot before dispatch: a handler may subscribe/unsubscribe mid-dispatch
        // (e.g. a module reload triggered by an event), which would otherwise throw
        // "Collection was modified" and abort delivery to the remaining handlers.
        var snapshot = handlers.ToArray();

        foreach (var handler in snapshot) {
            try {
                ((Func<TEvent, HookResult>)handler)?.Invoke(e);
            } catch (Exception ex) {
                Console.WriteLine($"[ERROR] OSBase EventBus: Exception in event handler for {eventName}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Returns event -> subscriber count snapshot for runtime diagnostics.
    /// </summary>
    public IReadOnlyDictionary<string, int> GetSubscriberCounts() {
        return eventBus.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Count, StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns event -> duplicate subscriber count (0 means clean).
    /// Duplicate = total handlers - distinct delegate pairs.
    /// </summary>
    public IReadOnlyDictionary<string, int> GetDuplicateCounts() {
        var duplicates = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var kvp in eventBus) {
            var total = kvp.Value.Count;
            var distinct = kvp.Value.Distinct().Count();
            duplicates[kvp.Key] = Math.Max(0, total - distinct);
        }

        return duplicates;
    }

    private void WarnAdminsDuplicateSubscription<TEvent>(string eventName, Func<TEvent, HookResult> handler) {
        var targetName = handler.Target?.GetType().Name ?? "Static";
        var methodName = handler.Method.Name;
        var dedupeKey = $"{eventName}:{targetName}:{methodName}";

        var now = DateTime.UtcNow;
        if (duplicateWarnTimestamps.TryGetValue(dedupeKey, out var lastWarnAt) && now - lastWarnAt < DuplicateWarnCooldown) {
            return;
        }

        duplicateWarnTimestamps[dedupeKey] = now;

        var msg = $"[EventBus WARNING] Duplicate subscription blocked: {eventName} -> {targetName}.{methodName}";
        ChatHelper.PrintToAdmins(msg);
    }

    /// <summary>Register all global event dispatchers (one-time setup)</summary>
    public void RegisterAllDispatchers() {
        plugin.RegisterEventHandler<EventPlayerHurt>(OnPlayerHurtGlobal);
        plugin.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeathGlobal);
        plugin.RegisterEventHandler<EventRoundStart>(OnRoundStartGlobal);
        plugin.RegisterEventHandler<EventRoundEnd>(OnRoundEndGlobal);
        plugin.RegisterEventHandler<EventPlayerConnect>(OnPlayerConnectGlobal);
        plugin.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnectGlobal);
        plugin.RegisterEventHandler<EventWarmupEnd>(OnWarmupEndGlobal);
        plugin.RegisterEventHandler<EventStartHalftime>(OnStartHalftimeGlobal);
        plugin.RegisterEventHandler<EventWeaponFire>(OnWeaponFireGlobal);
        plugin.RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEndGlobal);
        plugin.RegisterEventHandler<EventMapTransition>(OnMapTransitionGlobal);
        plugin.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFullGlobal);
        plugin.RegisterEventHandler<EventBombBeginplant>(OnBombBeginplantGlobal);
        plugin.RegisterEventHandler<EventBombAbortplant>(OnBombAbortplantGlobal);
        plugin.RegisterEventHandler<EventBombPlanted>(OnBombPlantedGlobal);
        plugin.RegisterEventHandler<EventBombBegindefuse>(OnBombBegindefuseGlobal);
        plugin.RegisterEventHandler<EventBombAbortdefuse>(OnBombAbortdefuseGlobal);
        plugin.RegisterEventHandler<EventBombDefused>(OnBombDefusedGlobal);
        plugin.RegisterEventHandler<EventBombExploded>(OnBombExplodedGlobal);
        plugin.RegisterEventHandler<EventPlayerTeam>(OnPlayerTeamGlobal);
        plugin.RegisterEventHandler<EventCsWinPanelMatch>(OnCsWinPanelMatchGlobal);
        plugin.RegisterEventHandler<EventRoundAnnounceMatchStart>(OnRoundAnnounceMatchStartGlobal);
        plugin.RegisterEventHandler<EventRoundAnnounceWarmup>(OnRoundAnnounceWarmupGlobal);
        plugin.RegisterEventHandler<EventBeginNewMatch>(OnBeginNewMatchGlobal);
        plugin.RegisterEventHandler<EventMapShutdown>(OnMapShutdownGlobal);
        plugin.RegisterEventHandler<EventItemPurchase>(OnItemPurchaseGlobal);
        plugin.RegisterEventHandler<EventItemPickup>(OnItemPickupGlobal);
    }

    // ========== GLOBAL EVENT DISPATCHERS ==========

    private HookResult OnPlayerHurtGlobal(EventPlayerHurt e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }

    private HookResult OnPlayerDeathGlobal(EventPlayerDeath e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }

    private HookResult OnRoundStartGlobal(EventRoundStart e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }

    private HookResult OnRoundEndGlobal(EventRoundEnd e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }

    private HookResult OnPlayerConnectGlobal(EventPlayerConnect e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnectGlobal(EventPlayerDisconnect e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }

    private HookResult OnWarmupEndGlobal(EventWarmupEnd e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }

    private HookResult OnStartHalftimeGlobal(EventStartHalftime e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }

    private HookResult OnWeaponFireGlobal(EventWeaponFire e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }

    private HookResult OnRoundFreezeEndGlobal(EventRoundFreezeEnd e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }

    private HookResult OnMapTransitionGlobal(EventMapTransition e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }

    private HookResult OnPlayerConnectFullGlobal(EventPlayerConnectFull e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }

    private HookResult OnBombBeginplantGlobal(EventBombBeginplant e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }

    private HookResult OnBombAbortplantGlobal(EventBombAbortplant e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }

    private HookResult OnBombPlantedGlobal(EventBombPlanted e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }

    private HookResult OnBombBegindefuseGlobal(EventBombBegindefuse e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }

    private HookResult OnBombAbortdefuseGlobal(EventBombAbortdefuse e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }

    private HookResult OnBombDefusedGlobal(EventBombDefused e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }

    private HookResult OnBombExplodedGlobal(EventBombExploded e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }

    private HookResult OnPlayerTeamGlobal(EventPlayerTeam e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }

    private HookResult OnCsWinPanelMatchGlobal(EventCsWinPanelMatch e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }

    private HookResult OnRoundAnnounceMatchStartGlobal(EventRoundAnnounceMatchStart e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }

    private HookResult OnRoundAnnounceWarmupGlobal(EventRoundAnnounceWarmup e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }

    private HookResult OnBeginNewMatchGlobal(EventBeginNewMatch e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }

    private HookResult OnMapShutdownGlobal(EventMapShutdown e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }

    private HookResult OnItemPurchaseGlobal(EventItemPurchase e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }

    private HookResult OnItemPickupGlobal(EventItemPickup e, GameEventInfo _) {
        DispatchToEventBus(e);
        return HookResult.Continue;
    }
}
