# Chapter 6: Pattern Comparison Master Guide

> **The Definitive Cross-Pattern Reference**
> *When you understand not just each pattern but how they relate to each other, you've moved from knowing patterns to thinking in patterns.*

---

## Table of Contents

1. [The Pattern Family Tree](#1-family-tree)
2. [The Master Comparison Table](#2-master-comparison)
3. [The Decision Flowchart](#3-decision-flowchart)
4. [How the Patterns Compose Together](#4-composition)
5. [Anti-Patterns — Common Mistakes](#5-anti-patterns)
6. [Pattern Quick-Reference Card](#6-quick-reference)

---

## 1. The Pattern Family Tree

All five patterns in this book are **Behavioral patterns** — they deal with *how objects communicate and responsibilities are distributed*.

```
Behavioral Patterns
│
├── Communication Patterns (how messages flow)
│   ├── Mediator     — All-to-all through a hub (N objects → 1 mediator → N objects)
│   ├── Observer     — One-to-many push notifications (1 subject → N observers)
│   └── Chain of Responsibility — Sequential one-at-a-time (Request → H1 → H2 → H3)
│
└── Action/Algorithm Patterns (how work is packaged)
    ├── Command      — Packages an ACTION as an object (execute, undo, queue)
    └── Strategy     — Packages an ALGORITHM as an object (swap, inject, compose)
```

---

## 2. The Master Comparison Table

| Aspect | Mediator | Observer | Chain of Responsibility | Command | Strategy |
|---|---|---|---|---|---|
| **One-liner** | Hub for cross-object communication | Push broadcast to many listeners | Pass request down a chain until handled | Encapsulate action as object | Encapsulate algorithm as object |
| **Problem solved** | N×N coupling | Unknown number of listeners | Who handles this? | Undo, queue, log, defer | Swap algorithm at runtime |
| **Coupling direction** | Colleagues → Mediator | Subject ↔ Observers | Chain is linked | Invoker → Command → Receiver | Context → Strategy |
| **Number of handlers** | One handler per request type | All observers notified | One handler claims it (or all process) | One (the Receiver) | One (the chosen strategy) |
| **Short-circuit?** | N/A | No | Yes | N/A | N/A |
| **Undo support?** | No | No | No | ✅ Yes | No |
| **Runtime swap?** | No (DI at startup) | Yes (attach/detach) | Yes (rebuild chain) | Yes (queue different commands) | ✅ Yes |
| **Returns value?** | Yes (Request→Response) | No (fire and forget) | Optional | Optional | ✅ Yes (algorithm output) |
| **State stored?** | No | No | No | ✅ Yes (pre-execution state) | No |
| **DI-friendly?** | ✅ Excellent (IMediator) | Good | Good | Good | ✅ Excellent (IEnumerable<IStrategy>) |
| **.NET example** | MediatR, SignalR Hub | C# events, IObservable | ASP.NET middleware | WPF ICommand, Game engines | LINQ .OrderBy(strategy) |
| **Best for** | CQRS, complex UI, orchestration | Domain events, reactive UIs | Request filtering, validation pipelines | Text editors, game commands, transactions | Payment methods, export formats, sorting |

---

## 3. The Decision Flowchart

Use this tree when you're unsure which pattern to reach for:

```
START: What is your core problem?
│
├─ "Multiple objects need to coordinate complex interactions"
│        └─ Do they need to talk TO EACH OTHER?
│               ├─ YES: Use MEDIATOR (they all talk through a hub)
│               └─ NO: Is it one object notifying others of changes?
│                       ├─ YES: Use OBSERVER (subject pushes to listeners)
│                       └─ NO: Is there a sequential chain to try?
│                               └─ YES: Use CHAIN OF RESPONSIBILITY
│
├─ "I need to package an operation"
│        └─ Do you need to UNDO or QUEUE it?
│               ├─ YES: Use COMMAND (stores state, reversible)
│               └─ NO: Use STRATEGY (stateless, swappable algorithm)
│
└─ "I need to handle a request and return a result"
         └─ Does one specific class handle it?
                ├─ YES and I want to decouple: Use MEDIATOR (Request → Handler)
                └─ YES and I want to try multiple: Use CHAIN OF RESPONSIBILITY
```

---

## 4. How the Patterns Compose Together

These patterns are not mutually exclusive. Production systems combine them constantly.

### 4.1 Mediator + Strategy

The most common combination. MediatR dispatches to a handler (Mediator), and the handler uses a Strategy for the algorithm:

```csharp
// MediatR handler (Mediator pattern)
public class ProcessPaymentHandler : IRequestHandler<ProcessPaymentCommand, PaymentResult>
{
    private readonly IStrategyResolver<IPaymentStrategy> _resolver; // Strategy pattern

    public async Task<PaymentResult> Handle(ProcessPaymentCommand cmd, CancellationToken ct)
    {
        // Mediator delivers the message; Strategy decides HOW to process payment
        var strategy = _resolver.Resolve(cmd.PaymentMethod);
        return await strategy.ProcessAsync(cmd.PaymentRequest, ct);
    }
}
```

### 4.2 Mediator + Observer (Domain Events)

A command handler (Mediator) does its work, then publishes a Notification (Observer broadcast):

```csharp
public class CreateOrderHandler : IRequestHandler<CreateOrderCommand, int>
{
    private readonly IPublisher _publisher; // Mediator's notification interface

    public async Task<int> Handle(CreateOrderCommand cmd, CancellationToken ct)
    {
        var order = await CreateOrderInDb(cmd, ct);

        // After work is done, BROADCAST the event — Observer pattern in action
        // Zero, one, or many handlers can react
        await _publisher.Publish(new OrderCreatedNotification(order.Id, order.CustomerId), ct);

        return order.Id;
    }
}
```

### 4.3 Mediator + Chain of Responsibility (Pipeline Behaviors)

MediatR's Pipeline Behaviors ARE Chain of Responsibility applied to every request:

```csharp
// This is literally the Chain of Responsibility pattern
public class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
{
    public async Task<TResponse> Handle(TRequest request,
        RequestHandlerDelegate<TResponse> next,  // ← "next handler in the chain"
        CancellationToken ct)
    {
        await ValidateAsync(request, ct); // My work
        return await next();              // Pass down the chain
    }
}
```

### 4.4 Command + Observer

A Command executes and notifies observers of the state change:

```csharp
public class BoldTextCommand : ICommand
{
    public event EventHandler? FormatChanged; // Observer hook

    public void Execute()
    {
        _document.ApplyBold(_selection);
        FormatChanged?.Invoke(this, EventArgs.Empty); // Notify toolbar to update button state
    }

    public void Undo()
    {
        _document.RemoveBold(_selection);
        FormatChanged?.Invoke(this, EventArgs.Empty);
    }
}
```

### 4.5 Full Enterprise Stack — All Five Patterns Together

```
HTTP Request
    │
    ▼
[ASP.NET Core Middleware Pipeline]  ← Chain of Responsibility
    │  LoggingMiddleware
    │  AuthMiddleware
    │  RateLimitMiddleware
    │
    ▼
[Controller]
    │  mediator.Send(new CreateOrderCommand(...))
    │
    ▼
[MediatR Pipeline]                  ← Chain of Responsibility (Behaviors)
    │  LoggingBehavior
    │  ValidationBehavior
    │  TransactionBehavior
    │
    ▼
[CreateOrderCommandHandler]         ← Mediator (dispatches the command)
    │
    ├─ Uses IPaymentStrategy        ← Strategy (selects payment method)
    │      CreditCardStrategy | PayPalStrategy | CryptoStrategy
    │
    ├─ Uses IShippingStrategy       ← Strategy (selects shipping)
    │      StandardPost | Express | SameDay
    │
    └─ Publishes OrderCreatedNotification  ← Observer (fan-out)
            │
            ├─► SendConfirmationEmailHandler
            ├─► UpdateInventoryHandler
            ├─► NotifyWarehouseHandler
            └─► UpdateAnalyticsDashboardHandler
```

---

## 5. Anti-Patterns — Common Mistakes

### 5.1 ❌ The God Mediator

The whole point of Mediator is to reduce coupling — don't put ALL your logic into one mediator class:

```csharp
// ❌ WRONG: Mediator becomes a 5,000-line monster
public class AppMediator : IAppMediator
{
    public void Notify(object sender, string @event)
    {
        if (@event == "UserRegistered") { /* 50 lines */ }
        else if (@event == "OrderPlaced") { /* 80 lines */ }
        else if (@event == "PaymentFailed") { /* 60 lines */ }
        // ... 200 more conditions
    }
}

// ✅ RIGHT: Use MediatR — each handler is its own class with its own responsibility
public class OrderPlacedHandler : INotificationHandler<OrderPlacedNotification> { }
public class PaymentFailedHandler : INotificationHandler<PaymentFailedNotification> { }
```

### 5.2 ❌ Memory Leaks in Observer

Always unsubscribe from events when the observer is no longer needed:

```csharp
// ❌ WRONG: Event subscription outlives the component — memory leak
public class StockChart : UserControl
{
    public StockChart(StockTicker ticker)
    {
        ticker.PriceChanged += OnPriceChanged; // Subscribed forever, chart never collected
    }
}

// ✅ RIGHT: Implement IDisposable and unsubscribe
public class StockChart : UserControl, IDisposable
{
    private readonly StockTicker _ticker;

    public StockChart(StockTicker ticker)
    {
        _ticker = ticker;
        _ticker.PriceChanged += OnPriceChanged;
    }

    public void Dispose()
    {
        _ticker.PriceChanged -= OnPriceChanged; // Clean up
    }
}
```

### 5.3 ❌ Commands That Don't Capture State for Undo

```csharp
// ❌ WRONG: No state captured — Undo is impossible
public class DeleteRowCommand : ICommand
{
    private readonly DataGrid _grid;
    private readonly int _rowIndex;

    public void Execute() => _grid.RemoveRow(_rowIndex);
    public void Undo()    => _grid.InsertRow(_rowIndex, /* what data? */);
    // ^^^ We threw away the row data in Execute! We cannot restore it.
}

// ✅ RIGHT: Capture state BEFORE execution
public class DeleteRowCommand : ICommand
{
    private DataRow? _deletedRow; // Store it!

    public void Execute()
    {
        _deletedRow = _grid.GetRow(_rowIndex); // Save BEFORE deleting
        _grid.RemoveRow(_rowIndex);
    }

    public void Undo()
    {
        if (_deletedRow is not null)
            _grid.InsertRow(_rowIndex, _deletedRow); // Restore from saved copy
    }
}
```

### 5.4 ❌ Strategy That Mutates Shared State

Strategies should be **stateless** — if they store state, parallel requests will corrupt each other:

```csharp
// ❌ WRONG: Strategy stores request state — not thread-safe
public class CreditCardStrategy : IPaymentStrategy
{
    private PaymentRequest? _currentRequest; // Shared state in a singleton!

    public async Task<PaymentResult> ProcessAsync(PaymentRequest request, CancellationToken ct)
    {
        _currentRequest = request; // ← Two simultaneous requests corrupt each other
        await Task.Delay(100, ct);
        return await ChargeCard(_currentRequest!); // Wrong request might be used!
    }
}

// ✅ RIGHT: All state is in method parameters or local variables
public class CreditCardStrategy : IPaymentStrategy
{
    public async Task<PaymentResult> ProcessAsync(PaymentRequest request, CancellationToken ct)
    {
        // request is a parameter — each call has its own copy. Thread-safe.
        return await ChargeCard(request.CardNumber, request.Amount, ct);
    }
}
```

### 5.5 ❌ Chain That Silently Drops Requests

```csharp
// ❌ WRONG: Request falls off the end of the chain with no indication
public class DefaultHandler : Handler
{
    public override void Handle(SupportTicket ticket)
    {
        // No next handler set — ticket simply disappears
        // The caller has no idea it was not handled!
    }
}

// ✅ RIGHT: Always handle the "no one claimed it" case explicitly
public class DefaultHandler : Handler
{
    public override void Handle(SupportTicket ticket)
    {
        if (_next is not null)
            _next.Handle(ticket);
        else
        {
            // Log it! Alert! Add to dead-letter queue!
            Console.WriteLine($"WARNING: No handler for ticket #{ticket.Id}. Adding to manual queue.");
            _manualQueue.Enqueue(ticket);
        }
    }
}
```

---

## 6. Pattern Quick-Reference Card

### When to Reach for Each Pattern

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                    BEHAVIORAL PATTERN QUICK REFERENCE                       │
├───────────────────┬─────────────────────────────────────────────────────────┤
│ MEDIATOR          │ "I want objects to communicate without knowing about     │
│                   │  each other. One hub routes all traffic."               │
│                   │ Keywords: CQRS, IRequest, IRequestHandler, IMediator    │
├───────────────────┼─────────────────────────────────────────────────────────┤
│ OBSERVER          │ "One thing changed, and I want any number of things to  │
│                   │  react automatically."                                  │
│                   │ Keywords: event, EventHandler, IObservable, Subscribe   │
├───────────────────┼─────────────────────────────────────────────────────────┤
│ CHAIN OF          │ "I have a request and a sequence of potential handlers.  │
│ RESPONSIBILITY    │  Let each one decide if it handles it."                 │
│                   │ Keywords: middleware, pipeline, SetNext, next()         │
├───────────────────┼─────────────────────────────────────────────────────────┤
│ COMMAND           │ "I want to store an action for later — undo it, queue   │
│                   │  it, replay it, or log it."                             │
│                   │ Keywords: Execute, Undo, Invoker, history stack         │
├───────────────────┼─────────────────────────────────────────────────────────┤
│ STRATEGY          │ "I have multiple ways to do the same thing and want to  │
│                   │  pick the right one at runtime."                        │
│                   │ Keywords: IStrategy, CanHandle, DI factory, IEnumerable │
└───────────────────┴─────────────────────────────────────────────────────────┘
```

### The Golden Rules

1. **Mediator**: *"Colleagues know the mediator, not each other."*
2. **Observer**: *"The subject doesn't care who is listening."*
3. **Chain of Responsibility**: *"Each handler asks: is this mine? If not, pass it on."*
4. **Command**: *"Always save state BEFORE you execute, so you can undo."*
5. **Strategy**: *"Keep strategies stateless — they are algorithms, not actors."*

---

*Previous Chapter →* [Chapter 5: The Strategy Pattern](book_ch5_strategy_pattern.md)
*Back to Index →* [Master Index](book_INDEX.md)
