# 📖 Behavioral Design Patterns in C# — Master Index

> **6 Chapters · Deep Dive · Production-Quality Code**
> Covers the Mediator pattern and its closest relatives in the C#/.NET ecosystem.
> Designed to prepare you for real enterprise work with CQRS, MediatR, and beyond.

---

## 📂 All Chapters

| # | Chapter | Core Pattern | Key Topics |
|---|---|---|---|
| 1 | **[The Mediator Pattern](book_ch1_mediator_pattern.md)** | Mediator | GoF classic, MediatR library, CQRS, Pipeline Behaviors, Notifications, Idempotency, Testing |
| 2 | **[The Observer Pattern](book_ch2_observer_pattern.md)** | Observer | GoF classic, C# Events & Delegates, IObservable/IObserver, Domain Events in DDD |
| 3 | **[The Command Pattern](book_ch3_command_pattern.md)** | Command | GoF classic, Undo/Redo, Macro Commands, Command Queuing, Transactional Rollback |
| 4 | **[The Chain of Responsibility Pattern](book_ch4_chain_of_responsibility.md)** | Chain of Responsibility | Support escalation, ASP.NET Middleware, Validation pipeline, Discount chain |
| 5 | **[The Strategy Pattern](book_ch5_strategy_pattern.md)** | Strategy | Payment processing, Shipping calculators, Report export, DI + Factory |
| 6 | **[Pattern Comparison Master Guide](book_ch6_pattern_comparison.md)** | All patterns | Family tree, decision flowchart, composition examples, anti-patterns |

---

## 🧠 One-Page Quick Reference

### When to Use Each Pattern

```
Complex object coordination?          → Mediator (MediatR + CQRS)
Notify unknown number of listeners?   → Observer (C# events / IObservable)
Request passed to one of many handlers→ Chain of Responsibility (middleware)
Store an action for undo / queue?     → Command (Execute + Undo)
Swap algorithm at runtime?            → Strategy (IEnumerable<IStrategy> + DI)
```

### The MediatR Mental Model (Most Important)

```
Controller/Client
       │  sends IRequest (POCO record)
       ▼
   IMediator.Send()
       │
       ├──► [LoggingBehavior]          ← Chain of Responsibility
       │         │
       │    ├──► [ValidationBehavior]  ← Chain of Responsibility
       │    │         │
       │    │    ├──► [CachingBehavior]
       │    │    │         │
       │    │    │    ├──► [IRequestHandler]   ← Business logic
       │    │    │    │         │
       │    │    │    │    Uses IStrategy       ← Strategy pattern
       │    │    │    │         │
       │    │    │    └── Publishes INotification  ← Observer pattern
       │    │    │              │
       │    │    │         ├──► [Handler1]  (Email)
       │    │    │         ├──► [Handler2]  (Search Index)
       │    │    │         └──► [Handler3]  (Audit Log)
```

---

## 📌 Key NuGet Packages

| Package | Pattern | Install Command |
|---|---|---|
| `MediatR` | Mediator | `dotnet add package MediatR` |
| `FluentValidation` | Validation Behavior | `dotnet add package FluentValidation.DependencyInjectionExtensions` |
| `Polly` | Retry Behavior | `dotnet add package Polly` |
| `System.Reactive` | IObservable / Rx.NET | `dotnet add package System.Reactive` |
| `ClosedXML` | Excel Strategy | `dotnet add package ClosedXML` |

---

## 🗺️ Pattern Relationship Map

```
                    ┌─────────────────┐
                    │    MEDIATOR     │
                    │ (MediatR/CQRS)  │
                    └────────┬────────┘
                             │ dispatches to
              ┌──────────────┼──────────────┐
              ▼              ▼              ▼
    ┌──────────────┐  ┌───────────┐  ┌──────────────┐
    │   CHAIN OF   │  │ STRATEGY  │  │   OBSERVER   │
    │RESPONSIBILITY│  │(algorithm)│  │(notification)│
    │ (behaviors)  │  └───────────┘  └──────────────┘
    └──────────────┘
              │
    ┌─────────┴────────┐
    │     COMMAND      │
    │(action as object)│
    └──────────────────┘
```

---

## 🧪 Testing Cheat Sheet

```csharp
// Unit test a MediatR handler directly — no mocking IMediator needed
var handler = new GetProductByIdQueryHandler(mockRepository);
var result  = await handler.Handle(new GetProductByIdQuery(42), CancellationToken.None);

// Integration test through the full pipeline
var mediator = serviceProvider.GetRequiredService<IMediator>();
var result   = await mediator.Send(new CreateProductCommand("Widget", 9.99m, 100));

// Test a Strategy in isolation
var strategy = new CreditCardPaymentStrategy(mockGateway);
var result   = await strategy.ProcessAsync(new PaymentRequest(...), CancellationToken.None);

// Test a Chain of Responsibility handler in isolation
var handler  = new BotHandler(mockBotService);
handler.SetNext(new Level1AgentHandler()); // or leave null to test boundary
handler.Handle(new SupportTicket { Category = "FAQ", Description = "reset password" });
```

---

*Happy coding! Patterns are tools — choose the right one for the right job.*
