# Chapter 2: The Observer Pattern — Event-Driven Decoupling

> **Behavioral Pattern · GoF Classic · Foundation of Event-Driven Systems**
> *"Define a one-to-many dependency between objects so that when one object changes state, all its dependents are notified and updated automatically."*
> — Gang of Four

---

## Table of Contents

1. [Introduction — What Is the Observer Pattern?](#1-introduction)
2. [Classic GoF Implementation](#2-classic-gof)
3. [The .NET Built-In Way: Events and Delegates](#3-dotnet-events-delegates)
4. [IObservable / IObserver — The Reactive Approach](#4-iobservable-iobserver)
5. [Real-World: Domain Events in DDD](#5-domain-events-ddd)
6. [Observer vs. Mediator — The Key Differences](#6-observer-vs-mediator)
7. [Summary](#7-summary)

---

## 1. Introduction — What Is the Observer Pattern?

The Observer pattern solves a specific problem: **how do you notify multiple objects when something changes, without hard-coding who those objects are?**

The object being observed is called the **Subject** (or Publisher/Observable). The objects watching it are called **Observers** (or Subscribers/Listeners).

```
Subject (Publisher)
    │
    ├──notifies──► Observer A  (e.g., UI Chart)
    ├──notifies──► Observer B  (e.g., Alert System)
    └──notifies──► Observer C  (e.g., Logger)
```

**Key distinction from Mediator**: In the Observer pattern, the Subject knows it has observers (it maintains a list of them). In the Mediator pattern, components know nothing about each other — only the Mediator knows everyone.

---

## 2. Classic GoF Implementation

### 2.1 The Interfaces

```csharp
// The Subject interface — the thing being watched
public interface ISubject
{
    void Attach(IObserver observer);
    void Detach(IObserver observer);
    void Notify();
}

// The Observer interface — the thing that watches
public interface IObserver
{
    void Update(ISubject subject);
}
```

### 2.2 A Concrete Subject — Stock Price Monitor

```csharp
public class StockTicker : ISubject
{
    private readonly List<IObserver> _observers = new();
    private readonly Dictionary<string, decimal> _prices = new();

    public IReadOnlyDictionary<string, decimal> Prices => _prices;

    public void Attach(IObserver observer)
    {
        if (!_observers.Contains(observer))
            _observers.Add(observer);
    }

    public void Detach(IObserver observer)
        => _observers.Remove(observer);

    public void SetPrice(string symbol, decimal price)
    {
        _prices[symbol] = price;
        Console.WriteLine($"[StockTicker] {symbol} price updated to ${price}");
        Notify(); // Automatically notify all observers
    }

    public void Notify()
    {
        foreach (var observer in _observers)
            observer.Update(this);
    }
}
```

### 2.3 Concrete Observers

```csharp
// Observer 1: Price Alert System
public class PriceAlertObserver : IObserver
{
    private readonly string _symbol;
    private readonly decimal _threshold;

    public PriceAlertObserver(string symbol, decimal threshold)
    {
        _symbol    = symbol;
        _threshold = threshold;
    }

    public void Update(ISubject subject)
    {
        if (subject is not StockTicker ticker) return;

        if (ticker.Prices.TryGetValue(_symbol, out var price) && price >= _threshold)
            Console.WriteLine($"  🔔 ALERT: {_symbol} hit ${price} (threshold: ${_threshold})!");
    }
}

// Observer 2: Portfolio Tracker
public class PortfolioTracker : IObserver
{
    private readonly Dictionary<string, int> _holdings;

    public PortfolioTracker(Dictionary<string, int> holdings)
        => _holdings = holdings;

    public void Update(ISubject subject)
    {
        if (subject is not StockTicker ticker) return;

        var totalValue = _holdings.Sum(h =>
            ticker.Prices.TryGetValue(h.Key, out var p) ? p * h.Value : 0);

        Console.WriteLine($"  📊 Portfolio value: ${totalValue:F2}");
    }
}

// Observer 3: Price History Logger
public class PriceHistoryLogger : IObserver
{
    private readonly List<(DateTime Time, string Symbol, decimal Price)> _history = new();

    public void Update(ISubject subject)
    {
        if (subject is not StockTicker ticker) return;

        foreach (var (symbol, price) in ticker.Prices)
            _history.Add((DateTime.UtcNow, symbol, price));
    }

    public IReadOnlyList<(DateTime, string, decimal)> GetHistory() => _history;
}
```

### 2.4 Usage

```csharp
var ticker = new StockTicker();

var alert     = new PriceAlertObserver("AAPL", threshold: 185m);
var portfolio = new PortfolioTracker(new Dictionary<string, int>
{
    ["AAPL"] = 100,
    ["MSFT"] = 50
});
var logger = new PriceHistoryLogger();

// Subscribe all observers
ticker.Attach(alert);
ticker.Attach(portfolio);
ticker.Attach(logger);

ticker.SetPrice("AAPL", 180m);
// [StockTicker] AAPL price updated to $180
//   📊 Portfolio value: $18000.00

ticker.SetPrice("MSFT", 420m);
// [StockTicker] MSFT price updated to $420
//   📊 Portfolio value: $39000.00

ticker.SetPrice("AAPL", 186m);
// [StockTicker] AAPL price updated to $186
//   🔔 ALERT: AAPL hit $186 (threshold: $185)!
//   📊 Portfolio value: $39600.00

// Detach an observer dynamically — no changes to StockTicker or other observers
ticker.Detach(alert);
```

---

## 3. The .NET Built-In Way: Events and Delegates

.NET has the Observer pattern built into the language via **events** and **delegates**. This is the idiomatic C# way for most scenarios.

### 3.1 Events with EventHandler

```csharp
// Define the event data
public class PriceChangedEventArgs : EventArgs
{
    public string Symbol { get; init; } = string.Empty;
    public decimal OldPrice { get; init; }
    public decimal NewPrice { get; init; }
    public decimal ChangePercent => OldPrice == 0 ? 0 : (NewPrice - OldPrice) / OldPrice * 100;
}

// The Subject using C# events
public class StockTickerModern
{
    private readonly Dictionary<string, decimal> _prices = new();

    // The event — observers subscribe by += and unsubscribe by -=
    public event EventHandler<PriceChangedEventArgs>? PriceChanged;

    public void SetPrice(string symbol, decimal newPrice)
    {
        var oldPrice = _prices.GetValueOrDefault(symbol, 0m);
        _prices[symbol] = newPrice;

        // Raise the event — the ?. handles the case where no one is subscribed
        PriceChanged?.Invoke(this, new PriceChangedEventArgs
        {
            Symbol   = symbol,
            OldPrice = oldPrice,
            NewPrice = newPrice
        });
    }
}

// Usage — idiomatic C# event subscription
var ticker = new StockTickerModern();

// Subscribe with a lambda
ticker.PriceChanged += (sender, e) =>
{
    Console.WriteLine($"Price changed: {e.Symbol} {e.ChangePercent:+0.##;-0.##}%");
};

// Subscribe with a method
ticker.PriceChanged += OnPriceChanged;

void OnPriceChanged(object? sender, PriceChangedEventArgs e)
{
    if (e.ChangePercent is > 5 or < -5)
        Console.WriteLine($"⚠️  Significant move on {e.Symbol}!");
}

ticker.SetPrice("AAPL", 186m);
// Price changed: AAPL +0%  (first set, no old price)
ticker.SetPrice("AAPL", 200m);
// Price changed: AAPL +7.53%
// ⚠️  Significant move on AAPL!

// Unsubscribe — IMPORTANT: always unsubscribe to prevent memory leaks
ticker.PriceChanged -= OnPriceChanged;
```

### 3.2 Action<T> — Lightweight Alternative to Events

For simpler scenarios, `Action<T>` is a lightweight alternative:

```csharp
public class OrderProcessor
{
    // Instead of event EventHandler<>, use Action<T> for simplicity
    public Action<Order>? OnOrderPlaced { get; set; }
    public Action<Order, string>? OnOrderFailed { get; set; }

    public async Task ProcessOrder(Order order)
    {
        try
        {
            await ValidateAndChargeAsync(order);
            OnOrderPlaced?.Invoke(order);
        }
        catch (Exception ex)
        {
            OnOrderFailed?.Invoke(order, ex.Message);
        }
    }
}

// Usage
var processor = new OrderProcessor();
processor.OnOrderPlaced = order => Console.WriteLine($"Order {order.Id} placed!");
processor.OnOrderFailed = (order, msg) => Console.WriteLine($"Order {order.Id} failed: {msg}");
```

---

## 4. IObservable / IObserver — The Reactive Approach

.NET provides `IObservable<T>` and `IObserver<T>` interfaces as the foundation for reactive programming (Rx.NET).

```csharp
// The Producer implements IObservable<T>
public class StockTickerReactive : IObservable<PriceChangedEventArgs>
{
    private readonly List<IObserver<PriceChangedEventArgs>> _observers = new();
    private readonly Dictionary<string, decimal> _prices = new();

    public IDisposable Subscribe(IObserver<PriceChangedEventArgs> observer)
    {
        _observers.Add(observer);
        return new Unsubscriber(_observers, observer); // Returns a token to unsubscribe
    }

    public void SetPrice(string symbol, decimal price)
    {
        var old = _prices.GetValueOrDefault(symbol, 0m);
        _prices[symbol] = price;

        foreach (var observer in _observers)
            observer.OnNext(new PriceChangedEventArgs { Symbol = symbol, OldPrice = old, NewPrice = price });
    }

    // Inner class to handle unsubscription via IDisposable
    private class Unsubscriber : IDisposable
    {
        private readonly List<IObserver<PriceChangedEventArgs>> _observers;
        private readonly IObserver<PriceChangedEventArgs> _observer;

        public Unsubscriber(
            List<IObserver<PriceChangedEventArgs>> observers,
            IObserver<PriceChangedEventArgs> observer)
        {
            _observers = observers;
            _observer  = observer;
        }

        public void Dispose()
        {
            if (_observers.Contains(_observer))
                _observers.Remove(_observer);
        }
    }
}

// An Observer
public class AlertObserver : IObserver<PriceChangedEventArgs>
{
    public void OnNext(PriceChangedEventArgs value)
        => Console.WriteLine($"Received: {value.Symbol} = ${value.NewPrice}");

    public void OnError(Exception error)
        => Console.WriteLine($"Error: {error.Message}");

    public void OnCompleted()
        => Console.WriteLine("Stream completed.");
}

// Usage
var ticker = new StockTickerReactive();
var observer = new AlertObserver();

using var subscription = ticker.Subscribe(observer); // IDisposable — auto-unsubscribes on dispose
ticker.SetPrice("GOOG", 150m);
// Received: GOOG = $150
```

---

## 5. Real-World: Domain Events in DDD

In Domain-Driven Design, the Observer pattern powers **Domain Events** — a way to signal that something important happened in your domain.

```csharp
// Base class for all domain events
public abstract record DomainEvent
{
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
}

// Domain events are part of your domain model — named in past tense
public record OrderPlaced(int OrderId, string CustomerId, decimal Total) : DomainEvent;
public record OrderCancelled(int OrderId, string Reason) : DomainEvent;
public record PaymentFailed(int OrderId, string ErrorCode) : DomainEvent;

// Domain entities collect events
public class Order
{
    private readonly List<DomainEvent> _domainEvents = new();
    public IReadOnlyList<DomainEvent> DomainEvents => _domainEvents;

    public int Id { get; private set; }
    public string Status { get; private set; } = "Pending";

    public void Place(string customerId, decimal total)
    {
        Status = "Placed";
        // Record the event — do NOT publish yet
        _domainEvents.Add(new OrderPlaced(Id, customerId, total));
    }

    public void Cancel(string reason)
    {
        Status = "Cancelled";
        _domainEvents.Add(new OrderCancelled(Id, reason));
    }

    public void ClearDomainEvents() => _domainEvents.Clear();
}

// The dispatcher (usually called after SaveChanges)
public class DomainEventDispatcher
{
    private readonly IMediator _mediator;

    public DomainEventDispatcher(IMediator mediator) => _mediator = mediator;

    public async Task DispatchAsync(IEnumerable<DomainEvent> events, CancellationToken ct)
    {
        foreach (var domainEvent in events)
        {
            // Publish each domain event via MediatR — all handlers get it
            await _mediator.Publish(domainEvent, ct);
        }
    }
}
```

---

## 6. Observer vs. Mediator — The Key Differences

This is the most important section for understanding when to use which pattern.

| Dimension | Observer | Mediator |
|---|---|---|
| **Communication** | One-to-Many (one subject, many observers) | Many-to-Many (any colleague to any other) |
| **Coupling** | Subject knows it has observers (a list) | Colleagues know nothing about each other |
| **Direction** | Unidirectional: Subject pushes to Observers | Bidirectional: Colleagues both send and receive |
| **Logic location** | Observers decide what to do with data | Mediator decides what to do and who to call |
| **Adding observers** | Subscribe/unsubscribe at runtime | Register new handlers via DI |
| **Best for** | Event broadcasting, reactive UIs, domain events | Complex workflow orchestration, CQRS, UI dialogs |

### Decision Flowchart

```
Is there ONE authoritative source of change?
    │
    ├── YES → Does it need to notify many things?
    │               │
    │               └── YES → USE OBSERVER
    │
    └── NO → Do many objects need to coordinate complex workflows?
                    │
                    └── YES → USE MEDIATOR
```

---

## 7. Summary

- The **Observer pattern** is for **reactive notification** — "something changed, who cares?"
- Use C# **events/delegates** for simple, in-process observer scenarios.
- Use **IObservable<T>** with Rx.NET for complex reactive streams (filtering, throttling, combining).
- In DDD, Observer powers **Domain Events** — significant occurrences captured in your domain model and dispatched after transaction commit.
- Observer and Mediator complement each other beautifully: Mediator handles the **request/response workflow**, Observer handles the **"after the fact" notifications**.

---

*Next Chapter →* [Chapter 3: The Command Pattern](book_ch3_command_pattern.md)
*Previous Chapter →* [Chapter 1: The Mediator Pattern](book_ch1_mediator_pattern.md)
