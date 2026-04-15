using Sharp.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace ParticleTest;

public class ParticleTest : IModSharpModule, IGameListener, IClientListener
{
    string IModSharpModule.DisplayName   => "ParticleTest";
    string IModSharpModule.DisplayAuthor => "Lethal";

    int IGameListener.ListenerVersion  => IGameListener.ApiVersion;
    int IGameListener.ListenerPriority => 0;

    int IClientListener.ListenerVersion  => IClientListener.ApiVersion;
    int IClientListener.ListenerPriority => 0;

    private readonly string                _sharpPath;
    private readonly ISharedSystem         _sharedSystem;
    private readonly IClientManager        _clientManager;
    private readonly ITransmitManager      _transmitManager;
    private readonly ILogger<ParticleTest> _logger;
    private readonly IModSharp             _modSharp;
    private readonly IEntityManager _entityManager;
    private readonly IHookManager _hookManager;

    // --- Per-player HUD state ---
    private readonly PlayerHudState?[] _huds = new PlayerHudState?[64];
    private float[] _lastSpeed = new float[64];
    private IBaseEntity?               _sharedTarget;
    private IConVar? _particleConVar;
    private IConVar? _yOffsetConVar;

    // --- Layout constants (adjust to taste) ---
    private static readonly float[] DigitOffsets   = { -1.4f, -0.45f, 0.45f, 1.4f };
    // private static readonly float[] DigitOffsets   = { 0f, 0f, 0f, 0f };
    private const           float   HudScale       = 0.06f;
    
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

    private class PlayerHudState
    {
        // Index 0 = thousands, 1 = hundreds, 2 = tens, 3 = ones
        public IBaseParticle?[] Digits { get; } = new IBaseParticle?[4];
    }

    public ParticleTest(
        ISharedSystem  sharedSystem,
        string         dllPath,
        string         sharpPath,
        Version        version,
        IConfiguration configuration,
        bool           hotReload)
    {
        _sharedSystem    = sharedSystem;
        _clientManager   = sharedSystem.GetClientManager();
        _entityManager   = sharedSystem.GetEntityManager();
        _modSharp        = sharedSystem.GetModSharp();
        _logger          = sharedSystem.GetLoggerFactory().CreateLogger<ParticleTest>();
        _transmitManager = sharedSystem.GetTransmitManager();
        _hookManager     = sharedSystem.GetHookManager();
        _sharpPath       = sharpPath;
    }

    public bool Init()
    {
        _clientManager.InstallClientListener(this);
        _sharedSystem.GetModSharp().InstallGameListener(this);

        var convarManager = _sharedSystem.GetConVarManager();
        _particleConVar = convarManager.CreateConVar("ms_cspeed_particle", "particles/digits_x/digits_x.vpcf");
        _yOffsetConVar = convarManager.CreateConVar("ms_cspeed_y_offset", 0.0f);
        
        // _clientManager.InstallCommandCallback("ptest", OnParticleTestCommand);
        
        _logger.LogInformation("ParticleTest loaded");
        
        _hookManager.PlayerRunCommand.InstallHookPost(PlayerRunCommandPost);
        _hookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawned);
        _hookManager.PlayerKilledPost.InstallForward(OnPlayerKilled);
        
        OnResourcePrecache();
        return true;
    }

    public void Shutdown()
    {
        _clientManager.RemoveClientListener(this);
        _sharedSystem.GetModSharp().RemoveGameListener(this);
        
        _hookManager.PlayerRunCommand.RemoveHookPost(PlayerRunCommandPost);
        _hookManager.PlayerSpawnPost.RemoveForward(OnPlayerSpawned);

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
        _logger.LogInformation("Player joined setting 5 second timer");
        _modSharp.PushTimer(() => SpawnPlayerHud(client), 5.0f, GameTimerFlags.None);
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
    }

    // -------------------------------------------------------------------------
    // HUD management

    private void SpawnPlayerHud(IGameClient client)
    {
        if (!client.IsValid || client.IsFakeClient)
            return;

        var slot = (byte) client.Slot;

        if (slot >= 64)
        {
            _logger.LogWarning("SpawnPlayerHud: slot {Slot} out of range", slot);
            return;
        }

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
        

        // Lazy-init the one shared info_target (never modified after creation).
        _logger.LogInformation("Shared target spawn");

        _logger.LogInformation("Spawning player hud 5");
        
        var state = new PlayerHudState();

        var particleName = _particleConVar?.GetString() ?? "particles/numbers/number_x.vpcf";
        var yOffset = _yOffsetConVar?.GetFloat() ?? -3.0f;
        
        _logger.LogInformation($"Spawning player hud 6 {particleName} offset {yOffset}");

        for (var i = 0; i < 4; i++)
        {
            _logger.LogInformation($"Spawning player hud {i + 1} particle {particleName}");
            var kv = new Dictionary<string, KeyValuesVariantValueItem>
            {
                ["effect_name"]  = particleName,
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

            particle.DataControlPoint      = 33;
            particle.DataControlPointValue = new Vector(DigitOffsets[i], yOffset, 0f);

            SetControlPointValue(particle, 32, new Vector(0f,       0f,   0f)); // digit frame (0)
            SetControlPointValue(particle, 34, new Vector(HudScale, 0f,   0f)); // scale
            SetControlPointValue(particle, 16, new Vector(255f,     255f, 255f)); // color

            particle.AcceptInput("Start");
            particle.Active = true;

            state.Digits[i] = particle;
            _transmitManager.AddEntityHooks(particle, true);
        }

        _huds[slot] = state;
        _logger.LogInformation("Spawning player hud 9");
    }

    private void KillPlayerHud(int slot)
    {
        var state = _huds[slot];
        if (state == null) return;

        foreach (var particle in state.Digits)
            particle?.AcceptInput("DestroyImmediately");

        _huds[slot] = null;
    }

    // -------------------------------------------------------------------------
    // Update timer — runs every 0.1 s
    
    private void PlayerRunCommandPost(IPlayerRunCommandHookParams param, HookReturnValue<EmptyHookReturn> retValue)
    {
        if (_modSharp.GetGlobals().TickCount % 10 == 0)
            return;
        
        var client = param.Client;
        var state = _huds[client.Slot];

        if (client.GetPlayerController()?.Team < CStrikeTeam.TE)
        {
            KillPlayerHud(client.Slot);
            return;
        }
        
        if (state == null) return;

        var controller = client.GetPlayerController();
        if (controller == null || controller.ConnectedState != PlayerConnectedState.PlayerConnected)
            return;
            

        // Default to 0 so dead/spectating players show "0000".
        var speed = 0;
        var pawn  = controller.GetPlayerPawn();

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
        
        if(_modSharp.GetGlobals().TickCount % 10 == 0)
            _logger.LogInformation("Speed={speed}, digits=[{1}, {2}, {3}, {4}]", speed, digits[0], digits[1], digits[2], digits[3]);

        // Update digit frames.
        for (var i = 0; i < 4; i++)
        {
            var particle = state.Digits[i];
            if (particle == null)
            {
                _logger.LogWarning($"Got null particle at {i}");
                continue;
            }

            var digit = _digitMap.GetValueOrDefault(digits[i], 1);
            
            SetControlPointValue(particle, 32, new Vector((float) digit, 0f, 0f));
            if (_lastSpeed[client.Slot] > speed)
            {
                SetControlPointValue(particle, 16, new Vector(255f, 0f, 0f));
            }else if (_lastSpeed[client.Slot] < speed)
            {
                SetControlPointValue(particle, 16, new Vector(0f, 255f, 0f));
            }
            else
            {
                SetControlPointValue(particle, 16, new Vector(255f, 255f, 255f));
            }
        }
        
        _lastSpeed[client.Slot] = speed;

        

        // Transmit: visible only to the owning player.
        for (var i = 0; i < 4; i++)
        {
            var particle = state.Digits[i];
            if (particle == null) continue;
        
            foreach(var con in _entityManager.GetPlayerControllers(true).Where(con => !con.IsFakeClient))
                _transmitManager.SetEntityState(particle.Index, con.Index, con.PlayerSlot == client.Slot, -1);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers

    private bool SetControlPointValue(IBaseParticle particle, int cpIndex, Vector value)
    {
        var assignments   = particle.GetServerControlPointAssignments();
        var controlPoints = particle.GetServerControlPoints();

        for (var i = 0; i < 4; i++)
        {
            if (assignments[i] == cpIndex || assignments[i] == 255)
            {
                assignments[i]   = (byte) cpIndex;
                controlPoints[i] = value;
                return true;
            }
        }

        _logger.LogWarning("No free server controlled control points for CP {CpIndex}", cpIndex);
        return false;
    }

    public void OnResourcePrecache()
    {
        _logger.LogInformation("[ParticleTest] precache start");

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
        _logger.LogInformation("[ParticleTest] precache done");
    }
}
