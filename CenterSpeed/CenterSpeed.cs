using Sharp.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Modules.ClientPreferences.Shared;
using Sharp.Modules.MenuManager.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using System;
using System.Collections.Generic;
using System.Linq;

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
    private IModSharpModuleInterface<IClientPreference>? _cachedInterface;
    private IDisposable? _callback;
    private IMenuManager? _menuManager;

    // --- Per-player HUD state ---
    private readonly PlayerHudState?[] _huds = new PlayerHudState?[64];
    private readonly PlayerHudSettings?[] _playerSettings = new PlayerHudSettings?[64];
    private float[] _lastSpeed = new float[64];
    private IBaseEntity? _sharedTarget;
    private IConVar? _particleConVar;

    // Adjustment increments
    private const float XOffsetIncrement = 0.1f;
    private const float YOffsetIncrement = 0.1f;
    private const float ScaleIncrement = 0.005f;

    private Dictionary<int, int> _digitMap = new()
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
        public bool IsDisposed = false;
        public IBaseParticle?[] Digits { get; } = new IBaseParticle?[4];
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
    }

    private IMenuManager MenuManager => _menuManager ??= _sharedSystem.GetSharpModuleManager()
        .GetRequiredSharpModuleInterface<IMenuManager>(IMenuManager.Identity)
        .Instance!;

    public bool Init()
    {
        _clientManager.InstallClientListener(this);
        _sharedSystem.GetModSharp().InstallGameListener(this);

        var convarManager = _sharedSystem.GetConVarManager();
        _particleConVar = convarManager.CreateConVar("ms_cspeed_particle", "particles/digits_x/digits_x.vpcf");

        _clientManager.InstallCommandCallback("hud", OnHudSettingsCommand);

        _logger.LogInformation("CenterSpeed loaded");

        _hookManager.PlayerRunCommand.InstallHookPost(PlayerRunCommandPost);
        _hookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawned);
        _hookManager.PlayerKilledPost.InstallForward(OnPlayerKilled);
        _hookManager.HandleCommandJoinTeam.InstallHookPost(OnPlayerTeamChanged);

        return true;
    }

    private void OnPlayerTeamChanged(IHandleCommandJoinTeamHookParams param, HookReturnValue<bool> ret)
    {
        KillPlayerHud(param.Client.Slot);
    }

    public void Shutdown()
    {
        _clientManager.RemoveClientListener(this);
        _sharedSystem.GetModSharp().RemoveGameListener(this);

        _hookManager.PlayerRunCommand.RemoveHookPost(PlayerRunCommandPost);
        _hookManager.PlayerSpawnPost.RemoveForward(OnPlayerSpawned);
        _callback?.Dispose();

        for (var i = 0; i < 64; i++)
            KillPlayerHud(i);

        _sharedTarget?.AcceptInput("DestroyImmediately");
        _sharedTarget = null;
    }

    // -------------------------------------------------------------------------
    // Game listener

    public void OnGameDeactivate()
    {
        // The game cleans up entities itself — just drop our references.
        for (var i = 0; i < 64; i++)
            _huds[i] = null;

        _sharedTarget = null;
    }

    // -------------------------------------------------------------------------
    // Client listener

    public void OnClientPostAdminCheck(IGameClient client)
    {
        _playerSettings[client.Slot] = new();
    }

    private void OnPlayerSpawned(IPlayerSpawnForwardParams param)
    {
        SpawnPlayerHud(param.Client);
    }

    private void OnPlayerKilled(IPlayerKilledForwardParams param)
    {
        KillPlayerHud(param.Client.Slot);
    }

    public void OnClientDisconnected(IGameClient client, NetworkDisconnectionReason reason)
    {
        KillPlayerHud(client.Slot);
        _playerSettings[client.Slot] = null;
    }

    // -------------------------------------------------------------------------
    // HUD management

    private void SpawnPlayerHud(IGameClient client)
    {
        if (!client.IsValid || client.IsFakeClient)
            return;

        var slot = (byte)client.Slot;
        if (client.GetPlayerController()?.Team < CStrikeTeam.TE)
            return;

        KillPlayerHud(slot); // clear any stale state

        if (_sharedTarget is null || !_sharedTarget.IsValid())
        {
            var targetKv = new Dictionary<string, KeyValuesVariantValueItem>
            {
                ["origin"] = "0.0 1.0 0.5"
            };
            var target = _sharedSystem.GetEntityManager()
                .SpawnEntitySync<IBaseEntity>("info_target", targetKv);

            if (target == null)
            {
                _logger.LogWarning("SpawnPlayerHud(target): failed to create shared target");
                return;
            }
            _sharedTarget = target;
        }

        var state = new PlayerHudState();
        var settings = _playerSettings[client.Slot];
        if (settings is null)
        {
            settings = new PlayerHudSettings();
            _playerSettings[client.Slot] = settings;
        }

        if (!settings.Enabled) return;

        var particleName = _particleConVar?.GetString() ?? "particles/numbers/number_x.vpcf";

        for (var i = 0; i < 4; i++)
        {
            var kv = new Dictionary<string, KeyValuesVariantValueItem>
            {
                ["effect_name"] = particleName,
                ["start_active"] = "0"
            };

            var particle = _sharedSystem.GetEntityManager()
                                        .SpawnEntitySync<IBaseParticle>("info_particle_system", kv);

            if (particle == null)
            {
                _logger.LogWarning("SpawnPlayerHud: failed to spawn digit {Index} for slot {Slot}", i, slot);
                continue;
            }

            particle.GetControlPointEntities()[17] = _sharedTarget.Handle;

            particle.DataControlPoint = 33;
            particle.DataControlPointValue = new Vector(settings.DigitOffsets[i], settings.YOffset, 0f);

            SetControlPointValue(particle, 32, new Vector(0f, 0f, 0f)); // digit frame (0)
            SetControlPointValue(particle, 34, new Vector(settings.HudScale, 0f, 0f)); // scale
            SetControlPointValue(particle, 16, new Vector(255f, 255f, 255f)); // color

            particle.AcceptInput("Start");
            particle.Active = true;

            state.Digits[i] = particle;
            _transmitManager.AddEntityHooks(particle, false);
        }

        // Set visibility: only visible to the owning player
        foreach (var con in _entityManager.GetPlayerControllers(true))
        {
            for (var i = 0; i < 4; i++)
            {
                if (state.Digits[i] == null) continue;
                bool shouldSee = (con.PlayerSlot == slot);
                _transmitManager.SetEntityState(state.Digits[i].Index, con.Index, shouldSee, -1);
            }
        }

        _huds[slot] = state;
    }

    private void KillPlayerHud(int slot)
    {
        var state = _huds[slot];
        if (state == null) return;

        state.IsDisposed = true;
        _huds[slot] = null;
        _lastSpeed[slot] = 0;

        foreach (var particle in state.Digits)
        {
            if (particle == null || !particle.IsValid()) continue;

            particle.AcceptInput("Stop");
            particle.AcceptInput("DestroyImmediately");
            particle.Active = false;
        }
    }

    // -------------------------------------------------------------------------
    // Update timer — runs every 0.1 s

    private void PlayerRunCommandPost(IPlayerRunCommandHookParams param, HookReturnValue<EmptyHookReturn> retValue)
    {
        if (_modSharp.GetGlobals().TickCount % 4 != 0)
            return;

        var client = param.Client;
        var state = _huds[client.Slot];

        if (client.GetPlayerController()?.Team < CStrikeTeam.TE)
        {
            KillPlayerHud(client.Slot);
            return;
        }

        if (state == null || state.IsDisposed) return;

        var controller = client.GetPlayerController();
        if (controller == null || controller.ConnectedState != PlayerConnectedState.PlayerConnected)
            return;

        // Default to 0 so dead/spectating players show "0000".
        var speed = 0;
        var pawn = controller.GetPlayerPawn();

        if (pawn != null)
        {
            var v = pawn.GetAbsVelocity().Length2D();
            speed = (int)Math.Clamp(v, 0f, 9999f);
        }
        var digits = new int[4]
        {
            speed / 1000,
            speed / 100  % 10,
            speed / 10   % 10,
            speed        % 10
        };

        // Update digit frames.
        for (var i = 0; i < 4; i++)
        {
            var particle = state.Digits[i];
            if (particle == null || state.IsDisposed)
            {
                continue;
            }

            var digit = _digitMap.GetValueOrDefault(digits[i], 1);

            SetControlPointValue(particle, 32, new Vector((float)digit, 0f, 0f));
            if (_lastSpeed[client.Slot] > speed)
            {
                SetControlPointValue(particle, 16, new Vector(255f, 0f, 0f));
            }
            else if (_lastSpeed[client.Slot] < speed)
            {
                SetControlPointValue(particle, 16, new Vector(0f, 255f, 0f));
            }
            else
            {
                SetControlPointValue(particle, 16, new Vector(255f, 255f, 255f));
            }
        }

        _lastSpeed[client.Slot] = speed;
    }

    // -------------------------------------------------------------------------
    // !hud command - Menu entry point
    // -------------------------------------------------------------------------

    private ECommandAction OnHudSettingsCommand(IGameClient client, StringCommand command)
    {
        if (client == null)
            return ECommandAction.Stopped;

        Task.Delay(150).ContinueWith(_ =>
        {
            if (client.IsValid)
                ShowHudAdjustmentMenu(client);
        });

        return ECommandAction.Stopped;
    }

    // -------------------------------------------------------------------------
    // HUD Adjustment Menu - 6 directional options
    // -------------------------------------------------------------------------

    private void ShowHudAdjustmentMenu(IGameClient client)
    {
        var slot = client.Slot;
        var settings = _playerSettings[slot] ??= new PlayerHudSettings();

        var statusText = settings.Enabled ? "ON" : "OFF";
        var menu = new Menu();
        menu.SetTitle($"HUD Settings [{statusText}] - X:{settings.DigitOffsets[0]:F2} Y:{settings.YOffset:F2} Scale:{settings.HudScale:F3}");

        // Toggle - Turn On/Off
        var toggleLabel = settings.Enabled ? "Turn Off" : "Turn On";
        menu.AddItem(_ => toggleLabel, controller =>
        {
            settings.Enabled = !settings.Enabled;
            SaveSettings(client.SteamId, settings);

            if (settings.Enabled)
                SpawnPlayerHud(client);
            else
                KillPlayerHud(client.Slot);

            Reply(client, $"HUD is now {(settings.Enabled ? "enabled" : "disabled")}");
        });

        // Left - Decrease X offset
        menu.AddItem(_ => "Left (Move Left)", controller =>
        {
            for (var i = 0; i < 4; i++)
            {
                settings.DigitOffsets[i] -= XOffsetIncrement;
                settings.DigitOffsets[i] = Math.Clamp(settings.DigitOffsets[i], -10f, 10f);
            }
            SaveSettings(client.SteamId, settings);
            UpdateHudPositions(client);
        });

        // Right - Increase X offset
        menu.AddItem(_ => "Right (Move Right)", controller =>
        {
            for (var i = 0; i < 4; i++)
            {
                settings.DigitOffsets[i] += XOffsetIncrement;
                settings.DigitOffsets[i] = Math.Clamp(settings.DigitOffsets[i], -10f, 10f);
            }
            SaveSettings(client.SteamId, settings);
            UpdateHudPositions(client);
        });

        // Up - Increase Y offset
        menu.AddItem(_ => "Up (Move Up)", controller =>
        {
            settings.YOffset += YOffsetIncrement;
            settings.YOffset = Math.Clamp(settings.YOffset, -10f, 10f);
            SaveSettings(client.SteamId, settings);
            UpdateHudPositions(client);
        });

        // Down - Decrease Y offset
        menu.AddItem(_ => "Down (Move Down)", controller =>
        {
            settings.YOffset -= YOffsetIncrement;
            settings.YOffset = Math.Clamp(settings.YOffset, -10f, 10f);
            SaveSettings(client.SteamId, settings);
            UpdateHudPositions(client);
        });

        // Bigger - Increase scale
        menu.AddItem(_ => "Bigger (Increase Size)", controller =>
        {
            settings.HudScale += ScaleIncrement;
            settings.HudScale = Math.Clamp(settings.HudScale, 0f, 10f);
            SaveSettings(client.SteamId, settings);
            UpdateHudPositions(client);
        });

        // Smaller - Decrease scale
        menu.AddItem(_ => "Smaller (Decrease Size)", controller =>
        {
            settings.HudScale -= ScaleIncrement;
            settings.HudScale = Math.Clamp(settings.HudScale, 0f, 10f);
            SaveSettings(client.SteamId, settings);
            UpdateHudPositions(client);
        });

        MenuManager.DisplayMenu(client, menu);
    }

    // -------------------------------------------------------------------------
    // Update HUD positions without full respawn
    // -------------------------------------------------------------------------

    private void UpdateHudPositions(IGameClient client)
    {
        var slot = client.Slot;
        var state = _huds[slot];
        var settings = _playerSettings[slot];

        if (state == null || settings == null || state.IsDisposed)
            return;

        // Update all digit particles with new positions and scale
        for (var i = 0; i < 4; i++)
        {
            var particle = state.Digits[i];
            if (particle == null || !particle.IsValid())
                continue;

            // Update position (X and Y offsets) using control point 33
            SetControlPointValue(particle, 33, new Vector(settings.DigitOffsets[i], settings.YOffset, 0f));

            // Update scale using control point 34
            SetControlPointValue(particle, 34, new Vector(settings.HudScale, 0f, 0f));
        }
    }

    // -------------------------------------------------------------------------
    // Settings Display

    private void PrintHudSettings(IGameClient client, PlayerHudSettings settings)
    {
        var o = settings.DigitOffsets;
        Reply(client, $"[HUD] Status: {(settings.Enabled ? "Enabled" : "Disabled")}");
        Reply(client, $"[HUD] Offsets: 1={o[0]:F2}  2={o[1]:F2}  3={o[2]:F2}  4={o[3]:F2}");
        Reply(client, $"[HUD] Scale: {settings.HudScale:F4}");
        Reply(client, $"[HUD] Y-Offset: {settings.YOffset:F4}");
    }

    // -------------------------------------------------------------------------
    // Utility Methods

    private static void Reply(IGameClient client, string msg)
    {
        client.GetPlayerController()?.Print(HudPrintChannel.Chat, msg);
    }

    private void CloseMenu(IGameClient client)
    {
        var emptyMenu = new Menu();
        emptyMenu.SetTitle(""); // blank menu to close
        MenuManager.DisplayMenu(client, emptyMenu);
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
        
        SpawnPlayerHud(client);
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
        {
            return;
        }

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