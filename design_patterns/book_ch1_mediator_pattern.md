# Chapter 1: The Mediator Design Pattern in C#

> **Behavioral Pattern · GoF Classic · Enterprise-Grade**
> *"Define an object that encapsulates how a set of objects interact. Mediator promotes loose coupling by keeping objects from referring to each other explicitly, and it lets you vary their interaction independently."*
> — Gang of Four, *Design Patterns: Elements of Reusable Object-Oriented Software*

---

## Table of Contents

1. [Introduction — The Problem of Tangled Objects](#1-introduction)
2. [Core Concepts and Vocabulary](#2-core-concepts)
3. [Classic GoF Mediator — Step by Step](#3-classic-gof-mediator)
4. [Real-World Example: Chat Room](#4-real-world-chat-room)
5. [Real-World Example: Air Traffic Control](#5-real-world-air-traffic-control)
6. [MediatR Library — Production-Grade Implementation](#6-mediatr-library)
7. [CQRS with MediatR — Commands and Queries](#7-cqrs-with-mediatr)
8. [Pipeline Behaviors — Cross-Cutting Concerns](#8-pipeline-behaviors)
9. [Notifications — Fan-Out Messaging](#9-notifications)
10. [Advanced: Idempotency and Retry Behaviors](#10-advanced-behaviors)
11. [Testing Mediator-Based Code](#11-testing)
12. [Performance Considerations](#12-performance)
13. [When to Use vs. When to Avoid](#13-when-to-use)
14. [Summary and Key Takeaways](#14-summary)

---

## 1. Introduction — The Problem of Tangled Objects

Imagine you are building an e-commerce checkout system. You have these components:

- **ShoppingCart** — holds items
- **InventoryService** — checks and reserves stock
- **PricingEngine** — calculates discounts and taxes
- **PaymentGateway** — charges the customer
- **NotificationService** — sends confirmation emails
- **AuditLogger** — records everything for compliance

The naive approach is to let these objects talk to each other directly:

```csharp
// ❌ NAIVE: Everything knows about everything else
public class ShoppingCart
{
    private readonly InventoryService _inventory;
    private readonly PricingEngine _pricing;
    private readonly PaymentGateway _payment;
    private readonly NotificationService _notification;
    private readonly AuditLogger _audit;

    // ShoppingCart must be constructed with ALL these dependencies.
    // It is tightly coupled to every single service.
    public ShoppingCart(
        InventoryService inventory,
        PricingEngine pricing,
        PaymentGateway payment,
        NotificationService notification,
        AuditLogger audit)
    {
        _inventory = inventory;
        _pricing = pricing;
        _payment = payment;
        _notification = notification;
        _audit = audit;
    }

    public async Task Checkout(Order order)
    {
        await _inventory.Reserve(order);
        var price = await _pricing.Calculate(order);
        await _payment.Charge(order, price);
        await _notification.SendConfirmation(order);
        await _audit.Log(order);
    }
}
```

### The Problems

This creates an **N×N coupling** problem. Each object knows about every other object it needs to interact with. Visually:

```
ShoppingCart ─────────► InventoryService
     │ ───────────────► PricingEngine
     │ ───────────────► PaymentGateway
     │ ───────────────► NotificationService
     └───────────────► AuditLogger
```

- **High Coupling**: ShoppingCart depends on 5 services. If any of them changes, ShoppingCart must change too.
- **Hard to Test**: You cannot test ShoppingCart without instantiating all 5 services (or mocking all 5).
- **Rigid Flow**: Adding a new step (e.g., a fraud check) requires modifying ShoppingCart's source code.
- **Shotgun Surgery**: A single logical change (e.g., changing checkout flow) touches many files.

### The Mediator Solution

The Mediator pattern introduces a single **hub** object. Instead of objects communicating directly, they send messages *to the mediator*, which decides who handles what.

```
ShoppingCart ──► Mediator ──► InventoryService
                    │ ───────► PricingEngine
                    │ ───────► PaymentGateway
                    │ ───────► NotificationService
                    └────────► AuditLogger
```

Now ShoppingCart only depends on **one thing**: the Mediator. The flow logic lives in the Mediator (or in handlers), not scattered across all objects.

---

## 2. Core Concepts and Vocabulary

Before we write code, let us establish a shared vocabulary.

| Term | Meaning |
|---|---|
| **Mediator** | The central hub object that coordinates communication. |
| **Colleague** | Any object that participates in the mediated communication (all participants). |
| **Request / Message** | A data object (POCO) that describes *what* a colleague wants done. |
| **Handler** | The component that knows *how* to process a specific request. |
| **Pipeline** | A chain of behaviors that a request passes through before reaching its handler. |
| **Notification** | A one-to-many message (broadcast). Multiple handlers can respond. |

### The Two Fundamental Message Types

In modern practice (especially with MediatR), we distinguish two message types:

| Type | Description | Sender knows handler? | Returns value? |
|---|---|---|---|
| **Request** | Sent to exactly ONE handler. | No | Yes (optional) |
| **Notification** | Sent to ALL registered handlers. | No | No |

---

## 3. Classic GoF Mediator — Step by Step

Let us implement the pattern from scratch, exactly as the Gang of Four described it.

### 3.1 The Interfaces

```csharp
// The Mediator interface — the contract for all mediators.
public interface IMediator
{
    void Notify(object sender, string @event);
}

// The Colleague abstract class — all participants inherit from this.
public abstract class Colleague
{
    protected IMediator _mediator;

    public Colleague(IMediator mediator)
    {
        _mediator = mediator;
    }
}
```

### 3.2 Concrete Colleagues

Let us model a simple UI dialog with components that interact with each other.

```csharp
// Component A: A text box that triggers events
public class TextBox : Colleague
{
    public string Text { get; private set; } = string.Empty;

    public TextBox(IMediator mediator) : base(mediator) { }

    public void SetText(string text)
    {
        Text = text;
        // Notify the mediator something changed. The TextBox does NOT
        // know who will respond — that is the mediator's job.
        _mediator.Notify(this, "TextChanged");
    }
}

// Component B: A button that can be enabled or disabled
public class Button : Colleague
{
    public bool IsEnabled { get; set; } = false;

    public Button(IMediator mediator) : base(mediator) { }

    public void Click()
    {
        if (!IsEnabled) return;
        _mediator.Notify(this, "ButtonClicked");
    }
}

// Component C: A label for feedback
public class Label : Colleague
{
    public string Content { get; set; } = string.Empty;

    public Label(IMediator mediator) : base(mediator) { }
}
```

### 3.3 The Concrete Mediator

The mediator wires everything together and contains the interaction logic:

```csharp
// The Concrete Mediator knows ALL colleagues and orchestrates their interactions.
public class DialogMediator : IMediator
{
    // The mediator holds direct references to all colleagues it manages.
    public TextBox SearchBox { get; set; } = null!;
    public Button SearchButton { get; set; } = null!;
    public Label StatusLabel { get; set; } = null!;

    public void Notify(object sender, string @event)
    {
        switch (@event)
        {
            case "TextChanged":
                // When text changes, enable/disable the button based on content
                SearchButton.IsEnabled = !string.IsNullOrWhiteSpace(SearchBox.Text);
                StatusLabel.Content = SearchButton.IsEnabled
                    ? $"Ready to search for: '{SearchBox.Text}'"
                    : "Please enter a search term.";
                break;

            case "ButtonClicked":
                // When button is clicked, update the label
                StatusLabel.Content = $"Searching for '{SearchBox.Text}'...";
                Console.WriteLine($"Executing search: {SearchBox.Text}");
                break;
        }
    }
}
```

### 3.4 Putting It Together

```csharp
// --- Setup (typically done by your DI container or composition root) ---
var mediator = new DialogMediator();

var searchBox = new TextBox(mediator);
var searchButton = new Button(mediator);
var statusLabel = new Label(mediator);

// Wire the mediator to know about all colleagues
mediator.SearchBox = searchBox;
mediator.SearchButton = searchButton;
mediator.StatusLabel = statusLabel;

// --- Usage ---
Console.WriteLine($"Button enabled: {searchButton.IsEnabled}"); // False

searchBox.SetText("C# Mediator Pattern");
// Mediator reacts: enables button, updates label

Console.WriteLine($"Button enabled: {searchButton.IsEnabled}"); // True
Console.WriteLine($"Label: {statusLabel.Content}");
// Output: Ready to search for: 'C# Mediator Pattern'

searchButton.Click();
// Mediator reacts: updates label, triggers search
Console.WriteLine($"Label: {statusLabel.Content}");
// Output: Searching for 'C# Mediator Pattern'...
```

### 3.5 What Have We Achieved?

- `TextBox` does NOT know about `Button` or `Label`.
- `Button` does NOT know about `TextBox` or `Label`.
- All interaction logic lives in `DialogMediator`.
- To change the behavior, you only change the Mediator. Components remain untouched.

---

## 4. Real-World Example: Chat Room

A chat room is the canonical textbook example of the Mediator pattern. Users do not send messages directly to each other — they send them to the *chat room* (the mediator), which then distributes them.

```csharp
// --- The Mediator Interface ---
public interface IChatRoom
{
    void Register(User user);
    void Send(string from, string to, string message);
    void Broadcast(string from, string message);
}

// --- The Colleague ---
public class User
{
    private readonly IChatRoom _chatRoom;
    public string Name { get; }

    public User(string name, IChatRoom chatRoom)
    {
        Name = name;
        _chatRoom = chatRoom;
        _chatRoom.Register(this);
    }

    // User sends through the mediator, NOT directly to another user
    public void Send(string to, string message)
        => _chatRoom.Send(Name, to, message);

    public void Broadcast(string message)
        => _chatRoom.Broadcast(Name, message);

    // Mediator calls this when a message is directed to this user
    public void Receive(string from, string message)
        => Console.WriteLine($"[{Name}] Received from {from}: {message}");
}

// --- The Concrete Mediator ---
public class ChatRoom : IChatRoom
{
    private readonly Dictionary<string, User> _users = new();

    public void Register(User user)
    {
        if (!_users.ContainsKey(user.Name))
            _users[user.Name] = user;
    }

    public void Send(string from, string to, string message)
    {
        if (_users.TryGetValue(to, out var recipient))
        {
            recipient.Receive(from, message);
        }
        else
        {
            Console.WriteLine($"[System] User '{to}' not found.");
        }
    }

    public void Broadcast(string from, string message)
    {
        foreach (var (name, user) in _users)
        {
            // Don't send to the sender themselves
            if (name != from)
                user.Receive(from, $"[BROADCAST] {message}");
        }
    }
}

// --- Usage ---
var room = new ChatRoom();

var alice = new User("Alice", room);
var bob   = new User("Bob", room);
var carol = new User("Carol", room);

alice.Send("Bob", "Hey Bob, how are you?");
// [Bob] Received from Alice: Hey Bob, how are you?

bob.Send("Alice", "Great! Working on the Mediator pattern.");
// [Alice] Received from Bob: Great! Working on the Mediator pattern.

carol.Broadcast("Anyone up for a code review?");
// [Alice] Received from Carol: [BROADCAST] Anyone up for a code review?
// [Bob]   Received from Carol: [BROADCAST] Anyone up for a code review?
```

**Key Insight**: Alice never holds a reference to Bob. If Bob's class is completely rewritten, Alice's class is unaffected. The ChatRoom is the only thing that holds references to users.

---

## 5. Real-World Example: Air Traffic Control

A more sophisticated example. Aircraft do not communicate with each other — they communicate exclusively through the ATC tower (the mediator). The tower coordinates landing, takeoff, and runway assignments.

```csharp
// --- The Mediator ---
public interface IAtcTower
{
    void RegisterAircraft(Aircraft aircraft);
    void RequestLanding(string callSign, string runway);
    void NotifyDeparture(string callSign);
}

// --- The Colleague ---
public abstract class Aircraft
{
    public string CallSign { get; }
    protected IAtcTower Tower { get; }

    protected Aircraft(string callSign, IAtcTower tower)
    {
        CallSign = callSign;
        Tower = tower;
        Tower.RegisterAircraft(this);
    }

    public abstract void RequestLanding(string runway);
    public void Receive(string message) 
        => Console.WriteLine($"  [{CallSign}] ATC: {message}");
}

public class CommercialFlight : Aircraft
{
    public CommercialFlight(string callSign, IAtcTower tower)
        : base(callSign, tower) { }

    public override void RequestLanding(string runway)
    {
        Console.WriteLine($"[{CallSign}] Requesting landing on runway {runway}.");
        Tower.RequestLanding(CallSign, runway);
    }

    public void Depart()
    {
        Console.WriteLine($"[{CallSign}] Departing. Notifying tower.");
        Tower.NotifyDeparture(CallSign);
    }
}

// --- The Concrete Mediator ---
public class AtcTower : IAtcTower
{
    private readonly Dictionary<string, Aircraft> _aircraft = new();
    private readonly HashSet<string> _occupiedRunways = new();

    public void RegisterAircraft(Aircraft aircraft)
        => _aircraft[aircraft.CallSign] = aircraft;

    public void RequestLanding(string callSign, string runway)
    {
        if (!_aircraft.TryGetValue(callSign, out var requester)) return;

        if (_occupiedRunways.Contains(runway))
        {
            // Runway is busy — instruct aircraft to hold
            requester.Receive($"Runway {runway} is occupied. Hold at current altitude.");
            
            // Notify other aircraft about the traffic
            foreach (var (sign, plane) in _aircraft)
                if (sign != callSign)
                    plane.Receive($"Traffic alert: {callSign} is holding for runway {runway}.");
        }
        else
        {
            // Clear to land
            _occupiedRunways.Add(runway);
            requester.Receive($"Cleared to land on runway {runway}. Wind 270 at 10.");
        }
    }

    public void NotifyDeparture(string callSign)
    {
        // Free up the runway this aircraft was using
        // (simplified — in reality runway is tracked per aircraft)
        Console.WriteLine($"  [ATC] {callSign} departed. Runway cleared.");
    }
}

// --- Usage ---
var tower = new AtcTower();

var ua123 = new CommercialFlight("UA123", tower);
var ba456 = new CommercialFlight("BA456", tower);

ua123.RequestLanding("28L");
// [UA123] Requesting landing on runway 28L.
//   [UA123] ATC: Cleared to land on runway 28L. Wind 270 at 10.

ba456.RequestLanding("28L"); // Same runway!
// [BA456] Requesting landing on runway 28L.
//   [BA456] ATC: Runway 28L is occupied. Hold at current altitude.
//   [UA123] ATC: Traffic alert: BA456 is holding for runway 28L.
```

---

## 6. MediatR Library — Production-Grade Implementation

Building a custom mediator works for simple cases, but in production .NET applications, you use the **MediatR** library by Jimmy Bogard. It is the de-facto standard Mediator implementation in the C#/.NET ecosystem.

### 6.1 Installation

```bash
dotnet add package MediatR
dotnet add package MediatR.Extensions.Microsoft.DependencyInjection  # For older versions
# In MediatR v12+, DI registration is built in
```

### 6.2 Registration with .NET DI

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMediatR(cfg =>
{
    // Scans the assembly and registers all IRequestHandler<,> implementations automatically.
    cfg.RegisterServicesFromAssemblyContaining<Program>();
});

var app = builder.Build();
```

### 6.3 Core MediatR Interfaces

MediatR provides three fundamental interfaces:

```csharp
// 1. IRequest<TResponse> — A request that expects a single response.
//    Maps to exactly ONE handler.
public interface IRequest<out TResponse> { }

// 2. IRequestHandler<TRequest, TResponse> — Handles a specific IRequest.
public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

// 3. INotification — A broadcast message. Can have ZERO or MANY handlers.
public interface INotification { }

// 4. INotificationHandler<TNotification> — Handles a specific INotification.
public interface INotificationHandler<in TNotification>
    where TNotification : INotification
{
    Task Handle(TNotification notification, CancellationToken cancellationToken);
}
```

---

## 7. CQRS with MediatR — Commands and Queries

MediatR pairs beautifully with **CQRS** (Command Query Responsibility Segregation). Every operation becomes either a **Command** (writes data, returns void or an ID) or a **Query** (reads data, never mutates state).

### 7.1 Creating a Query

```csharp
// --- The Query (IRequest with a return type) ---
// Represents the INTENT: "I want to get a product by ID."
public record GetProductByIdQuery(int ProductId) : IRequest<ProductDto>;

// --- The DTO (Data Transfer Object) ---
public record ProductDto(int Id, string Name, decimal Price, int StockQuantity);

// --- The Handler ---
// Single Responsibility: This class only knows how to handle GetProductByIdQuery.
public class GetProductByIdQueryHandler : IRequestHandler<GetProductByIdQuery, ProductDto>
{
    private readonly IProductRepository _repository;

    public GetProductByIdQueryHandler(IProductRepository repository)
    {
        _repository = repository;
    }

    public async Task<ProductDto> Handle(
        GetProductByIdQuery request,
        CancellationToken cancellationToken)
    {
        var product = await _repository.GetByIdAsync(request.ProductId, cancellationToken);

        if (product is null)
            throw new NotFoundException($"Product {request.ProductId} not found.");

        return new ProductDto(product.Id, product.Name, product.Price, product.Stock);
    }
}
```

### 7.2 Using the Query in a Controller

```csharp
[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    // The controller ONLY depends on IMediator.
    // It knows nothing about repositories, databases, or business logic.
    private readonly IMediator _mediator;

    public ProductsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ProductDto>> GetProduct(int id, CancellationToken ct)
    {
        // The controller simply sends a query and returns the result.
        var product = await _mediator.Send(new GetProductByIdQuery(id), ct);
        return Ok(product);
    }
}
```

### 7.3 Creating a Command

```csharp
// --- The Command ---
// A command has a clear, imperative name. It describes an ACTION.
public record CreateProductCommand(
    string Name,
    decimal Price,
    int InitialStock
) : IRequest<int>; // Returns the new product's ID

// --- The Command Handler ---
public class CreateProductCommandHandler : IRequestHandler<CreateProductCommand, int>
{
    private readonly IProductRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public CreateProductCommandHandler(
        IProductRepository repository,
        IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<int> Handle(
        CreateProductCommand request,
        CancellationToken cancellationToken)
    {
        // Business rule validation
        if (request.Price <= 0)
            throw new DomainException("Product price must be positive.");

        var product = new Product
        {
            Name  = request.Name,
            Price = request.Price,
            Stock = request.InitialStock
        };

        await _repository.AddAsync(product, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return product.Id;
    }
}

// --- Controller usage ---
[HttpPost]
public async Task<ActionResult<int>> CreateProduct(
    CreateProductCommand command,
    CancellationToken ct)
{
    var newId = await _mediator.Send(command, ct);
    return CreatedAtAction(nameof(GetProduct), new { id = newId }, newId);
}
```

### 7.4 Command with No Return Value

For commands that don't need to return data, use `Unit` (MediatR's equivalent of `void`):

```csharp
// IRequest with no response type — uses Unit internally
public record DeleteProductCommand(int ProductId) : IRequest;

public class DeleteProductCommandHandler : IRequestHandler<DeleteProductCommand>
{
    private readonly IProductRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public DeleteProductCommandHandler(
        IProductRepository repository,
        IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(DeleteProductCommand request, CancellationToken cancellationToken)
    {
        var product = await _repository.GetByIdAsync(request.ProductId, cancellationToken)
            ?? throw new NotFoundException($"Product {request.ProductId} not found.");

        _repository.Remove(product);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
```

---

## 8. Pipeline Behaviors — Cross-Cutting Concerns

This is where MediatR truly shines. **Pipeline Behaviors** are middleware that wrap every request/response cycle. Think of them like ASP.NET Core middleware, but for your business logic layer.

```
Request ──► [Logging] ──► [Validation] ──► [Caching] ──► [Handler] ──► Response
                                                                         │
Response ◄── [Logging] ◄── [Validation] ◄── [Caching] ◄──────────────────┘
```

### 8.1 The IPipelineBehavior Interface

```csharp
public interface IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,  // The next step in the pipeline
        CancellationToken cancellationToken);
}
```

### 8.2 Logging Behavior

Automatically logs every request and its response time:

```csharp
public class LoggingBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly ILogger<LoggingBehavior<TRequest, TResponse>> _logger;

    public LoggingBehavior(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    {
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;
        _logger.LogInformation("Handling {RequestName}: {@Request}", requestName, request);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await next(); // Call the next behavior or the actual handler
            stopwatch.Stop();
            _logger.LogInformation(
                "Handled {RequestName} in {ElapsedMs}ms",
                requestName, stopwatch.ElapsedMilliseconds);
            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex,
                "Error handling {RequestName} after {ElapsedMs}ms",
                requestName, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
```

### 8.3 Validation Behavior (with FluentValidation)

```csharp
// Install: dotnet add package FluentValidation.DependencyInjectionExtensions

public class ValidationBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    // MediatR DI will inject ALL registered validators for TRequest
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
    {
        _validators = validators;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!_validators.Any())
            return await next();

        var context = new ValidationContext<TRequest>(request);

        // Run all validators for this request type in parallel
        var validationResults = await Task.WhenAll(
            _validators.Select(v => v.ValidateAsync(context, cancellationToken)));

        var failures = validationResults
            .SelectMany(r => r.Errors)
            .Where(f => f is not null)
            .ToList();

        if (failures.Count > 0)
            throw new ValidationException(failures);

        return await next();
    }
}

// The validator for our CreateProductCommand:
public class CreateProductCommandValidator : AbstractValidator<CreateProductCommand>
{
    public CreateProductCommandValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Product name is required.")
            .MaximumLength(200).WithMessage("Product name cannot exceed 200 characters.");

        RuleFor(x => x.Price)
            .GreaterThan(0).WithMessage("Price must be greater than zero.");

        RuleFor(x => x.InitialStock)
            .GreaterThanOrEqualTo(0).WithMessage("Initial stock cannot be negative.");
    }
}
```

### 8.4 Caching Behavior

```csharp
// Mark a query as cacheable
public interface ICacheableQuery
{
    string CacheKey { get; }
    TimeSpan CacheDuration { get; }
}

public record GetProductByIdQuery(int ProductId)
    : IRequest<ProductDto>, ICacheableQuery
{
    public string CacheKey => $"Product_{ProductId}";
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);
}

public class CachingBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ICacheableQuery
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<CachingBehavior<TRequest, TResponse>> _logger;

    public CachingBehavior(
        IMemoryCache cache,
        ILogger<CachingBehavior<TRequest, TResponse>> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // Try to get from cache
        if (_cache.TryGetValue(request.CacheKey, out TResponse? cachedResponse))
        {
            _logger.LogDebug("Cache hit for key: {CacheKey}", request.CacheKey);
            return cachedResponse!;
        }

        // Cache miss — call handler and store result
        _logger.LogDebug("Cache miss for key: {CacheKey}", request.CacheKey);
        var response = await next();

        _cache.Set(request.CacheKey, response, request.CacheDuration);
        return response;
    }
}
```

### 8.5 Transaction Behavior

```csharp
public interface ITransactionalCommand { }

public record CreateProductCommand(...) : IRequest<int>, ITransactionalCommand;

public class TransactionBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ITransactionalCommand
{
    private readonly AppDbContext _dbContext;

    public TransactionBehavior(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(cancellationToken);
        try
        {
            var response = await next();
            await transaction.CommitAsync(cancellationToken);
            return response;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
```

### 8.6 Registering Behaviors

Behaviors are applied in the order they are registered. Think carefully about ordering!

```csharp
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<Program>();

    // ORDER MATTERS: Behaviors are applied outer-to-inner (first registered = outermost).
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(CachingBehavior<,>));
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));
});

// Register FluentValidation validators
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddMemoryCache();
```

**Pipeline execution order for a request:**
```
Logging → Validation → Caching → Transaction → Handler
                                                   │
Logging ← Validation ← Caching ← Transaction ←────┘
```

---

## 9. Notifications — Fan-Out Messaging

Notifications are broadcast messages. Unlike Requests (one handler), Notifications can have **any number of handlers** — zero, one, or many. They are fire-and-forget within the MediatR pipeline (all handlers are awaited, but the sender doesn't get return values).

### 9.1 Defining a Notification

```csharp
// A domain event: something that HAPPENED in our system.
// Name it in the past tense — it has already occurred.
public record ProductCreatedNotification(
    int ProductId,
    string ProductName,
    decimal Price,
    DateTime CreatedAt
) : INotification;
```

### 9.2 Multiple Handlers

```csharp
// Handler 1: Send a welcome email
public class SendProductCreatedEmailHandler
    : INotificationHandler<ProductCreatedNotification>
{
    private readonly IEmailService _emailService;

    public SendProductCreatedEmailHandler(IEmailService emailService)
        => _emailService = emailService;

    public async Task Handle(
        ProductCreatedNotification notification,
        CancellationToken cancellationToken)
    {
        await _emailService.SendAsync(
            to: "admin@store.com",
            subject: "New Product Created",
            body: $"Product '{notification.ProductName}' created at {notification.CreatedAt}.");
    }
}

// Handler 2: Update search index
public class UpdateSearchIndexHandler
    : INotificationHandler<ProductCreatedNotification>
{
    private readonly ISearchIndexService _searchIndex;

    public UpdateSearchIndexHandler(ISearchIndexService searchIndex)
        => _searchIndex = searchIndex;

    public async Task Handle(
        ProductCreatedNotification notification,
        CancellationToken cancellationToken)
    {
        await _searchIndex.IndexAsync(new SearchDocument
        {
            Id   = notification.ProductId.ToString(),
            Name = notification.ProductName,
            Type = "Product"
        }, cancellationToken);
    }
}

// Handler 3: Audit log
public class AuditProductCreatedHandler
    : INotificationHandler<ProductCreatedNotification>
{
    private readonly IAuditService _audit;

    public AuditProductCreatedHandler(IAuditService audit)
        => _audit = audit;

    public async Task Handle(
        ProductCreatedNotification notification,
        CancellationToken cancellationToken)
    {
        await _audit.LogAsync(
            $"Product created: ID={notification.ProductId}, Name={notification.ProductName}",
            cancellationToken);
    }
}
```

### 9.3 Publishing from a Command Handler

```csharp
public class CreateProductCommandHandler : IRequestHandler<CreateProductCommand, int>
{
    private readonly IProductRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPublisher _publisher; // Use IPublisher for notifications (lighter than IMediator)

    public CreateProductCommandHandler(
        IProductRepository repository,
        IUnitOfWork unitOfWork,
        IPublisher publisher)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _publisher = publisher;
    }

    public async Task<int> Handle(
        CreateProductCommand request,
        CancellationToken cancellationToken)
    {
        var product = new Product { Name = request.Name, Price = request.Price };
        await _repository.AddAsync(product, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Publish the notification — MediatR will call ALL registered handlers.
        // ALL three handlers above will run (email, search index, audit).
        await _publisher.Publish(new ProductCreatedNotification(
            product.Id, product.Name, product.Price, DateTime.UtcNow
        ), cancellationToken);

        return product.Id;
    }
}
```

### 9.4 Adding a New Handler = Zero Change to Existing Code

The most powerful aspect: you can add a 4th handler (e.g., `SendSlackNotificationHandler`) without touching ANY existing code. The `CreateProductCommandHandler` stays exactly the same. This is the **Open/Closed Principle** in action.

---

## 10. Advanced: Idempotency and Retry Behaviors

### 10.1 Idempotency Behavior

Prevent duplicate command processing (critical for payment operations):

```csharp
public interface IIdempotentCommand
{
    Guid IdempotencyKey { get; }
}

public record ProcessPaymentCommand(
    Guid IdempotencyKey,
    int OrderId,
    decimal Amount
) : IRequest<PaymentResult>, IIdempotentCommand;

public class IdempotencyBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IIdempotentCommand
{
    private readonly IIdempotencyStore _store;

    public IdempotencyBehavior(IIdempotencyStore store) => _store = store;

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // Check if we already processed this exact request
        var existingResult = await _store.GetAsync<TResponse>(
            request.IdempotencyKey, cancellationToken);

        if (existingResult is not null)
        {
            // Return the cached result — do NOT process again
            return existingResult;
        }

        var response = await next();

        // Store the result so future duplicate requests get the same answer
        await _store.SetAsync(request.IdempotencyKey, response, cancellationToken);

        return response;
    }
}
```

### 10.2 Retry Behavior with Polly

```csharp
// Install: dotnet add package Polly

public class RetryBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private static readonly ResiliencePipeline _pipeline = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromMilliseconds(200),
            BackoffType = DelayBackoffType.Exponential,
            ShouldHandle = new PredicateBuilder()
                .Handle<TransientException>()
                .Handle<TimeoutException>()
        })
        .Build();

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        return await _pipeline.ExecuteAsync(
            async ct => await next(), cancellationToken);
    }
}
```

---

## 11. Testing Mediator-Based Code

One of the biggest benefits of the Mediator pattern is testability. Handlers are simple classes with no framework coupling.

### 11.1 Unit Testing a Handler

```csharp
// Using xUnit + NSubstitute (or Moq)
public class GetProductByIdQueryHandlerTests
{
    private readonly IProductRepository _repository;
    private readonly GetProductByIdQueryHandler _handler;

    public GetProductByIdQueryHandlerTests()
    {
        // Create a mock repository — NO need to mock IMediator or anything else
        _repository = Substitute.For<IProductRepository>();
        _handler    = new GetProductByIdQueryHandler(_repository);
    }

    [Fact]
    public async Task Handle_ExistingProduct_ReturnsProductDto()
    {
        // Arrange
        var productId = 42;
        var product = new Product { Id = productId, Name = "Test Widget", Price = 9.99m };
        _repository.GetByIdAsync(productId, Arg.Any<CancellationToken>())
                   .Returns(product);

        // Act
        var result = await _handler.Handle(
            new GetProductByIdQuery(productId), CancellationToken.None);

        // Assert
        Assert.Equal(productId, result.Id);
        Assert.Equal("Test Widget", result.Name);
        Assert.Equal(9.99m, result.Price);
    }

    [Fact]
    public async Task Handle_NonExistentProduct_ThrowsNotFoundException()
    {
        // Arrange
        _repository.GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                   .Returns((Product?)null);

        // Act & Assert
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _handler.Handle(new GetProductByIdQuery(999), CancellationToken.None));
    }
}
```

### 11.2 Integration Testing with IMediator

```csharp
// Test through the full pipeline (all behaviors included)
public class CreateProductCommandIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly IMediator _mediator;

    public CreateProductCommandIntegrationTests(WebApplicationFactory<Program> factory)
    {
        var scope = factory.Services.CreateScope();
        _mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
    }

    [Fact]
    public async Task CreateProduct_ValidCommand_ReturnsNewId()
    {
        // Arrange
        var command = new CreateProductCommand("Test Product", 19.99m, 100);

        // Act — runs through ALL pipeline behaviors (logging, validation, transaction)
        var id = await _mediator.Send(command);

        // Assert
        Assert.True(id > 0);
    }

    [Fact]
    public async Task CreateProduct_InvalidPrice_ThrowsValidationException()
    {
        var command = new CreateProductCommand("Bad Product", -5m, 10);

        await Assert.ThrowsAsync<ValidationException>(() => _mediator.Send(command));
    }
}
```

---

## 12. Performance Considerations

MediatR has minimal overhead, but there are things to be aware of:

### 12.1 The Cost of Reflection

MediatR uses reflection to discover and invoke handlers. The reflection calls are cached after the first invocation, so **cold-start is the only concern**, not steady-state performance.

```csharp
// MediatR v12+ uses compiled expression trees for handler invocation,
// dramatically reducing the reflection overhead.
// Benchmarks show ~50-200ns overhead per request — negligible in real-world scenarios.
```

### 12.2 Avoid Overusing Notifications for Performance-Critical Paths

Notifications are sequential by default — handlers run one after another. For high-throughput scenarios, publish to a message broker instead (SQS, RabbitMQ) rather than using in-process notifications.

```csharp
// ❌ BAD for high-throughput: In-process notification with heavy handler
public class HeavyEmailHandler : INotificationHandler<OrderPlacedNotification>
{
    public async Task Handle(OrderPlacedNotification n, CancellationToken ct)
    {
        await _emailService.SendComplexEmailAsync(n); // Takes 200ms
    }
}

// ✅ BETTER: Publish to a queue, process asynchronously in the background
public class PublishToQueueHandler : INotificationHandler<OrderPlacedNotification>
{
    public async Task Handle(OrderPlacedNotification n, CancellationToken ct)
    {
        await _sqsClient.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl    = "https://sqs.us-east-1.amazonaws.com/.../order-emails",
            MessageBody = JsonSerializer.Serialize(n)
        }, ct); // Takes ~5ms
    }
}
```

---

## 13. When to Use vs. When to Avoid

### ✅ Use the Mediator Pattern When:

| Situation | Reason |
|---|---|
| You have many components that need to interact | Mediator reduces N×N coupling to N×1 |
| You want to apply cross-cutting concerns (logging, validation) consistently | Pipeline behaviors are perfect for this |
| You are implementing CQRS | MediatR is the standard tool for CQRS in .NET |
| Your controllers are getting fat with business logic | Move logic to handlers |
| You want to add features without modifying existing code | Add new handlers or behaviors |
| You need testable business logic | Handlers are plain classes, easy to unit test |

### ❌ Avoid the Mediator Pattern When:

| Situation | Reason |
|---|---|
| You have simple CRUD with no business logic | Adds unnecessary complexity |
| The "mediator" becomes a god object with all logic | You have not separated concerns properly — split into multiple mediators |
| You have very few objects interacting | Direct dependencies are simpler and equally valid |
| Performance is ultra-critical at nanosecond scale | Direct method calls are faster (though difference is tiny) |

---

## 14. Summary and Key Takeaways

### The 5 Principles of the Mediator Pattern

1. **Decouple Colleagues**: Objects communicate via the mediator, not directly. They do not hold references to each other.
2. **Centralize Interaction Logic**: The "how do we coordinate?" question has one answer — the Mediator.
3. **Single Responsibility**: Each Handler does exactly one thing. Behaviors handle cross-cutting concerns.
4. **Open/Closed**: Add new behaviors and handlers without modifying existing code.
5. **Testability First**: Handlers are plain classes. They test like plain classes.

### The MediatR Mental Model

```
Controller/Client
       │  sends Request (POCO)
       ▼
   IMediator.Send()
       │
       ├──► [LoggingBehavior]
       │         │
       │    ├──► [ValidationBehavior]
       │    │         │
       │    │    ├──► [CachingBehavior]
       │    │    │         │
       │    │    │    ├──► [IRequestHandler]  ← Your actual business logic
       │    │    │    │
       │    │    │    └──► Publishes INotification
       │    │    │              │
       │    │    │         ├──► [Handler1]  (Email)
       │    │    │         ├──► [Handler2]  (Search Index)
       │    │    │         └──► [Handler3]  (Audit Log)
```

### Quick Reference

```
Query    = IRequest<TResponse>    → reads data, returns something
Command  = IRequest / IRequest<T> → writes data, may return an ID
Handler  = IRequestHandler<,>     → processes exactly one Request type
Behavior = IPipelineBehavior<,>   → wraps all requests (logging, validation, etc.)
Notify   = INotification          → broadcast event, any number of handlers
```

---

*Next Chapter →* **Chapter 2: The Observer Pattern — Event-Driven Decoupling**

*Previous Chapter →* [Index](book_INDEX.md)
