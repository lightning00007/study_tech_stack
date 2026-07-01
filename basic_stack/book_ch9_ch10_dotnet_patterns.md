# Chapter 9: CQRS + MediatR — Separating Reads from Writes

---

## 9.1 The Problem: The God Service Anti-Pattern

In a traditional layered architecture, you often end up with a service class like this:

```csharp
// The "God Service" — does everything, grows without bound
public class OrderService
{
    public Task<Order> GetOrderAsync(int id) { ... }
    public Task<PagedResult<Order>> GetOrdersAsync(int page, int size) { ... }
    public Task<Order> CreateOrderAsync(CreateOrderRequest request) { ... }
    public Task UpdateOrderAsync(int id, UpdateOrderRequest request) { ... }
    public Task CancelOrderAsync(int id, string reason) { ... }
    public Task ApproveOrderAsync(int id, int approverId) { ... }
    public Task<OrderReport> GetOrderReportAsync(DateRange range) { ... }
    public Task<decimal> CalculateTaxAsync(int id) { ... }
    public Task<List<Order>> GetPendingOrdersForShippingAsync() { ... }
    // ... 30 more methods
}
```

Problems:
- **Constructor explosion**: Needs 10+ dependencies injected
- **Mixed concerns**: Read logic mixed with write logic
- **Hard to test**: Every test must set up many dependencies
- **Single Responsibility violated**: One class does too many things
- **Merge conflicts**: Multiple developers all editing the same file

---

## 9.2 CQRS — The Theory

**CQRS (Command Query Responsibility Segregation)** was formalized by Greg Young, based on Bertrand Meyer's **CQS (Command Query Separation)** principle:

> **"A method should either change the state of the system (Command), or return a result (Query), but not both."**

CQRS takes this further at the architectural level:
- **Commands** = write operations (create, update, delete). May return an ID or status, but don't return domain data.
- **Queries** = read operations. Return data but never modify state.

### Why Separate Them?

**Different scaling requirements:**
- Reads typically outnumber writes 9:1 or more
- You can add Redis caching to queries without touching commands
- You can route queries to read replicas, commands to the primary DB

**Different complexity:**
- Commands need: validation, business rules, domain logic, event publishing, transactions
- Queries need: fast data retrieval, projection to DTOs, caching

**Different models:**
- Commands work on rich domain objects (with business methods)
- Queries return flat, simple DTOs optimized for the UI

---

## 9.3 MediatR — The Mediator Pattern

MediatR is a .NET library by Jimmy Bogard that implements the **Mediator pattern**. Instead of controllers directly calling services, they send a **Request** to MediatR, which routes it to the appropriate **Handler**.

```
WITHOUT CQRS/MediatR:
Controller → OrderService → Repository → DB
             ↑ god object with 30 methods

WITH CQRS/MediatR:
Controller → mediator.Send(CreateOrderCommand)
                  └→ CreateOrderCommandHandler → Repository → DB
                                              ↘→ EventBus → SQS

Controller → mediator.Send(GetOrderQuery)
                  └→ GetOrderQueryHandler → Cache → DB (on miss)
```

### 9.3.1 Installation and Setup

```csharp
// Install:
// dotnet add package MediatR

// Program.cs
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(Assembly.GetExecutingAssembly());

    // Register pipeline behaviors (in order of execution)
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(PerformanceBehavior<,>));
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(CachingBehavior<,>));
});
```

---

## 9.4 Commands — Writing State

### 9.4.1 Anatomy of a Command

A command represents **intent to change state**. It is named as an imperative verb phrase: `CreateOrder`, `CancelOrder`, `UpdateProductPrice`.

```csharp
// ── The Command ────────────────────────────────────────────────────────────

// Using C# records — immutable, value-based equality, concise syntax
// IRequest<TResponse> tells MediatR what this command returns
public record CreateOrderCommand(
    int UserId,
    List<CreateOrderItemDto> Items,
    string? ShippingAddress,
    string? PromoCode
) : IRequest<CreateOrderResult>;

public record CreateOrderItemDto(int ProductId, int Quantity);

// Return type — what we get back after the command runs
public record CreateOrderResult(int OrderId, decimal Total, string Status);

// ── Validation (FluentValidation) ──────────────────────────────────────────

// Install: dotnet add package FluentValidation
//          dotnet add package FluentValidation.DependencyInjectionExtensions

public class CreateOrderCommandValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderCommandValidator(IProductRepository productRepo)
    {
        RuleFor(x => x.UserId)
            .GreaterThan(0).WithMessage("UserId must be positive");

        RuleFor(x => x.Items)
            .NotEmpty().WithMessage("Order must have at least one item")
            .Must(items => items.Count <= 50).WithMessage("Cannot order more than 50 different products");

        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.ProductId).GreaterThan(0);
            item.RuleFor(i => i.Quantity).InclusiveBetween(1, 100);
        });

        RuleFor(x => x.ShippingAddress)
            .MaximumLength(500)
            .When(x => x.ShippingAddress is not null);
    }
}

// ── The Domain Entities ────────────────────────────────────────────────────

public class Order
{
    public int Id { get; private set; }
    public int UserId { get; private set; }
    public decimal Total { get; private set; }
    public string Status { get; private set; } = "Pending";
    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;
    public List<OrderItem> Items { get; private set; } = new();

    // Domain factory method — encapsulates creation logic
    public static Order Create(int userId, List<(Product Product, int Quantity)> items, string? promoCode)
    {
        var order = new Order { UserId = userId };

        foreach (var (product, quantity) in items)
        {
            if (!product.IsInStock(quantity))
                throw new DomainException($"Product {product.Name} has insufficient stock");

            order.Items.Add(OrderItem.Create(product.Id, product.Price, quantity));
        }

        order.Total = order.Items.Sum(i => i.LineTotal);

        if (promoCode is not null)
        {
            var discount = ApplyPromoCode(promoCode, order.Total);
            order.Total -= discount;
        }

        return order;
    }
}

// ── The Command Handler ────────────────────────────────────────────────────

public class CreateOrderCommandHandler : IRequestHandler<CreateOrderCommand, CreateOrderResult>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICacheService _cache;
    private readonly IEventBus _eventBus;
    private readonly ILogger<CreateOrderCommandHandler> _logger;

    public CreateOrderCommandHandler(
        IUnitOfWork unitOfWork,
        ICacheService cache,
        IEventBus eventBus,
        ILogger<CreateOrderCommandHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _cache = cache;
        _eventBus = eventBus;
        _logger = logger;
    }

    public async Task<CreateOrderResult> Handle(CreateOrderCommand command, CancellationToken ct)
    {
        // 1. Load required data
        var productIds = command.Items.Select(i => i.ProductId).Distinct().ToList();
        var products = await _unitOfWork.Products.GetByIdsAsync(productIds, ct);

        if (products.Count != productIds.Count)
            throw new DomainException("One or more products not found");

        // 2. Map command items to (Product, Quantity) pairs
        var orderItems = command.Items
            .Select(item => (
                Product: products.First(p => p.Id == item.ProductId),
                Quantity: item.Quantity
            ))
            .ToList();

        // 3. Create domain object (business rules enforced inside)
        var order = Order.Create(command.UserId, orderItems, command.PromoCode);

        // 4. Persist
        await _unitOfWork.Orders.AddAsync(order, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation("Order {OrderId} created for user {UserId}. Total: {Total}",
            order.Id, command.UserId, order.Total);

        // 5. Invalidate related caches
        await _cache.RemoveByPatternAsync($"orders:user:{command.UserId}:*", ct);

        // 6. Publish integration event to notify other services
        await _eventBus.PublishAsync("OrderCreated", new OrderCreatedEvent
        {
            OrderId = order.Id,
            UserId = command.UserId,
            Total = order.Total,
            Items = order.Items.Select(i => new OrderItemDto(i.ProductId, i.Quantity)).ToList()
        }, ct);

        return new CreateOrderResult(order.Id, order.Total, order.Status);
    }
}
```

---

## 9.5 Queries — Reading State

### 9.5.1 The Query and Its Handler

```csharp
// ── DTOs (Data Transfer Objects) ──────────────────────────────────────────

// DTOs are flat, UI-friendly projections — never expose domain entities directly
public record OrderDetailDto(
    int Id,
    int UserId,
    string UserEmail,
    decimal Total,
    string Status,
    DateTime CreatedAt,
    List<OrderItemDetailDto> Items
);

public record OrderItemDetailDto(
    int ProductId,
    string ProductName,
    decimal UnitPrice,
    int Quantity,
    decimal LineTotal
);

public record PagedResult<T>(List<T> Items, int TotalCount, int Page, int PageSize)
{
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
    public bool HasPreviousPage => Page > 1;
    public bool HasNextPage => Page < TotalPages;
}

// ── Query: Get order details ───────────────────────────────────────────────

public record GetOrderDetailQuery(int OrderId) : IRequest<OrderDetailDto?>;

public class GetOrderDetailQueryHandler : IRequestHandler<GetOrderDetailQuery, OrderDetailDto?>
{
    private readonly AppDbContext _context;  // Queries can directly use DbContext (skip repository)
    private readonly ICacheService _cache;

    public GetOrderDetailQueryHandler(AppDbContext context, ICacheService cache)
    {
        _context = context;
        _cache = cache;
    }

    public async Task<OrderDetailDto?> Handle(GetOrderDetailQuery query, CancellationToken ct)
    {
        var cacheKey = $"order:detail:{query.OrderId}";
        var cached = await _cache.GetAsync<OrderDetailDto>(cacheKey, ct);
        if (cached is not null) return cached;

        // Direct DB query with projection — no need to load domain entity
        var dto = await _context.Orders
            .AsNoTracking()
            .Where(o => o.Id == query.OrderId)
            .Select(o => new OrderDetailDto(
                o.Id,
                o.UserId,
                o.User.Email,
                o.Total,
                o.Status,
                o.CreatedAt,
                o.Items.Select(i => new OrderItemDetailDto(
                    i.ProductId,
                    i.Product.Name,
                    i.UnitPrice,
                    i.Quantity,
                    i.UnitPrice * i.Quantity
                )).ToList()
            ))
            .FirstOrDefaultAsync(ct);

        if (dto is null) return null;

        await _cache.SetAsync(cacheKey, dto, TimeSpan.FromMinutes(15), ct);
        return dto;
    }
}

// ── Query: Paginated list ──────────────────────────────────────────────────

public record GetOrdersQuery(
    int UserId,
    string? Status = null,
    int Page = 1,
    int PageSize = 20,
    string SortBy = "CreatedAt",
    bool Descending = true
) : IRequest<PagedResult<OrderSummaryDto>>;

public class GetOrdersQueryHandler : IRequestHandler<GetOrdersQuery, PagedResult<OrderSummaryDto>>
{
    private readonly AppDbContext _context;

    public async Task<PagedResult<OrderSummaryDto>> Handle(GetOrdersQuery query, CancellationToken ct)
    {
        var q = _context.Orders
            .AsNoTracking()
            .Where(o => o.UserId == query.UserId);

        // Dynamic filtering
        if (query.Status is not null)
            q = q.Where(o => o.Status == query.Status);

        // Count before pagination
        var totalCount = await q.CountAsync(ct);

        // Dynamic sorting
        q = query.SortBy switch
        {
            "Total" => query.Descending ? q.OrderByDescending(o => o.Total) : q.OrderBy(o => o.Total),
            "Status" => query.Descending ? q.OrderByDescending(o => o.Status) : q.OrderBy(o => o.Status),
            _ => query.Descending ? q.OrderByDescending(o => o.CreatedAt) : q.OrderBy(o => o.CreatedAt)
        };

        var items = await q
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(o => new OrderSummaryDto(o.Id, o.Total, o.Status, o.CreatedAt, o.Items.Count))
            .ToListAsync(ct);

        return new PagedResult<OrderSummaryDto>(items, totalCount, query.Page, query.PageSize);
    }
}
```

---

## 9.6 Pipeline Behaviors — Cross-Cutting Concerns

Pipeline behaviors wrap every request/handler. Think of them as middleware for MediatR. They execute in order:

```
Request
  → LoggingBehavior (before)
    → ValidationBehavior (before)
      → PerformanceBehavior (before)
        → CachingBehavior (before / after)
          → HANDLER (actual work)
        ← CachingBehavior (after)
      ← PerformanceBehavior (after - measure time)
    ← ValidationBehavior (after)
  ← LoggingBehavior (after)
Response
```

### 9.6.1 Logging Behavior

```csharp
public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly ILogger<LoggingBehavior<TRequest, TResponse>> _logger;

    public LoggingBehavior(ILogger<LoggingBehavior<TRequest, TResponse>> logger) => _logger = logger;

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken ct)
    {
        var requestName = typeof(TRequest).Name;

        _logger.LogInformation("Handling {RequestName}: {@Request}", requestName, request);

        TResponse response;
        try
        {
            response = await next();
            _logger.LogInformation("Handled {RequestName} successfully", requestName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling {RequestName}", requestName);
            throw;
        }

        return response;
    }
}
```

### 9.6.2 Validation Behavior

```csharp
public class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators) => _validators = validators;

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken ct)
    {
        if (!_validators.Any()) return await next();

        var context = new ValidationContext<TRequest>(request);

        var validationResults = await Task.WhenAll(
            _validators.Select(v => v.ValidateAsync(context, ct))
        );

        var failures = validationResults
            .SelectMany(r => r.Errors)
            .Where(f => f is not null)
            .ToList();

        if (failures.Count != 0)
        {
            throw new ValidationException(failures);
            // This is caught by your global exception handler and returns 400 Bad Request
        }

        return await next();
    }
}
```

### 9.6.3 Performance Behavior

```csharp
public class PerformanceBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly Stopwatch _timer = new();
    private readonly ILogger<PerformanceBehavior<TRequest, TResponse>> _logger;
    private const int SlowRequestThresholdMs = 500;

    public PerformanceBehavior(ILogger<PerformanceBehavior<TRequest, TResponse>> logger) => _logger = logger;

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        _timer.Restart();
        var response = await next();
        _timer.Stop();

        var elapsedMs = _timer.ElapsedMilliseconds;

        if (elapsedMs > SlowRequestThresholdMs)
        {
            _logger.LogWarning(
                "SLOW REQUEST: {RequestName} took {ElapsedMs}ms. Request: {@Request}",
                typeof(TRequest).Name, elapsedMs, request);
        }
        else
        {
            _logger.LogDebug("{RequestName} completed in {ElapsedMs}ms", typeof(TRequest).Name, elapsedMs);
        }

        return response;
    }
}
```

### 9.6.4 Caching Behavior (for Queries Only)

```csharp
// Marker interface — only queries that implement this will be cached
public interface ICacheableQuery
{
    string CacheKey { get; }
    TimeSpan? CacheDuration { get; }
}

// Query with caching
public record GetProductCatalogQuery(string Category, int Page) 
    : IRequest<PagedResult<ProductDto>>, ICacheableQuery
{
    public string CacheKey => $"products:catalog:{Category}:page:{Page}";
    public TimeSpan? CacheDuration => TimeSpan.FromMinutes(5);
}

// Caching behavior
public class CachingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly ICacheService _cache;

    public CachingBehavior(ICacheService cache) => _cache = cache;

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        // Only cache if the request opts in
        if (request is not ICacheableQuery cacheableRequest)
            return await next();

        var cached = await _cache.GetAsync<TResponse>(cacheableRequest.CacheKey, ct);
        if (cached is not null) return cached;

        var response = await next();

        if (response is not null)
            await _cache.SetAsync(cacheableRequest.CacheKey, response, cacheableRequest.CacheDuration, ct);

        return response;
    }
}
```

---

## 9.7 Notifications — One Event, Many Handlers

Besides Requests (one handler), MediatR supports **Notifications** (multiple handlers). Perfect for domain events.

```csharp
// Notification — broadcast to ALL registered handlers
public record OrderCreatedNotification(int OrderId, int UserId, decimal Total) : INotification;

// Handler 1: Send confirmation email
public class SendOrderConfirmationEmailHandler : INotificationHandler<OrderCreatedNotification>
{
    private readonly IEmailService _email;

    public async Task Handle(OrderCreatedNotification notification, CancellationToken ct)
    {
        await _email.SendOrderConfirmationAsync(notification.UserId, notification.OrderId);
    }
}

// Handler 2: Update analytics
public class UpdateOrderAnalyticsHandler : INotificationHandler<OrderCreatedNotification>
{
    private readonly IAnalyticsService _analytics;

    public async Task Handle(OrderCreatedNotification notification, CancellationToken ct)
    {
        await _analytics.RecordOrderAsync(notification.OrderId, notification.Total);
    }
}

// Handler 3: Update inventory
public class ReserveInventoryHandler : INotificationHandler<OrderCreatedNotification>
{
    private readonly IInventoryService _inventory;

    public async Task Handle(OrderCreatedNotification notification, CancellationToken ct)
    {
        await _inventory.ReserveItemsForOrderAsync(notification.OrderId);
    }
}

// Publish the notification from the command handler
// ALL three handlers above execute when you publish this
await _mediator.Publish(new OrderCreatedNotification(order.Id, command.UserId, order.Total), ct);
```

---

## 9.8 Idempotency — Handling Duplicate Commands

In distributed systems, commands can be delivered more than once (network retries, SQS at-least-once delivery). Idempotent commands produce the same result whether executed once or 100 times.

```csharp
// Add an IdempotencyKey to commands that should be idempotent
public record CreateOrderCommand(
    int UserId,
    List<CreateOrderItemDto> Items,
    string IdempotencyKey  // Client generates this UUID per logical operation
) : IRequest<CreateOrderResult>;

// Idempotency behavior
public class IdempotencyBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly ICacheService _cache;

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        // Check if the request has an idempotency key
        if (request is not IIdempotentRequest idempotentRequest)
            return await next();

        var cacheKey = $"idempotency:{typeof(TRequest).Name}:{idempotentRequest.IdempotencyKey}";

        // Check if this command was already processed
        var cached = await _cache.GetAsync<TResponse>(cacheKey, ct);
        if (cached is not null)
        {
            // Already processed — return same result without executing again
            return cached;
        }

        var response = await next();

        // Cache the result for 24 hours
        await _cache.SetAsync(cacheKey, response, TimeSpan.FromHours(24), ct);

        return response;
    }
}
```

---

# Chapter 10: Unit of Work + Repository Pattern

---

## 10.1 The Problem Without These Patterns

Without Repository and Unit of Work patterns:

```csharp
// Business logic that directly uses DbContext — tight coupling, untestable
public class OrderService
{
    private readonly AppDbContext _context;

    public async Task TransferOrderAsync(int orderId, int targetUserId)
    {
        // Business logic is entangled with EF Core infrastructure
        var order = await _context.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId);

        order.UserId = targetUserId;
        order.UpdatedAt = DateTime.UtcNow;

        // What if we need to do multiple things in one transaction?
        // What if we need to test this without a real DB?
        // What if we switch from EF Core to Dapper?

        await _context.SaveChangesAsync();
    }
}
```

Problems:
- Can't unit test without a real database (or complex EF Core in-memory setup)
- Switching ORM requires rewriting service layer
- Transaction management is ad-hoc
- No single place to add cross-cutting concerns (soft delete, audit fields, concurrency)

---

## 10.2 The Repository Pattern

A Repository is an abstraction over the data access layer. It exposes collection-like semantics (`Add`, `Get`, `Remove`) instead of database-specific operations.

### 10.2.1 Generic Base Repository

```csharp
// ── Interface ─────────────────────────────────────────────────────────────

public interface IRepository<T> where T : class
{
    // Basic CRUD
    Task<T?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<List<T>> GetAllAsync(CancellationToken ct = default);
    Task AddAsync(T entity, CancellationToken ct = default);
    void Update(T entity);
    void Remove(T entity);

    // Bulk operations
    Task AddRangeAsync(IEnumerable<T> entities, CancellationToken ct = default);
    void RemoveRange(IEnumerable<T> entities);

    // Existence check
    Task<bool> ExistsAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);

    // Count
    Task<int> CountAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default);
}

// ── Implementation ────────────────────────────────────────────────────────

public class Repository<T> : IRepository<T> where T : class
{
    protected readonly AppDbContext Context;
    protected readonly DbSet<T> DbSet;

    public Repository(AppDbContext context)
    {
        Context = context;
        DbSet = context.Set<T>();
    }

    public async Task<T?> GetByIdAsync(int id, CancellationToken ct = default)
        => await DbSet.FindAsync(new object[] { id }, ct);

    public async Task<List<T>> GetAllAsync(CancellationToken ct = default)
        => await DbSet.AsNoTracking().ToListAsync(ct);

    public async Task AddAsync(T entity, CancellationToken ct = default)
        => await DbSet.AddAsync(entity, ct);

    public void Update(T entity) => DbSet.Update(entity);

    public void Remove(T entity) => DbSet.Remove(entity);

    public async Task AddRangeAsync(IEnumerable<T> entities, CancellationToken ct = default)
        => await DbSet.AddRangeAsync(entities, ct);

    public void RemoveRange(IEnumerable<T> entities) => DbSet.RemoveRange(entities);

    public async Task<bool> ExistsAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        => await DbSet.AsNoTracking().AnyAsync(predicate, ct);

    public async Task<int> CountAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default)
        => predicate is null
            ? await DbSet.CountAsync(ct)
            : await DbSet.CountAsync(predicate, ct);
}
```

### 10.2.2 Domain-Specific Repository

```csharp
// ── Domain-specific interface ──────────────────────────────────────────────

public interface IOrderRepository : IRepository<Order>
{
    Task<Order?> GetWithItemsAndProductsAsync(int orderId, CancellationToken ct = default);
    Task<List<int>> GetByIdsAsync(IEnumerable<int> ids, CancellationToken ct = default);
    Task<List<Order>> GetByUserIdAsync(int userId, string? status = null, CancellationToken ct = default);
    Task<List<Order>> GetPendingOrdersOlderThanAsync(TimeSpan age, CancellationToken ct = default);
    Task<decimal> GetTotalRevenueForPeriodAsync(DateTime from, DateTime to, CancellationToken ct = default);
}

// ── Implementation ────────────────────────────────────────────────────────

public class OrderRepository : Repository<Order>, IOrderRepository
{
    public OrderRepository(AppDbContext context) : base(context) { }

    public async Task<Order?> GetWithItemsAndProductsAsync(int orderId, CancellationToken ct = default)
        => await Context.Orders
            .Include(o => o.Items)
                .ThenInclude(i => i.Product)
            .Include(o => o.User)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);

    public async Task<List<int>> GetByIdsAsync(IEnumerable<int> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        return await Context.Orders
            .Where(o => idList.Contains(o.Id))
            .Select(o => o.Id)
            .ToListAsync(ct);
    }

    public async Task<List<Order>> GetByUserIdAsync(int userId, string? status = null, CancellationToken ct = default)
        => await Context.Orders
            .AsNoTracking()
            .Where(o => o.UserId == userId && (status == null || o.Status == status))
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(ct);

    public async Task<List<Order>> GetPendingOrdersOlderThanAsync(TimeSpan age, CancellationToken ct = default)
        => await Context.Orders
            .Where(o => o.Status == "Pending" && o.CreatedAt < DateTime.UtcNow - age)
            .ToListAsync(ct);

    public async Task<decimal> GetTotalRevenueForPeriodAsync(DateTime from, DateTime to, CancellationToken ct = default)
        => await Context.Orders
            .Where(o => o.Status == "Completed" && o.CreatedAt >= from && o.CreatedAt <= to)
            .SumAsync(o => o.Total, ct);
}
```

### 10.2.3 The Specification Pattern

The Specification pattern encapsulates query logic in reusable objects, avoiding method explosion in repositories.

```csharp
// ── Specification base class ───────────────────────────────────────────────

public abstract class Specification<T>
{
    public Expression<Func<T, bool>>? Criteria { get; protected set; }
    public List<Expression<Func<T, object>>> Includes { get; } = new();
    public List<string> IncludeStrings { get; } = new();
    public Expression<Func<T, object>>? OrderBy { get; protected set; }
    public Expression<Func<T, object>>? OrderByDescending { get; protected set; }
    public int Take { get; protected set; }
    public int Skip { get; protected set; }
    public bool IsPagingEnabled { get; protected set; }

    protected void AddInclude(Expression<Func<T, object>> include) => Includes.Add(include);
    protected void ApplyPaging(int page, int size) { Skip = (page - 1) * size; Take = size; IsPagingEnabled = true; }
    protected void ApplyOrderBy(Expression<Func<T, object>> orderBy) => OrderBy = orderBy;
    protected void ApplyOrderByDescending(Expression<Func<T, object>> orderByDesc) => OrderByDescending = orderByDesc;
}

// ── Concrete specifications ────────────────────────────────────────────────

public class OrdersForUserSpec : Specification<Order>
{
    public OrdersForUserSpec(int userId, string? status, int page, int pageSize)
    {
        Criteria = o => o.UserId == userId && (status == null || o.Status == status);
        AddInclude(o => o.Items);
        ApplyOrderByDescending(o => o.CreatedAt);
        ApplyPaging(page, pageSize);
    }
}

public class PendingOrdersOlderThanSpec : Specification<Order>
{
    public PendingOrdersOlderThanSpec(TimeSpan age)
    {
        Criteria = o => o.Status == "Pending" && o.CreatedAt < DateTime.UtcNow - age;
    }
}

// ── Specification evaluator ────────────────────────────────────────────────

public static class SpecificationEvaluator
{
    public static IQueryable<T> GetQuery<T>(IQueryable<T> source, Specification<T> spec) where T : class
    {
        var query = source;

        if (spec.Criteria is not null)
            query = query.Where(spec.Criteria);

        query = spec.Includes.Aggregate(query, (current, include) => current.Include(include));
        query = spec.IncludeStrings.Aggregate(query, (current, include) => current.Include(include));

        if (spec.OrderBy is not null)
            query = query.OrderBy(spec.OrderBy);
        else if (spec.OrderByDescending is not null)
            query = query.OrderByDescending(spec.OrderByDescending);

        if (spec.IsPagingEnabled)
            query = query.Skip(spec.Skip).Take(spec.Take);

        return query;
    }
}

// ── Usage in handler ───────────────────────────────────────────────────────

var spec = new OrdersForUserSpec(userId: 42, status: "Pending", page: 1, pageSize: 20);
var orders = await _context.Orders
    .Apply(spec)  // extension method using SpecificationEvaluator
    .AsNoTracking()
    .ToListAsync(ct);
```

---

## 10.3 Unit of Work Pattern

The Unit of Work tracks all changes during a business operation and commits them as a single transaction.

```csharp
// ── Interface ─────────────────────────────────────────────────────────────

public interface IUnitOfWork : IAsyncDisposable
{
    // All repositories accessible through UoW
    IOrderRepository Orders { get; }
    IProductRepository Products { get; }
    IUserRepository Users { get; }

    // Commit all changes in one transaction
    Task<int> SaveChangesAsync(CancellationToken ct = default);

    // Explicit transaction control for complex scenarios
    Task BeginTransactionAsync(CancellationToken ct = default);
    Task CommitTransactionAsync(CancellationToken ct = default);
    Task RollbackTransactionAsync(CancellationToken ct = default);
}

// ── Implementation ────────────────────────────────────────────────────────

public sealed class UnitOfWork : IUnitOfWork
{
    private readonly AppDbContext _context;
    private IDbContextTransaction? _transaction;

    // Lazy initialization — only create repositories when accessed
    private IOrderRepository? _orders;
    private IProductRepository? _products;
    private IUserRepository? _users;

    public UnitOfWork(AppDbContext context) => _context = context;

    public IOrderRepository Orders => _orders ??= new OrderRepository(_context);
    public IProductRepository Products => _products ??= new ProductRepository(_context);
    public IUserRepository Users => _users ??= new UserRepository(_context);

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        // Intercept SaveChanges to automatically set audit fields
        SetAuditFields();
        return await _context.SaveChangesAsync(ct);
    }

    public async Task BeginTransactionAsync(CancellationToken ct = default)
        => _transaction = await _context.Database.BeginTransactionAsync(ct);

    public async Task CommitTransactionAsync(CancellationToken ct = default)
    {
        try
        {
            await _context.SaveChangesAsync(ct);
            await _transaction!.CommitAsync(ct);
        }
        catch
        {
            await RollbackTransactionAsync(ct);
            throw;
        }
        finally
        {
            _transaction?.Dispose();
            _transaction = null;
        }
    }

    public async Task RollbackTransactionAsync(CancellationToken ct = default)
    {
        if (_transaction is not null)
            await _transaction.RollbackAsync(ct);
        _transaction?.Dispose();
        _transaction = null;
    }

    private void SetAuditFields()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in _context.ChangeTracker.Entries<IAuditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.UpdatedAt = now;
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    // Prevent CreatedAt from being modified
                    entry.Property(nameof(IAuditableEntity.CreatedAt)).IsModified = false;
                    break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null) await _transaction.DisposeAsync();
        await _context.DisposeAsync();
    }
}

// ── Audit interface for entities ─────────────────────────────────────────

public interface IAuditableEntity
{
    DateTime CreatedAt { get; set; }
    DateTime UpdatedAt { get; set; }
}

// ── Registration ──────────────────────────────────────────────────────────

builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
// Individual repositories are not registered separately — access through UoW
```

---

## 10.4 Testing — Why These Patterns Pay Off

The entire reason to use Repository + Unit of Work is testability. You can mock them:

```csharp
// Install: dotnet add package Moq
//          dotnet add package xunit

public class CreateOrderCommandHandlerTests
{
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly Mock<IOrderRepository> _orderRepoMock = new();
    private readonly Mock<IProductRepository> _productRepoMock = new();
    private readonly Mock<ICacheService> _cacheMock = new();
    private readonly Mock<IEventBus> _eventBusMock = new();
    private readonly CreateOrderCommandHandler _handler;

    public CreateOrderCommandHandlerTests()
    {
        // Wire up mocks
        _unitOfWorkMock.Setup(x => x.Orders).Returns(_orderRepoMock.Object);
        _unitOfWorkMock.Setup(x => x.Products).Returns(_productRepoMock.Object);

        _handler = new CreateOrderCommandHandler(
            _unitOfWorkMock.Object,
            _cacheMock.Object,
            _eventBusMock.Object,
            NullLogger<CreateOrderCommandHandler>.Instance
        );
    }

    [Fact]
    public async Task Handle_ValidCommand_ReturnsCreatedOrder()
    {
        // Arrange
        var product = new Product { Id = 1, Name = "Laptop", Price = 999.99m, Stock = 10 };

        _productRepoMock
            .Setup(r => r.GetByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Product> { product });

        _orderRepoMock
            .Setup(r => r.AddAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _unitOfWorkMock
            .Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var command = new CreateOrderCommand(
            UserId: 42,
            Items: new List<CreateOrderItemDto> { new(ProductId: 1, Quantity: 2) },
            ShippingAddress: "123 Main St",
            PromoCode: null
        );

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(1999.98m, result.Total);  // 2 × 999.99
        Assert.Equal("Pending", result.Status);

        // Verify side effects
        _orderRepoMock.Verify(r => r.AddAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _eventBusMock.Verify(e => e.PublishAsync("OrderCreated", It.IsAny<OrderCreatedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ProductNotFound_ThrowsDomainException()
    {
        // Arrange — products list doesn't contain all requested products
        _productRepoMock
            .Setup(r => r.GetByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Product>());  // empty — product not found

        var command = new CreateOrderCommand(
            UserId: 42,
            Items: new List<CreateOrderItemDto> { new(ProductId: 99999, Quantity: 1) },
            ShippingAddress: null,
            PromoCode: null
        );

        // Act & Assert
        await Assert.ThrowsAsync<DomainException>(
            () => _handler.Handle(command, CancellationToken.None)
        );

        // Verify: no save happened
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _eventBusMock.Verify(e => e.PublishAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
```

---

## Summary — Chapters 9 and 10

### CQRS + MediatR

| Concept | Key Takeaway |
|---|---|
| **Command** | Changes state. Validates. Publishes events. Returns minimal data. |
| **Query** | Returns data only. Uses caching. Reads from read replicas. Returns DTOs. |
| **Handler** | One handler per command/query. Single responsibility. |
| **Pipeline Behavior** | Cross-cutting concerns: logging, validation, performance, caching, idempotency |
| **Notification** | One event, many independent handlers. Don't use for commands (one-to-one). |
| **Idempotency** | Commands from queues can be retried — make them idempotent with an IdempotencyKey |

### Unit of Work + Repository

| Concept | Key Takeaway |
|---|---|
| **Repository** | Abstracts data access. Enables mocking. Hides query complexity. |
| **Generic Repository** | Base CRUD operations reused across all entities |
| **Specific Repository** | Domain-specific queries that don't belong in the handler |
| **Specification Pattern** | Encapsulates query logic — avoids repository method explosion |
| **Unit of Work** | Coordinates repositories. One `SaveChangesAsync()` = one transaction. |
| **Audit Fields** | `SetAuditFields()` in `SaveChangesAsync()` — automatic CreatedAt/UpdatedAt |
| **Testing** | Mock `IUnitOfWork` and `IRepository` — no database needed for business logic tests |
