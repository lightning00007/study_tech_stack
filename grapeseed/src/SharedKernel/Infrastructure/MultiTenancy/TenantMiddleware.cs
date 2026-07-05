using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace GrapeSeed.SharedKernel.Infrastructure.MultiTenancy;

// =============================================================================
// 📖 CONCEPT: Multi-Tenant Context
// =============================================================================
// Every HTTP request in GrapeSeed belongs to exactly one tenant. The tenant
// identity is established once (in TenantMiddleware, below) and then flows
// throughout the entire request lifecycle via ITenantContext.
//
// ITenantContext is registered as a SCOPED service:
//   - A new instance is created for each HTTP request.
//   - All services within that request share the same instance.
//   - It is disposed when the request completes.
//
// This means: any service that receives ITenantContext via dependency injection
// automatically gets the correct tenant for the current request.
//
// 💡 WHY not use HttpContext directly in services?
//   Services deeper in the stack (e.g., repositories) should not know about HTTP.
//   ITenantContext abstracts the source of the tenant identity — it could come
//   from an HTTP header, a message queue consumer, or a background job context.
// =============================================================================

/// <summary>
/// Provides the current tenant's identity to any service that needs it.
/// Registered as Scoped — one instance per request.
/// </summary>
public interface ITenantContext
{
    /// <summary>The unique identifier of the current tenant.</summary>
    Guid TenantId { get; }

    /// <summary>
    /// The PostgreSQL schema name for this tenant.
    /// Computed as: "tenant_" + tenant.Slug.ToLowerInvariant().Replace("-", "_")
    /// Example: tenant "school-a-london" → schema "tenant_school_a_london"
    /// </summary>
    string SchemaName { get; }

    /// <summary>True if the tenant context has been resolved (i.e., we are inside an authenticated request).</summary>
    bool IsResolved { get; }
}

/// <summary>
/// Mutable implementation of ITenantContext. Set once by TenantMiddleware.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    public Guid TenantId { get; private set; }
    public string SchemaName { get; private set; } = string.Empty;
    public bool IsResolved { get; private set; }

    /// <summary>
    /// Called by TenantMiddleware to populate the tenant context for this request.
    /// </summary>
    internal void Resolve(Guid tenantId, string schemaName)
    {
        TenantId = tenantId;
        SchemaName = schemaName;
        IsResolved = true;
    }
}

// =============================================================================
// 📖 CONCEPT: ASP.NET Core Middleware
// =============================================================================
// Middleware is a component that is assembled into an application pipeline to
// handle requests and responses. Each middleware:
//   1. Receives the HttpContext (the request and its metadata).
//   2. Optionally modifies the request or response.
//   3. Calls the next middleware (or short-circuits the pipeline).
//
// TenantMiddleware runs early in the pipeline, before controllers are invoked.
// It reads the X-Tenant-Id header (injected by the API Gateway after JWT validation)
// and resolves the TenantContext for the request.
//
// Pipeline order (defined in Program.cs):
//   UseRouting → UseAuthentication → UseAuthorization → UseTenantMiddleware → UseControllers
// =============================================================================

/// <summary>
/// Middleware that resolves the current tenant from the X-Tenant-Id HTTP header
/// and populates ITenantContext for use throughout the request.
/// </summary>
public sealed class TenantMiddleware
{
    private const string TenantIdHeader = "X-Tenant-Id";
    private const string TenantSchemaHeader = "X-Tenant-Schema";

    private readonly RequestDelegate _next;
    private readonly ILogger<TenantMiddleware> _logger;

    public TenantMiddleware(RequestDelegate next, ILogger<TenantMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext httpContext, TenantContext tenantContext)
    {
        // 📖 CONCEPT: Some endpoints are tenant-agnostic (e.g., health checks, Swagger UI).
        // We skip tenant resolution for those paths rather than requiring every
        // request to carry tenant headers.
        if (IsPublicEndpoint(httpContext.Request.Path))
        {
            await _next(httpContext);
            return;
        }

        var tenantIdHeader = httpContext.Request.Headers[TenantIdHeader].FirstOrDefault();
        var schemaHeader = httpContext.Request.Headers[TenantSchemaHeader].FirstOrDefault();

        if (string.IsNullOrEmpty(tenantIdHeader) || !Guid.TryParse(tenantIdHeader, out var tenantId))
        {
            _logger.LogWarning("Request to {Path} missing or invalid {Header} header", 
                httpContext.Request.Path, TenantIdHeader);

            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsync($"Missing required header: {TenantIdHeader}");
            return; // Short-circuits: next middleware/controller is NOT called
        }

        // Populate the scoped TenantContext for this request
        var schemaName = schemaHeader ?? $"tenant_{tenantId:N}"; // fallback if schema not in header
        tenantContext.Resolve(tenantId, schemaName);

        _logger.LogDebug("Resolved tenant {TenantId} with schema {Schema}", tenantId, schemaName);

        await _next(httpContext); // Call the next middleware in the pipeline
    }

    private static bool IsPublicEndpoint(PathString path)
    {
        return path.StartsWithSegments("/health") ||
               path.StartsWithSegments("/swagger") ||
               path.StartsWithSegments("/metrics");
    }
}
