using Sharp.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Extensions.GameEventManager;
using Sharp.Modules.ClientPreferences.Shared;
using Sharp.Shared.Abstractions;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameEvents;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace CenterSpeed;

public class CenterSpeed : IModSharpModule, IGameListener, IClientListener
{
    string IModSharpModule.DisplayName => "Center Speed";
    string IModSharpModule.DisplayAuthor => "Lethal & Retro";

    int IGameListener.ListenerVersion => IGameListener.ApiVersion;
    int IGameListener.ListenerPriority => 0;

    int IClientListener.ListenerVersion => IClientListener.ApiVersion;
    int IClientListener.ListenerPriority => 0;

    private readonly string _sharpPath;
    private readonly ISharedSystem _sharedSystem;
    private readonly IClientManager _clientManager;
    private readonly ITransmitManager _transmitManager;
    private readonly ILogger<CenterSpeed> _logger;
    private readonly IModSharp _modSharp;
    private readonly IEntityManager _entityManager;
    private readonly IHookManager _hookManager;
    private readonly ISharpModuleManager _modules;
    private readonly IServiceProvider _serviceProvider;
    private readonly IGameEventManager _gameEventManager;
    private IModSharpModuleInterface<IClientPreference>? _cachedInterface;
    private IDisposable? _callback;

    // --- Per-player HUD state ---
    private readonly PlayerHudState?[] _huds = new PlayerHudState?[64];
    private readonly PlayerHudSettings?[] _playerSettings = new PlayerHudSettings?[64];
    private readonly float[] _lastSpeed = new float[64];
    private IConVar? _particleConVar;

    private readonly Dictionary<int, int> _digitMap = new()
    {
        [0] = 1,
        [1] = 2,
        [2] = 4,
        [3] = 5,
        [4] = 7,
        [5] = 8,
        [6] = 10,
        [7] = 11,
        [8] = 12,
        [9] = 13,
    };

    private class PlayerHudSettings
    {
        public float[] DigitOffsets = { -1.4f, -0.45f, 0.45f, 1.4f };
        public float HudScale = 0.04f;
        public float YOffset = -1f;
        public bool Enabled = false;
    }

    private class PlayerHudState
    {
        // Index 0 = thousands, 1 = hundreds, 2 = tens, 3 = ones
        public IBaseParticle?[] Digits { get; } = new IBaseParticle?[4];
        public IBaseEntity? Target { get; set; } = null;
    }

    public CenterSpeed(
        ISharedSystem sharedSystem,
        string dllPath,
        string sharpPath,
        Version version,
        IConfiguration configuration,
        bool hotReload)
    {
        _sharedSystem = sharedSystem;
        _clientManager = sharedSystem.GetClientManager();
        _entityManager = sharedSystem.GetEntityManager();
        _modSharp = sharedSystem.GetModSharp();
        _logger = sharedSystem.GetLoggerFactory().CreateLogger<CenterSpeed>();
        _transmitManager = sharedSystem.GetTransmitManager();
        _hookManager = sharedSystem.GetHookManager();
        _modules = sharedSystem.GetSharpModuleManager();
        _sharpPath = sharpPath;

        var services = new ServiceCollection();
        services.AddSingleton(sharedSystem);
        services.AddGameEventManager();
        _serviceProvider = services.BuildServiceProvider();
        _gameEventManager = _serviceProvider.GetRequiredService<IGameEventManager>();
    }

    public bool Init()
    {
        _clientManager.InstallClientListener(this);
        _modSharp.InstallGameListener(this);

        var convarManager = _sharedSystem.GetConVarManager();
        _particleConVar = convarManager.CreateConVar("ms_cspeed_particle", "particles/digits_x/digits_x.vpcf");

        _clientManager.InstallCommandCallback("hud", OnHudSettingsCommand);

        _logger.LogInformation("CenterSpeed loaded");

        _hookManager.PlayerRunCommand.InstallHookPost(PlayerRunCommandPost);
        _hookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawned);
        _hookManager.PlayerKilledPost.InstallForward(OnPlayerKilled);
        _hookManager.HandleCommandJoinTeam.InstallHookPost(OnPlayerTeamChanged);

        _serviceProvider.LoadAllSharpExtensions();
        _gameEventManager.ListenEvent("round_end", OnRoundEnd);

        _clientManager.InstallCommandCallback("test", (client, command) =>
        {

            var controller = client.GetPlayerController();
            if (controller is null) return ECommandAction.Stopped;
            var hud = _huds[client.Slot];
            if (hud is null) return ECommandAction.Stopped;

            foreach (var digit in hud.Digits)
            {
                if (digit is null) continue;
                
                var state = _transmitManager.GetEntityState(digit.Index, controller.Index);
                _logger.LogInformation("Can {name} see digit {value} ", client.Name, state);
            }
            
            
            return ECommandAction.Stopped;
        });
        
        return true;
    }

    private void OnPlayerTeamChanged(IHandleCommandJoinTeamHookParams param, HookReturnValue<bool> ret)
    {
        _modSharp.InvokeAction(() => SetHudVisibility(param.Client.Slot));
    }

    public void Shutdown()
    {
        _serviceProvider.ShutdownAllSharpExtensions();
        _clientManager.RemoveClientListener(this);
        _sharedSystem.GetModSharp().RemoveGameListener(this);

        _hookManager.PlayerRunCommand.RemoveHookPost(PlayerRunCommandPost);
        _hookManager.PlayerSpawnPost.RemoveForward(OnPlayerSpawned);
        _callback?.Dispose();

        // Drop references only — game engine owns the entities.
        for (var i = 0; i < 64; i++)
            _huds[i] = null;
    }

    // -------------------------------------------------------------------------
    // Game listener

    private void OnRoundEnd(IGameEvent e)
    {
        _logger.LogInformation("CenterSpeed OnRoundEnd — invalidating HUD state");
        for (var i = 0; i < 64; i++)
            _huds[i] = null;
    }

    private void EnsureHudForSlot(int slot)
    {
        if (_huds[slot] != null) return;
        var controller = _entityManager.FindPlayerControllerBySlot((PlayerSlot) slot);
        if (controller is null || controller.IsFakeClient) return;

        var state = new PlayerHudState();
        
        var targetkv = new Dictionary<string, KeyValuesVariantValueItem> { ["origin"] = "0.0 1.0 0.5" };
        var target = _entityManager.SpawnEntitySync<IBaseEntity>("info_target", targetkv);
        if (target is null || !target.IsValid())
        {
            _logger.LogWarning("Failed to create target");
            return;
        }
        state.Target = target;
        _transmitManager.AddEntityHooks(target, false);
        
        var particleName = _particleConVar?.GetString() ?? "particles/digits_x/digits_x.vpcf";
        var defaults = new PlayerHudSettings();

        for (var i = 0; i < 4; i++)
        {
            var kv = new Dictionary<string, KeyValuesVariantValueItem>
            {
                ["effect_name"] = particleName,
                ["start_active"] = "0"
            };
            var particle = _entityManager.SpawnEntitySync<IBaseParticle>("info_particle_system", kv);
            if (particle == null)
            {
                _logger.LogWarning("EnsureHudForSlot: failed to spawn digit {Index} for slot {Slot}", i, slot);
                continue;
            }

            particle.GetControlPointEntities()[17] = target.Handle;
            particle.DataControlPoint = 33;
            particle.DataControlPointValue = new Vector(defaults.DigitOffsets[i], defaults.YOffset, 0f);
            SetControlPointValue(particle, 32, new Vector(0f, 0f, 0f));
            SetControlPointValue(particle, 34, new Vector(0f, 0f, 0f));
            SetControlPointValue(particle, 16, new Vector(255f, 255f, 255f));
            particle.AcceptInput("Start");
            particle.Active = true;

            state.Digits[i] = particle;
            _transmitManager.AddEntityHooks(particle, false);
            _transmitManager.SetEntityOwner(particle.Index, controller.Index);
        }

        _huds[slot] = state;
    }

    public void OnGameDeactivate()
    {
        // Game destroys entities on map end — just drop our references.
        for (var i = 0; i < 64; i++)
            _huds[i] = null;
    }

    // -------------------------------------------------------------------------
    // Client listener

    public void OnClientPostAdminCheck(IGameClient client)
    {
        _playerSettings[client.Slot] = new PlayerHudSettings();
    }

    private void OnPlayerSpawned(IPlayerSpawnForwardParams param)
    {
        var client = param.Client;
        if (!client.IsValid || client.IsFakeClient) return;
        if (client.GetPlayerController()?.Team < CStrikeTeam.TE) return;

        EnsureHudForSlot(client.Slot);
        ApplyHudSettings(client.Slot);
        _logger.LogInformation("CenterSpeed OnPlayerSpawned");
        SetHudVisibility(client.Slot);
    }

    private void OnPlayerKilled(IPlayerKilledForwardParams param)
    {
        _logger.LogInformation("CenterSpeed OnPlayerKilled");
        SetHudVisibility(param.Client.Slot);
    }

    public void OnClientDisconnected(IGameClient client, NetworkDisconnectionReason reason)
    {
        _playerSettings[client.Slot] = null;
    }

    public void OnClientDisconnecting(IGameClient client, NetworkDisconnectionReason reason)
    {
        SetHudVisibility(client.Slot);
    }

    // -------------------------------------------------------------------------
    // HUD management

    /// <summary>
    /// Pushes current settings (digit offsets, yoffset, scale) onto the already-existing
    /// particle entities for this slot. No entity creation or destruction.
    /// </summary>
    private void ApplyHudSettings(int slot)
    {
        var state = _huds[slot];
        var settings = _playerSettings[slot];
        if (state == null || settings == null) return;

        for (var i = 0; i < 4; i++)
        {
            var particle = state.Digits[i];
            if (particle == null || !particle.IsValid()) continue;

            particle.DataControlPointValue = new Vector(settings.DigitOffsets[i], settings.YOffset, 0f);
            SetControlPointValue(particle, 34, new Vector(settings.HudScale, 0f, 0f));
        }
    }

    // -------------------------------------------------------------------------
    // Per-tick digit update

    private void PlayerRunCommandPost(IPlayerRunCommandHookParams param, HookReturnValue<EmptyHookReturn> retValue)
    {
        if (_modSharp.GetGlobals().TickCount % 10 == 0)
            return;

        var client = param.Client;
        var slot = client.Slot;
        var state = _huds[slot];
        if (state == null) return;

        var controller = client.GetPlayerController();
        if (controller == null || controller.ConnectedState != PlayerConnectedState.PlayerConnected)
            return;
        if (controller.Team < CStrikeTeam.TE) return;

        var speed = 0;
        var pawn = controller.GetPlayerPawn();
        if (pawn != null)
        {
            var v = pawn.GetAbsVelocity().Length2D();
            speed = (int)Math.Clamp(v, 0f, 9999f);
        }

        var digits = new[]
        {
            speed / 1000,
            speed / 100 % 10,
            speed / 10  % 10,
            speed       % 10
        };

        for (var i = 0; i < 4; i++)
        {
            var particle = state.Digits[i];
            if (particle == null) continue;

            SetControlPointValue(particle, 32, new Vector(_digitMap.GetValueOrDefault(digits[i], 1), 0f, 0f));

            if (_lastSpeed[slot] > speed)
                SetControlPointValue(particle, 16, new Vector(255f, 0f, 0f));
            else if (_lastSpeed[slot] < speed)
                SetControlPointValue(particle, 16, new Vector(0f, 255f, 0f));
            else
                SetControlPointValue(particle, 16, new Vector(255f, 255f, 255f));
        }

        _lastSpeed[slot] = speed;
    }
    
    private void SetHudVisibility(PlayerSlot slot)
    {
        var controller = _entityManager.FindPlayerControllerBySlot(slot);
        if (controller == null) return;
        _logger.LogInformation("Found controller");
        
        var state = _huds[slot];
        if (state is null) return;
        _logger.LogInformation("Found state");
        var settings = _playerSettings[slot] ?? new();
        
        _logger.LogInformation("Name: {name}, Enabled: {enabled}, teamCheck: {teamCheck}({team}), IsAlive: {alive}", controller.PlayerName, settings.Enabled, controller.Team >= CStrikeTeam.TE, controller.Team, controller.GetPlayerPawn()?.IsAlive == true);
        
        var isVisible = settings.Enabled && controller.Team >= CStrikeTeam.TE && controller.GetPlayerPawn()?.IsAlive == true;
        _logger.LogInformation("Changing visibility for hud for {name} to {value}", controller.PlayerName, isVisible);

        _logger.LogInformation("Ensuring other huds are set to false");
        foreach (var hud in _huds)
        {
            if(hud == state) continue;
            if(state.Target is not null)
                _transmitManager.SetEntityState(state.Target.Index, controller.Index, false, -1);
            for (var i = 0; i < 4; i++)
            {
                var particle = state.Digits[i];
                if (particle == null) continue;
                _transmitManager.SetEntityState(particle.Index, controller.Index, false, -1);
            }
        }
        _logger.LogInformation("Ensure hud is set to correct visibility");
        if(state.Target is not null)
            _transmitManager.SetEntityState(state.Target.Index, controller.Index, isVisible, -1);
        for (var i = 0; i < 4; i++)
        {
            var particle = state.Digits[i];
            if (particle == null) continue;
            _logger.LogInformation("Found a non-null particle and valid is {valid} index is {index}", particle.IsValid(), particle.Index);
            particle.SetOwner(controller);
            _transmitManager.SetEntityState(particle.Index, controller.Index, isVisible, -1);
        }
    }

    // -------------------------------------------------------------------------
    // !hudsettings command

    private ECommandAction OnHudSettingsCommand(IGameClient client, StringCommand command)
    {
        var slot = client.Slot;
        var settings = _playerSettings[slot] ??= new PlayerHudSettings();

        if (command.ArgCount == 0 || command.GetArg(1).Equals("info", StringComparison.OrdinalIgnoreCase))
        {
            PrintHudSettings(client, settings);
            return ECommandAction.Stopped;
        }

        var sub = command.GetArg(1).ToLowerInvariant();

        if (sub == "offset")
        {
            if (command.ArgCount < 3 ||
                !int.TryParse(command.GetArg(2), out var index1) ||
                !float.TryParse(command.GetArg(3), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Usage: !hudsettings offset <1-4> <-10 to 10>");
                return ECommandAction.Stopped;
            }

            index1 = Math.Clamp(index1, 1, 4);
            value = Math.Clamp(value, -10f, 10f);
            settings.DigitOffsets[index1 - 1] = value;
            SaveSettings(client.SteamId, settings);
            ApplyHudSettings(slot);
            client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Digit {index1} offset set to {value:F2}");
        }
        else if (sub == "scale")
        {
            if (command.ArgCount < 2 ||
                !float.TryParse(command.GetArg(2), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Usage: !hudsettings scale <0-10>");
                return ECommandAction.Stopped;
            }

            value = Math.Clamp(value, 0f, 10f);
            settings.HudScale = value;
            SaveSettings(client.SteamId, settings);
            ApplyHudSettings(slot);
            client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Scale set to {value:F2}");
        }
        else if (sub == "yoffset")
        {
            if (command.ArgCount < 2 ||
                !float.TryParse(command.GetArg(2), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var offset))
            {
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Usage: !hudsettings yoffset <-10-10>");
                return ECommandAction.Stopped;
            }

            offset = Math.Clamp(offset, -10f, 10f);
            settings.YOffset = offset;
            SaveSettings(client.SteamId, settings);
            ApplyHudSettings(slot);
            client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Y-Offset set to {offset:F2}");
        }
        else if (sub == "toggle")
        {
            settings.Enabled = !settings.Enabled;
            SaveSettings(client.SteamId, settings);
            _logger.LogInformation("Setting hud visiblity in command to {enabled} for {name}", settings.Enabled, client.Name);
            SetHudVisibility(slot);
            client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Enabled set to {settings.Enabled}");
        }
        else
        {
            client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Subcommands: offset <1-4> <-10..10> | scale <0-10> | yoffset <-10-10> | toggle | info");
        }

        return ECommandAction.Stopped;
    }

    private void PrintHudSettings(IGameClient client, PlayerHudSettings settings)
    {
        var o = settings.DigitOffsets;
        client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Offsets: 1={o[0]:F2}  2={o[1]:F2}  3={o[2]:F2}  4={o[3]:F2}");
        client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Scale: {settings.HudScale:F4}");
        client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Y-Offset: {settings.YOffset:F4}");
        client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Enabled: {settings.Enabled}");
    }

    // -------------------------------------------------------------------------
    // ClientPrefs integration

    public void OnAllModulesLoaded()
    {
        _cachedInterface = _modules.GetOptionalSharpModuleInterface<IClientPreference>(IClientPreference.Identity);
        if (_cachedInterface?.Instance is { } instance)
            _callback = instance.ListenOnLoad(OnCookieLoad);
    }

    public void OnLibraryConnected(string name)
    {
        if (!name.Equals("ClientPreferences")) return;
        _cachedInterface = _modules.GetRequiredSharpModuleInterface<IClientPreference>(IClientPreference.Identity);
        if (_cachedInterface?.Instance is { } instance)
            _callback = instance.ListenOnLoad(OnCookieLoad);
    }

    public void OnLibraryDisconnect(string name)
    {
        if (!name.Equals("ClientPreferences")) return;
        _cachedInterface = null;
    }

    private IClientPreference? GetInterface()
    {
        if (_cachedInterface?.Instance is null)
        {
            _cachedInterface = _modules.GetOptionalSharpModuleInterface<IClientPreference>(IClientPreference.Identity);
            if (_cachedInterface?.Instance is { } instance)
                _callback = instance.ListenOnLoad(OnCookieLoad);
        }
        return _cachedInterface?.Instance;
    }

    private void OnCookieLoad(IGameClient client)
    {
        if (GetInterface() is not { } cp) return;

        var settings = _playerSettings[client.Slot] ??= new PlayerHudSettings();
        var id = client.SteamId;

        for (var i = 0; i < 4; i++)
        {
            if (cp.GetCookie(id, $"hud_d{i}") is { } c &&
                float.TryParse(c.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                settings.DigitOffsets[i] = v;
        }

        if (cp.GetCookie(id, "hud_scale") is { } sc &&
            float.TryParse(sc.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var scale))
            settings.HudScale = scale;

        if (cp.GetCookie(id, "hud_yoffset") is { } yo &&
            float.TryParse(yo.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var yoffset))
            settings.YOffset = yoffset;

        if (cp.GetCookie(id, "hud_enabled") is { } en)
            settings.Enabled = en.GetString() != "0";

        ApplyHudSettings(client.Slot);
        _logger.LogInformation("Setting hud visiblity for {name} to {value}", client.Name, settings.Enabled);
        SetHudVisibility(client.Slot);
    }

    private void SaveSettings(ulong steamId, PlayerHudSettings s)
    {
        if (GetInterface() is not { } cp) return;

        for (var i = 0; i < 4; i++)
            cp.SetCookie(steamId, $"hud_d{i}",
                s.DigitOffsets[i].ToString("F4", System.Globalization.CultureInfo.InvariantCulture));

        cp.SetCookie(steamId, "hud_scale",
            s.HudScale.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
        cp.SetCookie(steamId, "hud_yoffset",
            s.YOffset.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
        cp.SetCookie(steamId, "hud_enabled", s.Enabled ? "1" : "0");
    }

    // -------------------------------------------------------------------------
    // Helpers

    private bool SetControlPointValue(IBaseParticle particle, int cpIndex, Vector value)
    {
        var assignments = particle.GetServerControlPointAssignments();
        var controlPoints = particle.GetServerControlPoints();

        for (var i = 0; i < 4; i++)
        {
            if (assignments[i] == cpIndex || assignments[i] == 255)
            {
                assignments[i] = (byte)cpIndex;
                controlPoints[i] = value;
                return true;
            }
        }

        _logger.LogWarning("No free server controlled control points for CP {CpIndex}", cpIndex);
        return false;
    }

    public void OnResourcePrecache()
    {
        var assetPath = Path.Combine(_sharpPath, "assets");

        if (!Directory.Exists(assetPath))
            return;

        var files = Directory.EnumerateFiles(assetPath, "*", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var relative = file[(assetPath.Length + 1)..].Replace("\\", "/");

            if (!relative.StartsWith("particles/", StringComparison.OrdinalIgnoreCase))
                continue;

            var asset = relative.EndsWith("_c", StringComparison.OrdinalIgnoreCase)
                ? relative[..^2]
                : relative;

            _modSharp.PrecacheResource(asset);
        }
    }
}
