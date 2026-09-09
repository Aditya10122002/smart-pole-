// ============================================================
// Program.cs - Device Startup Template
// ============================================================
// 
// This is a template showing how to integrate the new encryption
// system with dynamic key rotation into your application.
//
// Usage:
// 1. Copy the Initialize() section into your Program.cs
// 2. Register services in DI container
// 3. Inject services into your controllers/services
// ============================================================

/*
using vitalschair_prod_v1;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ════════════════════════════════════════════════════════
// DEVICE INITIALIZATION (Run once at startup)
// ════════════════════════════════════════════════════════

var initializer = new DeviceStartupInitializer();

try
{
    await initializer.InitializeAsync();
}
catch (Exception ex)
{
    Console.WriteLine($"❌ FATAL: Device initialization failed");
    Console.WriteLine($"Error: {ex.Message}");
    Environment.Exit(1);
}

// ════════════════════════════════════════════════════════
// REGISTER SERVICES IN DEPENDENCY INJECTION
// ════════════════════════════════════════════════════════

// Register managers as singletons (thread-safe, created once)
builder.Services.AddSingleton(initializer.GetRegistrationManager());
builder.Services.AddSingleton(initializer.GetResilientSecretKeyManager());
builder.Services.AddSingleton(initializer.GetEncryptionManager());

// Register API client as scoped (one per request)
builder.Services.AddScoped(sp => initializer.GetSecureApiClient());

// Register your services that need encryption
builder.Services.AddScoped<ExampleVitalsService>();

// ════════════════════════════════════════════════════════
// BUILD AND RUN APP
// ════════════════════════════════════════════════════════

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

Console.WriteLine("\n✓ Application started successfully");
await app.RunAsync();
*/

// ════════════════════════════════════════════════════════
// EXAMPLE: Controller using encrypted API
// ════════════════════════════════════════════════════════

/*
[ApiController]
[Route("api/[controller]")]
public class VitalsController : ControllerBase
{
    private readonly ExampleVitalsService _vitalsService;

    public VitalsController(ExampleVitalsService vitalsService)
    {
        _vitalsService = vitalsService;
    }

    [HttpPost("submit")]
    public async Task<IActionResult> SubmitVitals([FromBody] ExampleVitalsService.VitalsData vitals)
    {
        try
        {
            // Service automatically encrypts payload and sends
            await _vitalsService.SubmitVitalsAsync(vitals);
            return Ok(new { status = "success", message = "Vitals submitted" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { status = "error", message = ex.Message });
        }
    }

    [HttpGet("encryption-status")]
    public IActionResult GetEncryptionStatus()
    {
        _vitalsService.PrintEncryptionDiagnostics();
        return Ok(new { status = "Encryption diagnostics printed to console" });
    }
}
*/

// ════════════════════════════════════════════════════════
// DEPENDENCY INJECTION SUMMARY
// ════════════════════════════════════════════════════════

/*
Services registered:
  ✓ RegistrationDataManager          - Device registration & config
  ✓ ResilientSecretKeyManager        - Key caching & fallback
  ✓ ResilientApiEncryptionManager    - Encryption with dynamic keys
  ✓ ResilientSecureApiClient         - HTTP client with auto encrypt/decrypt
  ✓ ExampleVitalsService             - Your service using encryption

Usage in services:
  public MyService(ResilientApiEncryptionManager encMgr)
  {
      string encrypted = await encMgr.EncryptAsync(data);
      string decrypted = await encMgr.DecryptAsync(encrypted);
  }

Or with SecureApiClient:
  public MyService(ResilientSecureApiClient apiClient)
  {
      string response = await apiClient.PostAsync("/endpoint", payload);
      // Encryption/decryption automatic
  }
*/

// ════════════════════════════════════════════════════════
// KEY ROTATION BEHAVIOR
// ════════════════════════════════════════════════════════

/*
Timeline:

T=0: Device starts
  → DeviceStartupInitializer.InitializeAsync()
  → Fetches first secret key from server
  → Caches key in memory + disk
  → Device ready for encryption

T=0-60min: Normal operation
  → Uses cached key (instant, no network)
  → ~2000 encryptions/sec possible

T=60min: Cache TTL expires
  → Next encryption triggers key fetch
  → Server provides new key
  → Cache updated, device continues

T=API_DOWN: API unreachable
  → Tries to fetch new key
  → Fails, falls back to cached key
  → Device continues with stale key
  → Logs warning

T=24h+DOWN: Cache very old, API still down
  → Uses very old cached key
  → Logs critical warning
  → Device still works (best effort)

T=7d+DOWN: Cache expired, API down
  → No valid cache available
  → Encryption fails with error
  → Operator must fix network/API
*/

// ════════════════════════════════════════════════════════
// LOGGING
// ════════════════════════════════════════════════════════

/*
Log messages to monitor:

✓ SUCCESS:
  "✓ Secret key pre-fetched successfully (ID: 2)"
  "✓ Using cached secret key (age: 45.2s)"
  "✓ Device initialization complete"

⚠️  WARNING:
  "⚠️  Using stale in-memory cache (age: 1.5h) - API unreachable"
  "⚠️  Secret key pre-fetch failed (will retry on first use)"

❌ ERROR:
  "❌ Secret key unavailable: API unreachable and no cache available"
  "❌ Initialization failed"

Setup alerts for:
  - "❌ FATAL:" → Immediate action required
  - "❌ Secret key unavailable" → Device cannot encrypt
  - Multiple "⚠️  Using stale" within 30min → API issue
*/
