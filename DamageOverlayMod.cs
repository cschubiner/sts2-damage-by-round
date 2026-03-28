using System.Globalization;
using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace Sts2DamageByRound;

[ModInitializer(nameof(ModLoaded))]
public static class DamageOverlayMod
{
    private const string HarmonyId = "canal.sts2.damage_by_round";
    private const string SelfTestEnvVar = "STS2_DAMAGE_BY_ROUND_SELFTEST";
    private const string SelfTestScreenshotPath = "/tmp/sts2-damage-overlay-selftest.png";
    private static readonly Dictionary<NCombatUi, DamageOverlayController> Controllers = new();
    private static Harmony? _harmony;
    private static bool SelfTestEnabled => System.Environment.GetEnvironmentVariable(SelfTestEnvVar) == "1";
    private static bool _selfTestStarted;
    private static bool _selfTestCombatEntered;
    private static bool _selfTestScreenshotTaken;
    private static int _selfTestCombatCount;
    private static bool _selfTestFirstCombatResolved;
    private static bool _selfTestSecondCombatQueued;

    public static void ModLoaded()
    {
        _harmony ??= new Harmony(HarmonyId);
        _harmony.PatchAll(typeof(DamageOverlayMod).Assembly);
        DamageRunTracker.Initialize();
        CombatManager.Instance.CombatEnded += OnCombatEnded;
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

    private static void OnCombatEnded(CombatRoom room)
    {
        if (!SelfTestEnabled)
        {
            return;
        }

        Log.Info($"[DamageByRound] Self-test observed combat end after combat-ui count {_selfTestCombatCount}");

        if (_selfTestCombatCount == 1 && !_selfTestFirstCombatResolved)
        {
            _selfTestFirstCombatResolved = true;
            TaskHelper.RunSafely(QueueSecondCombatAsync(room));
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
            DamageRunTracker.CommitBeforeHistoryReset(__instance.DebugOnlyGetState(), __instance.History);
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

    [HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
    private static class MainMenuReadyPatch
    {
        private static void Postfix()
        {
            if (!SelfTestEnabled || _selfTestStarted)
            {
                return;
            }

            _selfTestStarted = true;
            TaskHelper.RunSafely(SelfTestFlowAsync());
        }
    }

    [HarmonyPatch(typeof(NCombatUi), nameof(NCombatUi.Activate))]
    private static class CombatUiSelfTestPatch
    {
        private static void Postfix(NCombatUi __instance, CombatState state)
        {
            if (!SelfTestEnabled)
            {
                return;
            }

            _selfTestCombatCount++;
            Log.Info($"[DamageByRound] Self-test combat UI activate count {_selfTestCombatCount}");

            if (_selfTestCombatCount == 1)
            {
                TaskHelper.RunSafely(ResolveSelfTestCombatAsync(state));
                return;
            }

            if (_selfTestCombatCount == 2 && !_selfTestScreenshotTaken)
            {
                TaskHelper.RunSafely(CaptureSelfTestScreenshotAsync(__instance));
            }
        }
    }

    private static async Task CaptureSelfTestScreenshotAsync(NCombatUi combatUi)
    {
        await Task.Delay(1500);
        await combatUi.ToSignal(combatUi.GetTree(), SceneTree.SignalName.ProcessFrame);
        var image = combatUi.GetViewport().GetTexture().GetImage();
        image.SavePng(SelfTestScreenshotPath);
        _selfTestScreenshotTaken = true;
        Log.Info($"[DamageByRound] Self-test screenshot saved to {SelfTestScreenshotPath}");
    }

    private static async Task QueueSecondCombatAsync(CombatRoom room)
    {
        if (_selfTestSecondCombatQueued)
        {
            return;
        }

        _selfTestSecondCombatQueued = true;
        await Task.Delay(1500);

        if (RunManager.Instance.IsInProgress)
        {
            await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Monster, model: null, showTransition: false);
            Log.Info("[DamageByRound] Self-test entered second combat");
        }
    }

    private static async Task ResolveSelfTestCombatAsync(CombatState state)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (CombatManager.Instance.IsInProgress
                && CombatManager.Instance.IsPlayPhase
                && state.CurrentSide == CombatSide.Player
                && state.Enemies.Count > 0
                && state.Players.Count > 0)
            {
                break;
            }

            await Task.Delay(200);
        }

        if (!CombatManager.Instance.IsInProgress
            || !CombatManager.Instance.IsPlayPhase
            || state.CurrentSide != CombatSide.Player
            || state.Enemies.Count == 0
            || state.Players.Count == 0)
        {
            Log.Warn("[DamageByRound] Self-test could not find active player combat in time");
            return;
        }

        var attacker = state.Players[0].Creature;
        var targets = state.Enemies.Where(enemy => enemy.IsAlive).ToList();
        if (targets.Count == 0)
        {
            Log.Warn("[DamageByRound] Self-test could not find a living enemy");
            return;
        }

        await CreatureCmd.Damage(new BlockingPlayerChoiceContext(), targets, 999m, ValueProp.Unpowered, attacker, null);
        await CombatManager.Instance.CheckWinCondition();
        Log.Info($"[DamageByRound] Self-test resolved combat {_selfTestCombatCount} with player-attributed damage");
    }

    private static async Task SelfTestFlowAsync()
    {
        try
        {
            await Task.Delay(1500);
            var game = MegaCrit.Sts2.Core.Nodes.NGame.Instance;
            if (game == null)
            {
                Log.Warn("[DamageByRound] Self-test could not find NGame instance");
                return;
            }

            var seed = "DAMAGEBYROUNDSELFTEST";
            var acts = ActModel.GetDefaultList();
            await game.StartNewSingleplayerRun(ModelDb.Character<MegaCrit.Sts2.Core.Models.Characters.Ironclad>(), shouldSave: false, acts, Array.Empty<ModifierModel>(), seed);

            await Task.Delay(1200);
            if (!_selfTestCombatEntered)
            {
                _selfTestCombatEntered = true;
                await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Monster, model: null, showTransition: false);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[DamageByRound] Self-test failed: {ex}");
        }
    }
}

internal static class DamageRunTracker
{
    private static readonly Dictionary<ulong, int> ActTotals = new();
    private static readonly Dictionary<ulong, int> RunTotals = new();
    private static readonly Dictionary<ulong, int> CurrentCombatTotals = new();
    private static Dictionary<ulong, int> _latestCombatTotals = new();
    private static CombatHistory? _activeHistory;
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
        _latestCombatTotals = new Dictionary<ulong, int>();

        _activeHistory = history;
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

        _activeHistory = null;
        _activeState = null;
        CurrentCombatTotals.Clear();
        _latestCombatTotals = new Dictionary<ulong, int>();
    }

    public static void CommitBeforeHistoryReset(CombatState? state, CombatHistory history)
    {
        if (_activeCombatCommitted || state == null)
        {
            return;
        }

        var damageEntries = history.Entries.OfType<DamageReceivedEntry>().ToList();
        Log.Info($"[DamageByRound] CommitBeforeHistoryReset sees {damageEntries.Count} damage entries");
        foreach (var entry in damageEntries.Take(8))
        {
            Log.Info($"[DamageByRound] Entry round={entry.RoundNumber} dealer_player={entry.Dealer?.IsPlayer} dealer={entry.Dealer} receiver_enemy={entry.Receiver.IsEnemy} amount={entry.Result.TotalDamage}");
        }

        var totals = BuildTotals(state, history);
        if (totals.Count > 0)
        {
            CommitTotals(totals);
            _activeCombatCommitted = true;
            Log.Info($"[DamageByRound] Committed combat totals before history reset: {string.Join(", ", totals.Select(pair => $"{pair.Key}={pair.Value}"))}");
        }
        else
        {
            Log.Warn("[DamageByRound] CommitBeforeHistoryReset computed zero totals");
        }

        _activeHistory = null;
        _activeState = null;
        _latestCombatTotals = new Dictionary<ulong, int>();
    }

    public static DamageSnapshot BuildSnapshot(CombatState state, CombatHistory history)
    {
        SyncAct(state);
        _activeState = state;

        var rounds = BuildRounds(state, history);
        var combatTotals = new Dictionary<ulong, int>(CurrentCombatTotals);
        _latestCombatTotals = new Dictionary<ulong, int>(combatTotals);
        var actTotals = MergeTotals(ActTotals, combatTotals);
        var runTotals = MergeTotals(RunTotals, combatTotals);
        return new DamageSnapshot(rounds, combatTotals, actTotals, runTotals);
    }

    private static void OnRunStarted(RunState state)
    {
        ActTotals.Clear();
        RunTotals.Clear();
        _activeHistory = null;
        _activeState = null;
        CurrentCombatTotals.Clear();
        _latestCombatTotals = new Dictionary<ulong, int>();
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
        _activeHistory = null;
        _activeState = null;
        CurrentCombatTotals.Clear();
        _latestCombatTotals = new Dictionary<ulong, int>();
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
            _activeHistory = null;
            _activeState = null;
            CurrentCombatTotals.Clear();
            _latestCombatTotals = new Dictionary<ulong, int>();
            _activeCombatCommitted = false;
        }
    }

    private static void OnCombatEnded(CombatRoom _)
    {
        if (_activeCombatCommitted)
        {
            return;
        }

        if (CurrentCombatTotals.Count > 0)
        {
            CommitTotals(CurrentCombatTotals);
            _activeCombatCommitted = true;
            Log.Info($"[DamageByRound] Committed combat totals on CombatEnded: {string.Join(", ", CurrentCombatTotals.Select(pair => $"{pair.Key}={pair.Value}"))}");
        }

        _activeHistory = null;
        _activeState = null;
        CurrentCombatTotals.Clear();
        _latestCombatTotals = new Dictionary<ulong, int>();
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

        CurrentCombatTotals[player.NetId] = CurrentCombatTotals.GetValueOrDefault(player.NetId) + result.TotalDamage;
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
