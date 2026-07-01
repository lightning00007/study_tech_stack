# Chapter 4: The Chain of Responsibility Pattern — Request Pipelines

> **Behavioral Pattern · GoF Classic · Foundation of Middleware Pipelines**
> *"Avoid coupling the sender of a request to its receiver by giving more than one object a chance to handle the request. Chain the receiving objects and pass the request along the chain until an object handles it."*
> — Gang of Four

---

## Table of Contents

1. [Introduction — The Problem of Hardwired Handling Logic](#1-introduction)
2. [Classic GoF Implementation](#2-classic-gof)
3. [Real-World: HTTP Request Middleware (ASP.NET Core Style)](#3-http-middleware)
4. [Real-World: Support Ticket Escalation System](#4-support-escalation)
5. [Real-World: Validation Pipeline](#5-validation-pipeline)
6. [Real-World: Discount Calculation Chain](#6-discount-chain)
7. [Chain of Responsibility vs. Mediator Pipeline Behaviors](#7-cor-vs-mediator)
8. [Summary](#8-summary)

---

## 1. Introduction — The Problem of Hardwired Handling Logic

Imagine a customer support system. When a ticket arrives, you need to check:

1. Is it a simple FAQ? → Bot handles it.
2. Is it a billing issue? → Level 1 agent handles it.
3. Is it a technical issue? → Level 2 engineer handles it.
4. Is it a legal/compliance matter? → Legal team handles it.
5. Is it a VIP customer? → Account manager handles it.

The naive approach puts all this logic in one place:

```csharp
// ❌ NAIVE: One giant if-else chain
public class SupportCenter
{
    public void HandleTicket(SupportTicket ticket)
    {
        if (ticket.Category == "FAQ")
            _bot.Handle(ticket);
        else if (ticket.Category == "Billing" && ticket.Priority < 3)
            _level1Agent.Handle(ticket);
        else if (ticket.Category == "Technical")
            _level2Engineer.Handle(ticket);
        else if (ticket.IsLegalMatter)
            _legalTeam.Handle(ticket);
        else if (ticket.Customer.IsVip)
            _accountManager.Handle(ticket);
        else
            _defaultQueue.Enqueue(ticket);
    }
}
```

Problems:
- **Open/Closed Violation**: Adding a new handler requires modifying `SupportCenter`.
- **Single Responsibility Violation**: `SupportCenter` knows the routing logic AND calls every handler.
- **Hard to reorder**: Changing priority requires editing this method.
- **Impossible to test handlers in isolation**.

### The Chain Solution

Build a **linked chain** of handlers. Each handler decides:
- Handle the request itself (and stop the chain), OR
- Pass it to the next handler in the chain.

```
Ticket ──► [BotHandler] ──► [Level1Handler] ──► [Level2Handler] ──► [LegalHandler]
               │                   │                   │                   │
           Handles FAQ         Handles Billing     Handles Tech        Handles Legal
               │                   │                   │
           (chain stops)       (chain stops)       (chain stops)
```

---

## 2. Classic GoF Implementation

### 2.1 The Abstract Handler

```csharp
// The abstract handler — defines the chain structure
public abstract class Handler
{
    // Reference to the next handler in the chain
    private Handler? _nextHandler;

    // Fluent API for building the chain: handler.SetNext(nextHandler).SetNext(anotherHandler)
    public Handler SetNext(Handler handler)
    {
        _nextHandler = handler;
        return handler; // Return next so we can chain fluently
    }

    // Template method: subclasses implement their logic, calling base.Handle() to pass along
    public virtual void Handle(SupportTicket ticket)
    {
        _nextHandler?.Handle(ticket);
    }
}

// The ticket model
public class SupportTicket
{
    public int Id { get; init; }
    public string Category { get; init; } = string.Empty;
    public int Priority { get; init; } // 1 = highest, 5 = lowest
    public bool IsLegalMatter { get; init; }
    public Customer Customer { get; init; } = new();
    public string Description { get; init; } = string.Empty;
}

public class Customer
{
    public string Name { get; init; } = string.Empty;
    public bool IsVip { get; init; }
}
```

### 2.2 Concrete Handlers

```csharp
// Handler 1: AI Bot — handles simple FAQs
public class BotHandler : Handler
{
    private static readonly string[] _faqKeywords = { "password", "reset", "hours", "location" };

    public override void Handle(SupportTicket ticket)
    {
        var isFaq = _faqKeywords.Any(kw =>
            ticket.Description.Contains(kw, StringComparison.OrdinalIgnoreCase));

        if (isFaq)
        {
            Console.WriteLine($"[BOT] Ticket #{ticket.Id} resolved with FAQ response.");
            return; // Chain STOPS here
        }

        Console.WriteLine($"[BOT] Cannot resolve #{ticket.Id}. Escalating...");
        base.Handle(ticket); // Pass to next handler
    }
}

// Handler 2: Level 1 Agent — handles billing and low-priority issues
public class Level1AgentHandler : Handler
{
    public override void Handle(SupportTicket ticket)
    {
        if (ticket.Category == "Billing" && ticket.Priority >= 3)
        {
            Console.WriteLine($"[L1 AGENT] Ticket #{ticket.Id} — billing issue handled.");
            return;
        }

        Console.WriteLine($"[L1 AGENT] Cannot resolve #{ticket.Id}. Escalating...");
        base.Handle(ticket);
    }
}

// Handler 3: Level 2 Engineer — handles technical issues
public class Level2EngineerHandler : Handler
{
    public override void Handle(SupportTicket ticket)
    {
        if (ticket.Category == "Technical")
        {
            Console.WriteLine($"[L2 ENGINEER] Ticket #{ticket.Id} — technical issue handled.");
            return;
        }

        Console.WriteLine($"[L2 ENGINEER] Cannot resolve #{ticket.Id}. Escalating...");
        base.Handle(ticket);
    }
}

// Handler 4: Legal Team — handles compliance matters
public class LegalTeamHandler : Handler
{
    public override void Handle(SupportTicket ticket)
    {
        if (ticket.IsLegalMatter)
        {
            Console.WriteLine($"[LEGAL] Ticket #{ticket.Id} — legal matter handled by compliance team.");
            return;
        }

        base.Handle(ticket);
    }
}

// Handler 5: VIP Account Manager — catches VIP customers that fell through
public class VipAccountManagerHandler : Handler
{
    public override void Handle(SupportTicket ticket)
    {
        if (ticket.Customer.IsVip)
        {
            Console.WriteLine($"[VIP MANAGER] Ticket #{ticket.Id} — VIP customer {ticket.Customer.Name} assigned dedicated manager.");
            return;
        }

        Console.WriteLine($"[DEFAULT QUEUE] Ticket #{ticket.Id} added to general queue.");
        // Chain ends — no more handlers
    }
}
```

### 2.3 Building and Using the Chain

```csharp
// Build the chain — ORDER MATTERS
var bot            = new BotHandler();
var level1         = new Level1AgentHandler();
var level2         = new Level2EngineerHandler();
var legal          = new LegalTeamHandler();
var vipManager     = new VipAccountManagerHandler();

// Fluent chain construction
bot.SetNext(level1)
   .SetNext(level2)
   .SetNext(legal)
   .SetNext(vipManager);

// The entry point — always send to the HEAD of the chain
var chain = bot;

// Test different tickets
chain.Handle(new SupportTicket
{
    Id = 1, Description = "How do I reset my password?",
    Customer = new Customer { Name = "Alice" }
});
// [BOT] Ticket #1 resolved with FAQ response.

chain.Handle(new SupportTicket
{
    Id = 2, Category = "Billing", Priority = 3,
    Customer = new Customer { Name = "Bob" }
});
// [BOT] Cannot resolve #2. Escalating...
// [L1 AGENT] Ticket #2 — billing issue handled.

chain.Handle(new SupportTicket
{
    Id = 3, Category = "Technical", Priority = 1,
    Customer = new Customer { Name = "Carol" }
});
// [BOT] Cannot resolve #3. Escalating...
// [L1 AGENT] Cannot resolve #3. Escalating...
// [L2 ENGINEER] Ticket #3 — technical issue handled.

chain.Handle(new SupportTicket
{
    Id = 4, IsLegalMatter = true,
    Customer = new Customer { Name = "Dave" }
});
// [BOT] Cannot resolve #4. Escalating...
// [L1 AGENT] Cannot resolve #4. Escalating...
// [L2 ENGINEER] Cannot resolve #4. Escalating...
// [LEGAL] Ticket #4 — legal matter handled.

chain.Handle(new SupportTicket
{
    Id = 5, Category = "Account",
    Customer = new Customer { Name = "Eve", IsVip = true }
});
// ... escalates through all until VIP Manager
// [VIP MANAGER] Ticket #5 — VIP customer Eve assigned dedicated manager.
```

---

## 3. Real-World: HTTP Request Middleware (ASP.NET Core Style)

ASP.NET Core's middleware pipeline IS the Chain of Responsibility pattern. Let us implement a simplified version from scratch to understand it deeply.

```csharp
// The core delegate — represents the next handler in the pipeline
public delegate Task RequestDelegate(HttpContext context);

// Our HttpContext (simplified)
public class HttpContext
{
    public string Path { get; init; } = "/";
    public Dictionary<string, string> Headers { get; } = new();
    public int StatusCode { get; set; } = 200;
    public string? ResponseBody { get; set; }
    public Dictionary<string, object?> Items { get; } = new(); // Shared state across middleware
}

// The middleware interface
public interface IMiddleware
{
    Task InvokeAsync(HttpContext context, RequestDelegate next);
}

// The Application Builder — assembles the pipeline
public class ApplicationBuilder
{
    private readonly List<Func<RequestDelegate, RequestDelegate>> _middlewares = new();

    // Add middleware to the pipeline
    public ApplicationBuilder Use(Func<RequestDelegate, RequestDelegate> middleware)
    {
        _middlewares.Add(middleware);
        return this;
    }

    // Build the chain and return the first RequestDelegate
    public RequestDelegate Build()
    {
        // Start with a terminal handler that returns 404
        RequestDelegate app = context =>
        {
            context.StatusCode = 404;
            context.ResponseBody = "Not Found";
            return Task.CompletedTask;
        };

        // Wrap each middleware around the previous one, in reverse order
        foreach (var middleware in Enumerable.Reverse(_middlewares))
            app = middleware(app);

        return app;
    }
}

// --- Concrete Middleware ---

// Middleware 1: Request/Response Logging
public class LoggingMiddleware : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"[LOG] --> {context.Path}");
        await next(context); // Call next middleware
        sw.Stop();
        Console.WriteLine($"[LOG] <-- {context.StatusCode} ({sw.ElapsedMilliseconds}ms)");
    }
}

// Middleware 2: Authentication Check
public class AuthenticationMiddleware : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Headers.TryGetValue("Authorization", out var token) || token != "valid-token")
        {
            context.StatusCode = 401;
            context.ResponseBody = "Unauthorized";
            return; // Short-circuit — do NOT call next
        }

        context.Items["UserId"] = "user-123"; // Store auth result for downstream
        await next(context);
    }
}

// Middleware 3: Rate Limiting
public class RateLimitingMiddleware : IMiddleware
{
    private readonly Dictionary<string, (int Count, DateTime Window)> _requests = new();
    private const int MaxRequestsPerMinute = 60;

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var clientId = context.Headers.GetValueOrDefault("X-Client-Id", "anonymous");
        var now = DateTime.UtcNow;

        if (_requests.TryGetValue(clientId, out var record))
        {
            if (now - record.Window < TimeSpan.FromMinutes(1))
            {
                if (record.Count >= MaxRequestsPerMinute)
                {
                    context.StatusCode = 429;
                    context.ResponseBody = "Too Many Requests";
                    return; // Short-circuit
                }
                _requests[clientId] = (record.Count + 1, record.Window);
            }
            else
            {
                _requests[clientId] = (1, now); // Reset window
            }
        }
        else
        {
            _requests[clientId] = (1, now);
        }

        await next(context);
    }
}

// --- Building and Using the Pipeline ---
var builder = new ApplicationBuilder();

var logging    = new LoggingMiddleware();
var auth       = new AuthenticationMiddleware();
var rateLimit  = new RateLimitingMiddleware();

builder
    .Use(next => ctx => logging.InvokeAsync(ctx, next))
    .Use(next => ctx => auth.InvokeAsync(ctx, next))
    .Use(next => ctx => rateLimit.InvokeAsync(ctx, next))
    .Use(next => async ctx =>
    {
        // The actual "route handler" at the end of the pipeline
        ctx.ResponseBody = $"Hello, User {ctx.Items["UserId"]}!";
        await Task.CompletedTask;
    });

var pipeline = builder.Build();

// Test 1: Unauthorized request
var unauthCtx = new HttpContext { Path = "/api/data" };
await pipeline(unauthCtx);
// [LOG] --> /api/data
// [LOG] <-- 401 (1ms)

// Test 2: Authorized request
var authCtx = new HttpContext { Path = "/api/data" };
authCtx.Headers["Authorization"] = "valid-token";
await pipeline(authCtx);
// [LOG] --> /api/data
// [LOG] <-- 200 (2ms)
Console.WriteLine(authCtx.ResponseBody); // Hello, User user-123!
```

---

## 4. Real-World: Support Ticket Escalation System

A more sophisticated version using interfaces and DI for production use:

```csharp
// Generic handler interface — cleaner than abstract class for DI
public interface ITicketHandler
{
    void SetNext(ITicketHandler handler);
    Task<HandlingResult> HandleAsync(SupportTicket ticket, CancellationToken ct);
}

public record HandlingResult(bool Handled, string? HandledBy, string? Message);

public abstract class TicketHandlerBase : ITicketHandler
{
    private ITicketHandler? _next;

    public void SetNext(ITicketHandler handler) => _next = handler;

    public async Task<HandlingResult> HandleAsync(SupportTicket ticket, CancellationToken ct)
    {
        var result = await TryHandleAsync(ticket, ct);
        if (result.Handled) return result;

        return _next is not null
            ? await _next.HandleAsync(ticket, ct)
            : new HandlingResult(false, null, "No handler could process this ticket.");
    }

    // Subclasses implement this — return null to pass to next handler
    protected abstract Task<HandlingResult?> TryHandleAsync(SupportTicket ticket, CancellationToken ct);
}

// Now handlers are cleaner — they ONLY decide if they handle it
public class BotTicketHandler : TicketHandlerBase
{
    private readonly IBotService _botService;

    public BotTicketHandler(IBotService botService) => _botService = botService;

    protected override async Task<HandlingResult?> TryHandleAsync(
        SupportTicket ticket, CancellationToken ct)
    {
        if (!await _botService.CanResolveAsync(ticket.Description, ct))
            return null; // Pass to next

        var answer = await _botService.ResolveAsync(ticket.Description, ct);
        return new HandlingResult(true, "Bot", answer);
    }
}
```

---

## 5. Real-World: Validation Pipeline

Chain of Responsibility is excellent for multi-step validation where each step can fail early:

```csharp
public abstract class OrderValidator
{
    private OrderValidator? _next;

    public OrderValidator SetNext(OrderValidator next)
    {
        _next = next;
        return next;
    }

    public ValidationResult Validate(Order order)
    {
        var result = ValidateStep(order);
        if (!result.IsValid) return result; // Short-circuit on first failure

        return _next?.Validate(order) ?? ValidationResult.Success();
    }

    protected abstract ValidationResult ValidateStep(Order order);
}

public record ValidationResult(bool IsValid, string? Error)
{
    public static ValidationResult Success() => new(true, null);
    public static ValidationResult Fail(string error) => new(false, error);
}

// Validator 1: Check items are in stock
public class StockValidator : OrderValidator
{
    private readonly IInventoryService _inventory;
    public StockValidator(IInventoryService inventory) => _inventory = inventory;

    protected override ValidationResult ValidateStep(Order order)
    {
        foreach (var item in order.Items)
        {
            if (!_inventory.IsInStock(item.ProductId, item.Quantity))
                return ValidationResult.Fail($"Product {item.ProductId} is out of stock.");
        }
        return ValidationResult.Success();
    }
}

// Validator 2: Check credit limit
public class CreditValidator : OrderValidator
{
    private readonly ICreditService _credit;
    public CreditValidator(ICreditService credit) => _credit = credit;

    protected override ValidationResult ValidateStep(Order order)
    {
        var available = _credit.GetAvailableCredit(order.CustomerId);
        if (order.Total > available)
            return ValidationResult.Fail($"Credit limit exceeded. Available: {available:C}");

        return ValidationResult.Success();
    }
}

// Validator 3: Check shipping address
public class AddressValidator : OrderValidator
{
    protected override ValidationResult ValidateStep(Order order)
    {
        if (string.IsNullOrWhiteSpace(order.ShippingAddress?.PostalCode))
            return ValidationResult.Fail("Valid shipping address with postal code is required.");

        return ValidationResult.Success();
    }
}

// Validator 4: Fraud check
public class FraudValidator : OrderValidator
{
    private readonly IFraudDetectionService _fraud;
    public FraudValidator(IFraudDetectionService fraud) => _fraud = fraud;

    protected override ValidationResult ValidateStep(Order order)
    {
        if (_fraud.IsSuspicious(order))
            return ValidationResult.Fail("Order flagged for fraud review.");

        return ValidationResult.Success();
    }
}

// Build the validation chain
var stockValidator   = new StockValidator(inventoryService);
var creditValidator  = new CreditValidator(creditService);
var addressValidator = new AddressValidator();
var fraudValidator   = new FraudValidator(fraudService);

stockValidator
    .SetNext(creditValidator)
    .SetNext(addressValidator)
    .SetNext(fraudValidator);

var validationChain = stockValidator;

var result = validationChain.Validate(order);
if (!result.IsValid)
    throw new OrderValidationException(result.Error!);
```

---

## 6. Real-World: Discount Calculation Chain

Unlike previous examples where the chain STOPS when handled, sometimes you want the request to pass through ALL handlers and each one can MODIFY it:

```csharp
public class PricingContext
{
    public decimal OriginalPrice { get; init; }
    public decimal CurrentPrice { get; set; }
    public Customer Customer { get; init; } = new();
    public Order Order { get; init; } = new();
    public List<string> AppliedDiscounts { get; } = new();
}

// This time, ALL handlers run — each can modify the price
public abstract class DiscountHandler
{
    private DiscountHandler? _next;

    public DiscountHandler SetNext(DiscountHandler next)
    {
        _next = next;
        return next;
    }

    public void ApplyDiscount(PricingContext ctx)
    {
        Apply(ctx); // Always apply (or skip if condition not met)
        _next?.ApplyDiscount(ctx); // Always continue to next
    }

    protected abstract void Apply(PricingContext ctx);
}

public class LoyaltyDiscountHandler : DiscountHandler
{
    protected override void Apply(PricingContext ctx)
    {
        if (ctx.Customer.LoyaltyYears >= 3)
        {
            var discount = ctx.CurrentPrice * 0.05m;
            ctx.CurrentPrice -= discount;
            ctx.AppliedDiscounts.Add($"Loyalty discount: -{discount:C}");
        }
    }
}

public class BulkOrderDiscountHandler : DiscountHandler
{
    protected override void Apply(PricingContext ctx)
    {
        if (ctx.Order.TotalQuantity >= 10)
        {
            var discount = ctx.CurrentPrice * 0.10m;
            ctx.CurrentPrice -= discount;
            ctx.AppliedDiscounts.Add($"Bulk discount (10+ items): -{discount:C}");
        }
    }
}

public class SeasonalDiscountHandler : DiscountHandler
{
    protected override void Apply(PricingContext ctx)
    {
        var isBlackFriday = DateTime.Today.Month == 11 && DateTime.Today.Day == 29;
        if (isBlackFriday)
        {
            var discount = ctx.CurrentPrice * 0.20m;
            ctx.CurrentPrice -= discount;
            ctx.AppliedDiscounts.Add($"Black Friday discount: -{discount:C}");
        }
    }
}

public class VipDiscountHandler : DiscountHandler
{
    protected override void Apply(PricingContext ctx)
    {
        if (ctx.Customer.IsVip)
        {
            var discount = ctx.CurrentPrice * 0.15m;
            ctx.CurrentPrice -= discount;
            ctx.AppliedDiscounts.Add($"VIP discount: -{discount:C}");
        }
    }
}

// Usage
var loyalty  = new LoyaltyDiscountHandler();
var bulk     = new BulkOrderDiscountHandler();
var seasonal = new SeasonalDiscountHandler();
var vip      = new VipDiscountHandler();

loyalty.SetNext(bulk).SetNext(seasonal).SetNext(vip);

var pricingCtx = new PricingContext
{
    OriginalPrice  = 100m,
    CurrentPrice   = 100m,
    Customer       = new Customer { LoyaltyYears = 5, IsVip = true },
    Order          = new Order { TotalQuantity = 15 }
};

loyalty.ApplyDiscount(pricingCtx);

Console.WriteLine($"Original:  ${pricingCtx.OriginalPrice}");
Console.WriteLine($"Final:     ${pricingCtx.CurrentPrice:F2}");
Console.WriteLine("Discounts applied:");
foreach (var d in pricingCtx.AppliedDiscounts)
    Console.WriteLine($"  - {d}");

// Original:  $100
// Final:     $63.07
// Discounts applied:
//   - Loyalty discount: -$5.00      (5% of $100 = $5 → $95)
//   - Bulk discount (10+ items): -$9.50  (10% of $95 = $9.50 → $85.50)
//   - VIP discount: -$12.83        (15% of $85.50 → $72.67 → after any seasonal)
```

---

## 7. Chain of Responsibility vs. Mediator Pipeline Behaviors

| Aspect | Chain of Responsibility | Mediator Pipeline Behaviors |
|---|---|---|
| **Structure** | Linked list of handlers | Nested function wrappers (Russian dolls) |
| **Configuration** | Manual chain building | DI container registration |
| **Request passes through** | Until a handler claims it (or all) | ALL behaviors, always |
| **Short-circuit** | Yes — by not calling next | Yes — by not calling `next()` |
| **Return value** | Optional | Always — wraps request/response |
| **Scope** | Domain logic, filtering | Cross-cutting concerns (logging, auth) |
| **Reordering** | Change `SetNext()` calls | Change DI registration order |

### Key Insight

MediatR's `IPipelineBehavior<TRequest, TResponse>` IS the Chain of Responsibility pattern applied to cross-cutting concerns. When you write:

```csharp
public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, ...)
{
    // Before
    var result = await next(); // ← This IS calling SetNext().Handle()
    // After
    return result;
}
```

...you are writing a Chain of Responsibility handler. The `next` delegate IS the rest of the chain.

---

## 8. Summary

- **Chain of Responsibility** passes a request down a linked list of handlers until one (or all) handle it.
- Each handler decides: **handle it** (and optionally stop), or **pass it along**.
- Use it when:
  - You have multiple potential handlers and don't know which one will act
  - You want to add/remove/reorder handlers without changing client code
  - You need middleware-style pipelines (logging, auth, rate limiting)
- **Two flavors**:
  - **Stop-on-first**: Chain stops when a handler claims the request (support escalation, routing)
  - **Pass-through-all**: Every handler processes it and can modify state (discount pipeline)
- ASP.NET Core middleware IS Chain of Responsibility — understanding the pattern helps you understand ASP.NET Core deeply.

---

*Next Chapter →* [Chapter 5: The Strategy Pattern](book_ch5_strategy_pattern.md)
*Previous Chapter →* [Chapter 3: The Command Pattern](book_ch3_command_pattern.md)
