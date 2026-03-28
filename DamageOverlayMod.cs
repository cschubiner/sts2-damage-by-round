using System.Globalization;
using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2DamageByRound;

[ModInitializer(nameof(ModLoaded))]
public static class DamageOverlayMod
{
    private const string HarmonyId = "canal.sts2.damage_by_round";
    private static readonly Dictionary<NCombatUi, DamageOverlayController> Controllers = new();
    private static Harmony? _harmony;

    public static void ModLoaded()
    {
        _harmony ??= new Harmony(HarmonyId);
        _harmony.PatchAll(typeof(DamageOverlayMod).Assembly);
        DamageRunTracker.Initialize();
        Log.Info("[DamageByRound] Loaded overlay mod");
    }

    private static void AttachOverlay(NCombatUi ui, CombatState state)
    {
        DetachOverlay(ui);

        DamageRunTracker.BeginCombat(state, CombatManager.Instance.History);
        var controller = new DamageOverlayController(ui, state);
        Controllers[ui] = controller;
        controller.Attach();
    }

    private static void DetachOverlay(NCombatUi ui)
    {
        DamageRunTracker.EndCombat();

        if (Controllers.Remove(ui, out var controller))
        {
            controller.Dispose();
        }
    }

    [HarmonyPatch(typeof(NCombatUi), nameof(NCombatUi.Activate))]
    private static class ActivatePatch
    {
        private static void Postfix(NCombatUi __instance, CombatState state)
        {
            AttachOverlay(__instance, state);
        }
    }

    [HarmonyPatch(typeof(NCombatUi), nameof(NCombatUi.Deactivate))]
    private static class DeactivatePatch
    {
        private static void Postfix(NCombatUi __instance)
        {
            DetachOverlay(__instance);
        }
    }

    [HarmonyPatch(typeof(NCombatUi), nameof(NCombatUi._ExitTree))]
    private static class ExitTreePatch
    {
        private static void Postfix(NCombatUi __instance)
        {
            DetachOverlay(__instance);
        }
    }

    [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.Reset))]
    private static class CombatResetPatch
    {
        private static void Prefix(CombatManager __instance)
        {
            DamageRunTracker.CommitBeforeHistoryReset();
        }
    }

    [HarmonyPatch(typeof(CombatHistory), nameof(CombatHistory.DamageReceived))]
    private static class DamageReceivedPatch
    {
        private static void Postfix(CombatState combatState, Creature receiver, Creature? dealer, DamageResult result)
        {
            DamageRunTracker.RecordDamage(dealer, receiver, result);
        }
    }

    [HarmonyPatch(typeof(PoisonPower), nameof(PoisonPower.AfterSideTurnStart))]
    private static class PoisonPowerPatch
    {
        private static void Prefix(PoisonPower __instance, CombatSide side, out DeferredPowerDamageState __state)
        {
            __state = DeferredPowerDamageState.Capture(__instance.Owner, __instance.Applier?.Player, side == __instance.Owner.Side);
        }

        private static void Postfix(PoisonPower __instance, DeferredPowerDamageState __state)
        {
            __state.Commit(__instance.Owner);
        }
    }

    [HarmonyPatch(typeof(DoomPower), nameof(DoomPower.BeforeTurnEnd))]
    private static class DoomPowerPatch
    {
        private static void Prefix(DoomPower __instance, CombatSide side, out DeferredPowerDamageState __state)
        {
            var shouldTrigger = side == __instance.Owner.Side && !__instance.Owner.IsDead && __instance.IsOwnerDoomed();
            __state = DeferredPowerDamageState.Capture(__instance.Owner, __instance.Applier?.Player, shouldTrigger);
        }

        private static void Postfix(DoomPower __instance, DeferredPowerDamageState __state)
        {
            __state.Commit(__instance.Owner);
        }
    }

}

internal readonly record struct DeferredPowerDamageState(Player? Player, int StartingHp, bool ShouldTrack)
{
    public static DeferredPowerDamageState Capture(Creature owner, Player? player, bool shouldTrack)
    {
        if (!shouldTrack || player == null || !owner.IsEnemy)
        {
            return new DeferredPowerDamageState(null, 0, false);
        }

        return new DeferredPowerDamageState(player, owner.CurrentHp, true);
    }

    public void Commit(Creature owner)
    {
        if (!ShouldTrack || Player == null)
        {
            return;
        }

        var hpLost = Math.Max(StartingHp - owner.CurrentHp, 0);
        if (hpLost > 0)
        {
            DamageRunTracker.RecordAttributedDamage(Player.NetId, hpLost);
        }
    }
}

internal static class DamageRunTracker
{
    private static readonly Dictionary<ulong, int> ActTotals = new();
    private static readonly Dictionary<ulong, int> RunTotals = new();
    private static readonly Dictionary<ulong, int> CurrentCombatTotals = new();
    private static CombatState? _activeState;
    private static bool _activeCombatCommitted;
    private static bool _initialized;
    private static int _trackedActIndex = -1;

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        RunManager.Instance.RunStarted += OnRunStarted;
        RunManager.Instance.ActEntered += OnActEntered;
        CombatManager.Instance.CombatEnded += OnCombatEnded;
    }

    public static void BeginCombat(CombatState state, CombatHistory history)
    {
        SyncAct(state);
        _activeState = state;
        CurrentCombatTotals.Clear();
        _activeCombatCommitted = false;
    }

    public static void EndCombat()
    {
        if (_activeCombatCommitted)
        {
            return;
        }

        if (CurrentCombatTotals.Count > 0)
        {
            CommitTotals(CurrentCombatTotals);
            _activeCombatCommitted = true;
        }

        _activeState = null;
        CurrentCombatTotals.Clear();
    }

    public static void CommitBeforeHistoryReset()
    {
        EndCombat();
    }

    public static DamageSnapshot BuildSnapshot(CombatState state, CombatHistory history)
    {
        SyncAct(state);
        _activeState = state;

        var rounds = BuildRounds(state, history);
        var combatTotals = new Dictionary<ulong, int>(CurrentCombatTotals);
        var actTotals = MergeTotals(ActTotals, combatTotals);
        var runTotals = MergeTotals(RunTotals, combatTotals);
        return new DamageSnapshot(rounds, combatTotals, actTotals, runTotals);
    }

    private static void OnRunStarted(RunState state)
    {
        ActTotals.Clear();
        RunTotals.Clear();
        _activeState = null;
        CurrentCombatTotals.Clear();
        _activeCombatCommitted = false;
        _trackedActIndex = state.CurrentActIndex;
    }

    private static void OnActEntered()
    {
        var state = TryGetRunState();
        if (state == null)
        {
            return;
        }

        _trackedActIndex = state.CurrentActIndex;
        ActTotals.Clear();
        _activeState = null;
        CurrentCombatTotals.Clear();
        _activeCombatCommitted = false;
    }

    private static void SyncAct(CombatState state)
    {
        var runState = state.Players.FirstOrDefault()?.RunState as RunState;
        if (runState == null)
        {
            return;
        }

        if (_trackedActIndex == -1)
        {
            _trackedActIndex = runState.CurrentActIndex;
            return;
        }

        if (_trackedActIndex != runState.CurrentActIndex)
        {
            _trackedActIndex = runState.CurrentActIndex;
            ActTotals.Clear();
            _activeState = null;
            CurrentCombatTotals.Clear();
            _activeCombatCommitted = false;
        }
    }

    private static void OnCombatEnded(CombatRoom _)
    {
        if (_activeCombatCommitted)
        {
            return;
        }

        EndCombat();
    }

    public static void RecordDamage(Creature? dealer, Creature receiver, DamageResult result)
    {
        if (dealer == null || !dealer.IsPlayer || !receiver.IsEnemy)
        {
            return;
        }

        var player = dealer.Player;
        if (player == null)
        {
            return;
        }

        RecordAttributedDamage(player.NetId, result.TotalDamage);
    }

    public static void RecordAttributedDamage(ulong playerNetId, int amount)
    {
        if (amount <= 0)
        {
            return;
        }

        CurrentCombatTotals[playerNetId] = CurrentCombatTotals.GetValueOrDefault(playerNetId) + amount;
    }

    private static RunState? TryGetRunState()
    {
        return _activeState?.Players.FirstOrDefault()?.RunState as RunState;
    }

    private static void CommitTotals(Dictionary<ulong, int> combatTotals)
    {
        AddTotals(ActTotals, combatTotals);
        AddTotals(RunTotals, combatTotals);
    }

    private static void AddTotals(Dictionary<ulong, int> destination, Dictionary<ulong, int> source)
    {
        foreach (var pair in source)
        {
            destination[pair.Key] = destination.GetValueOrDefault(pair.Key) + pair.Value;
        }
    }

    private static Dictionary<ulong, int> MergeTotals(Dictionary<ulong, int> committed, Dictionary<ulong, int> live)
    {
        var merged = new Dictionary<ulong, int>(committed);
        AddTotals(merged, live);
        return merged;
    }

    private static SortedDictionary<int, Dictionary<ulong, int>> BuildRounds(CombatState state, CombatHistory history)
    {
        var playerIds = state.Players.Select(player => player.NetId).ToHashSet();
        var rounds = new SortedDictionary<int, Dictionary<ulong, int>>();

        foreach (var entry in history.Entries.OfType<DamageReceivedEntry>())
        {
            var player = GetDamagePlayer(entry, playerIds);
            if (player == null)
            {
                continue;
            }

            if (!rounds.TryGetValue(entry.RoundNumber, out var playerDamage))
            {
                playerDamage = new Dictionary<ulong, int>();
                rounds[entry.RoundNumber] = playerDamage;
            }

            playerDamage[player.NetId] = playerDamage.GetValueOrDefault(player.NetId) + entry.Result.TotalDamage;
        }

        return rounds;
    }

    private static Dictionary<ulong, int> BuildTotals(CombatState state, CombatHistory history)
    {
        var playerIds = state.Players.Select(player => player.NetId).ToHashSet();
        var totals = new Dictionary<ulong, int>();

        foreach (var entry in history.Entries.OfType<DamageReceivedEntry>())
        {
            var player = GetDamagePlayer(entry, playerIds);
            if (player == null)
            {
                continue;
            }

            totals[player.NetId] = totals.GetValueOrDefault(player.NetId) + entry.Result.TotalDamage;
        }

        return totals;
    }

    private static Player? GetDamagePlayer(DamageReceivedEntry entry, HashSet<ulong> playerIds)
    {
        var dealer = entry.Dealer;
        if (dealer == null || !dealer.IsPlayer || !entry.Receiver.IsEnemy)
        {
            return null;
        }

        var player = dealer.Player;
        if (player == null || !playerIds.Contains(player.NetId))
        {
            return null;
        }

        return player;
    }
}

internal readonly record struct DamageSnapshot(
    SortedDictionary<int, Dictionary<ulong, int>> Rounds,
    Dictionary<ulong, int> CombatTotals,
    Dictionary<ulong, int> ActTotals,
    Dictionary<ulong, int> RunTotals);

internal sealed class DamageOverlayController : IDisposable
{
    private readonly NCombatUi _ui;
    private readonly DamageOverlayPanel _panel;
    private CombatState _state;
    private bool _disposed;

    public DamageOverlayController(NCombatUi ui, CombatState state)
    {
        _ui = ui;
        _state = state;
        _panel = new DamageOverlayPanel();
    }

    public void Attach()
    {
        _ui.AddChild(_panel);
        CombatManager.Instance.StateTracker.CombatStateChanged += OnCombatStateChanged;
        Refresh();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CombatManager.Instance.StateTracker.CombatStateChanged -= OnCombatStateChanged;

        if (GodotObject.IsInstanceValid(_panel))
        {
            _panel.QueueFree();
        }
    }

    private void OnCombatStateChanged(CombatState state)
    {
        _state = state;
        Refresh();
    }

    private void Refresh()
    {
        if (_disposed || !GodotObject.IsInstanceValid(_ui) || !GodotObject.IsInstanceValid(_panel))
        {
            return;
        }

        _panel.Refresh(_state, DamageRunTracker.BuildSnapshot(_state, CombatManager.Instance.History));
    }
}

internal sealed class DamageOverlayPanel : PanelContainer
{
    private readonly Label _title;
    private readonly Label _body;

    public DamageOverlayPanel()
    {
        Name = "DamageByRoundOverlay";
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 100;
        AnchorLeft = 1f;
        AnchorRight = 1f;
        AnchorTop = 0f;
        AnchorBottom = 0f;
        OffsetLeft = -470f;
        OffsetRight = -20f;
        OffsetTop = 18f;
        OffsetBottom = 310f;
        GrowHorizontal = GrowDirection.Begin;
        Modulate = new Color(1f, 1f, 1f, 0.96f);

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.08f, 0.11f, 0.88f),
            BorderColor = new Color(0.86f, 0.76f, 0.46f, 0.75f),
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            CornerRadiusBottomLeft = 10,
            CornerRadiusBottomRight = 10,
            CornerRadiusTopLeft = 10,
            CornerRadiusTopRight = 10,
            ShadowColor = new Color(0f, 0f, 0f, 0.35f),
            ShadowSize = 3
        };
        AddThemeStyleboxOverride("panel", style);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        AddChild(margin);

        var stack = new VBoxContainer();
        stack.AddThemeConstantOverride("separation", 6);
        margin.AddChild(stack);

        _title = new Label
        {
            Text = "Damage By Round",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _title.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.76f));
        stack.AddChild(_title);

        _body = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Text = "Waiting for combat..."
        };
        _body.AddThemeColorOverride("font_color", new Color(0.92f, 0.95f, 0.98f));
        stack.AddChild(_body);
    }

    public void Refresh(CombatState state, DamageSnapshot snapshot)
    {
        _title.Text = $"Damage Tracker  |  Round {state.RoundNumber} {SideSuffix(state.CurrentSide)}";
        _body.Text = BuildBody(state, snapshot);
    }

    private static string BuildBody(CombatState state, DamageSnapshot snapshot)
    {
        var players = state.Players.ToList();
        if (players.Count == 0)
        {
            return "No players found in combat.";
        }

        var sb = new StringBuilder();

        if (snapshot.Rounds.Count == 0)
        {
            sb.Append("No enemy damage recorded yet.");
        }
        else
        {
            foreach (var round in snapshot.Rounds)
            {
                sb.Append("R");
                sb.Append(round.Key);
                sb.Append(": ");
                sb.AppendLine(string.Join("  |  ", players.Select(player =>
                {
                    var amount = round.Value.GetValueOrDefault(player.NetId);
                    return $"{FormatPlayerName(player)} {amount}";
                })));
            }
        }

        if (sb.Length > 0)
        {
            sb.AppendLine();
        }

        sb.Append("Combat: ");
        sb.AppendLine(FormatTotals(players, snapshot.CombatTotals));
        sb.Append("Act: ");
        sb.AppendLine(FormatTotals(players, snapshot.ActTotals));
        sb.Append("Total: ");
        sb.AppendLine(FormatTotals(players, snapshot.RunTotals));

        return sb.ToString().TrimEnd();
    }

    private static string FormatTotals(IEnumerable<Player> players, IReadOnlyDictionary<ulong, int> totals)
    {
        return string.Join("  |  ", players.Select(player => $"{FormatPlayerName(player)} {totals.GetValueOrDefault(player.NetId)}"));
    }

    private static string FormatPlayerName(Player player)
    {
        var raw = player.Character.Id.Entry.Replace("_", " ").ToLowerInvariant();
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(raw);
    }

    private static string SideSuffix(CombatSide side)
    {
        return side == CombatSide.Player ? "(Player Turn)" : "(Enemy Turn)";
    }
}
