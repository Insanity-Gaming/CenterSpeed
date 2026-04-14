using Sharp.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace ParticleTest;

public class ParticleTest : IModSharpModule, IGameListener, IClientListener
{
    string IModSharpModule.DisplayName => "ParticleTest";
    string IModSharpModule.DisplayAuthor => "Lethal";

    int IGameListener.ListenerVersion => IGameListener.ApiVersion;
    int IGameListener.ListenerPriority => 0;

    int IClientListener.ListenerVersion => IClientListener.ApiVersion;
    private readonly List<string> _particlePaths = new();
    int IClientListener.ListenerPriority => 0;
    private readonly string _sharpPath;

    private readonly ISharedSystem _sharedSystem;
    private readonly IClientManager _clientManager;
    private readonly ITransmitManager _transmitManager;
    private readonly ILogger<ParticleTest> _logger;
    private readonly IModSharp _modSharp;

    public ParticleTest(
        ISharedSystem sharedSystem,
        string dllPath,
        string sharpPath,
        Version version,
        IConfiguration configuration,
        bool hotReload)
    {
        _sharedSystem = sharedSystem;
        _clientManager = sharedSystem.GetClientManager();
        _modSharp = sharedSystem.GetModSharp();
        _logger = sharedSystem.GetLoggerFactory().CreateLogger<ParticleTest>();
        _transmitManager = sharedSystem.GetTransmitManager();
        _sharpPath = sharpPath;
    }

    public bool Init()
    {
        _clientManager.InstallClientListener(this);
        _sharedSystem.GetModSharp().InstallGameListener(this);

        _clientManager.InstallCommandCallback("ptest", OnParticleTestCommand);
        

        _logger.LogInformation("ParticleTest loaded");
        OnResourcePrecache();

        return true;
    }

    public void Shutdown()
    {
        _clientManager.RemoveClientListener(this);
        _sharedSystem.GetModSharp().RemoveGameListener(this);
    }

    private IBaseParticle? _debugParticle;
    private IBaseEntity _targetInfo;

    private void KillActiveParticle()
    {
        _debugParticle?.Kill();
        _debugParticle = null;
    }

    private ECommandAction OnParticleTestCommand(IGameClient client, StringCommand command)
    {
        var pawn = client.GetPlayerController()?.GetPlayerPawn();
        if (pawn == null) return ECommandAction.Stopped;

        try
        {
            KillActiveParticle();

            var kv = new Dictionary<string, KeyValuesVariantValueItem>
            {
                ["effect_name"] = "particles/digits_x/digits_x.vpcf",
                ["start_active"] = "0"
            };

            var entity = _sharedSystem.GetEntityManager().SpawnEntitySync<IBaseParticle>("info_particle_system", kv);
            if (entity is not { } particle) return ECommandAction.Stopped;

            var targetKv = new Dictionary<string, KeyValuesVariantValueItem>
            {
                ["origin"] = "0.0 1.0 0.5"
            };

            var entity2 = _sharedSystem.GetEntityManager().SpawnEntitySync<IBaseEntity>("info_target", targetKv);
            if (entity2 is not { } target) return ECommandAction.Stopped;

            _targetInfo = target;
            particle.GetControlPointEntities()[17] = _targetInfo.Handle;

            // 1. Set the properties while it is inactive
            particle.DataControlPoint = 33;
            particle.DataControlPointValue = new Vector(1.5f, 1.5f, 0.0f); // Position

            // 2. Apply your manual Control Point Overrides
            SetControlPointValue(particle, 32, new Vector(7.0f, 0.0f, 0.0f)); // Frame
            SetControlPointValue(particle, 34, new Vector(0.08f, 0.0f, 0.0f)); // Scale
            SetControlPointValue(particle, 16, new Vector(255f, 255f, 255f)); // Alpha

            // 3. Force the particle to wake up and read the new data
            particle.AcceptInput("Start");
            particle.Active = true;

            _debugParticle = particle;
            _logger.LogInformation("Large Digit 7 Particle Spawned and Started");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Particle test failed");
        }

        return ECommandAction.Stopped;
    }

    private bool SetControlPointValue(IBaseParticle particle, int cpIndex, Vector value)
    {
        var assignments = particle.GetServerControlPointAssignments();
        var controlPoints = particle.GetServerControlPoints();
        for (int i = 0; i < 4; i++)
        {
            if (assignments[i] == cpIndex || assignments[i] == 255)
            {
                assignments[i] = (byte) cpIndex;
                controlPoints[i] = value;
                return true;
            }
        }

        _logger.LogWarning("No free server controlled control points for CP {CpIndex}", cpIndex);
        return false;
    }

    public void OnResourcePrecache()
    {
        Console.WriteLine("[ParticleTest] precache start");
        _logger.LogInformation("[ParticleTest] precache start");

        // Ensure _sharpPath is correctly pointing to your plugin root
        var assetPath = Path.Combine(_sharpPath, "assets");

        if (!Directory.Exists(assetPath))
        {
            Console.WriteLine($"[ParticleTest] asset path missing: {assetPath}");
            return;
        }

        var files = Directory.EnumerateFiles(assetPath, "*", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            // 1. Normalize the path string
            var relative = file[(assetPath.Length + 1)..].Replace("\\", "/");

            // 2. Filter: Only process if it's in the 'particles' directory
            if (!relative.StartsWith("particles/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 3. Clean the asset name (remove the _c compiled suffix if present)
            var asset = relative.EndsWith("_c", StringComparison.OrdinalIgnoreCase)
                ? relative[..^2]
                : relative;

            // 4. Precache and Log
            _modSharp.PrecacheResource(asset);

            if (relative.EndsWith(".vpcf_c", StringComparison.OrdinalIgnoreCase))
            {
                _particlePaths.Add(asset);
            }

            Console.WriteLine($"[ParticleTest] Precached Particle: {asset}");
        }

        Console.WriteLine("[ParticleTest] precache done");
        _logger.LogInformation("[ParticleTest] precache done");
    }
}