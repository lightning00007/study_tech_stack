# Chapter 5: The Strategy Pattern — Swappable Algorithms

> **Behavioral Pattern · GoF Classic · Foundation of Clean, Extensible Logic**
> *"Define a family of algorithms, encapsulate each one, and make them interchangeable. Strategy lets the algorithm vary independently from clients that use it."*
> — Gang of Four

---

## Table of Contents

1. [Introduction — The Problem of Conditional Logic](#1-introduction)
2. [Classic GoF Implementation](#2-classic-gof)
3. [Real-World: Payment Processing](#3-payment-processing)
4. [Real-World: Shipping Cost Calculators](#4-shipping-calculators)
5. [Real-World: Report Export (PDF, CSV, Excel)](#5-report-export)
6. [Real-World: Sorting and Filtering Strategies](#6-sorting-filtering)
7. [Strategy with Dependency Injection — The Factory Pattern](#7-strategy-with-di)
8. [Strategy vs. Command vs. Template Method](#8-strategy-vs-others)
9. [Summary](#9-summary)

---

## 1. Introduction — The Problem of Conditional Logic

Consider an e-commerce checkout that supports multiple payment methods: Credit Card, PayPal, Apple Pay, Crypto. The naive implementation uses a massive switch statement:

```csharp
// ❌ NAIVE: One method handles ALL payment types
public class CheckoutService
{
    public async Task<PaymentResult> ProcessPayment(PaymentRequest request)
    {
        // Every time a new payment method is added, THIS method must be modified.
        // Classic Open/Closed Principle violation.
        switch (request.Method)
        {
            case "CreditCard":
                // 30 lines of credit card logic
                var ccProcessor = new CreditCardProcessor();
                return await ccProcessor.ChargeCardAsync(
                    request.CardNumber, request.Cvv, request.Amount);

            case "PayPal":
                // 25 lines of PayPal SDK calls
                var paypalClient = new PayPalClient(_config.PayPalClientId);
                return await paypalClient.CreateOrderAsync(request.Amount, request.PayPalToken);

            case "ApplePay":
                // 20 lines of Apple Pay logic
                // ...

            case "Crypto":
                // 40 lines of blockchain logic
                // ...

            default:
                throw new UnsupportedPaymentMethodException(request.Method);
        }
    }
}
```

Every new payment method means a riskier change to a critical, large method.

### The Strategy Solution

Extract each algorithm into its own class implementing a common interface. The `CheckoutService` depends ONLY on the interface, not on any concrete implementation.

---

## 2. Classic GoF Implementation

### 2.1 The Strategy Interface

```csharp
// The Strategy interface — the contract all algorithms must fulfill
public interface ISortStrategy<T>
{
    void Sort(List<T> data);
    string StrategyName { get; }
}

// Context class that uses a strategy
public class DataSorter<T>
{
    private ISortStrategy<T> _strategy;

    public DataSorter(ISortStrategy<T> strategy)
        => _strategy = strategy;

    // Strategies can be swapped at RUNTIME
    public void SetStrategy(ISortStrategy<T> strategy)
        => _strategy = strategy;

    public void Sort(List<T> data)
    {
        Console.WriteLine($"Sorting using: {_strategy.StrategyName}");
        _strategy.Sort(data);
    }
}
```

### 2.2 Concrete Strategy Implementations

```csharp
// Strategy A: Bubble Sort (for small lists or educational purposes)
public class BubbleSortStrategy<T> : ISortStrategy<T> where T : IComparable<T>
{
    public string StrategyName => "Bubble Sort";

    public void Sort(List<T> data)
    {
        int n = data.Count;
        for (int i = 0; i < n - 1; i++)
            for (int j = 0; j < n - i - 1; j++)
                if (data[j].CompareTo(data[j + 1]) > 0)
                    (data[j], data[j + 1]) = (data[j + 1], data[j]);
    }
}

// Strategy B: Quick Sort (for large lists in memory)
public class QuickSortStrategy<T> : ISortStrategy<T> where T : IComparable<T>
{
    public string StrategyName => "Quick Sort";

    public void Sort(List<T> data)
    {
        if (data.Count <= 1) return;
        QuickSort(data, 0, data.Count - 1);
    }

    private void QuickSort(List<T> data, int low, int high)
    {
        if (low >= high) return;
        int pivot = Partition(data, low, high);
        QuickSort(data, low, pivot - 1);
        QuickSort(data, pivot + 1, high);
    }

    private int Partition(List<T> data, int low, int high)
    {
        var pivot = data[high];
        int i = low - 1;
        for (int j = low; j < high; j++)
            if (data[j].CompareTo(pivot) <= 0)
                (data[++i], data[j]) = (data[j], data[i]);
        (data[i + 1], data[high]) = (data[high], data[i + 1]);
        return i + 1;
    }
}

// Strategy C: LINQ OrderBy (simplest, leverages .NET runtime)
public class LinqSortStrategy<T> : ISortStrategy<T> where T : IComparable<T>
{
    public string StrategyName => "LINQ Sort";

    public void Sort(List<T> data)
    {
        var sorted = data.OrderBy(x => x).ToList();
        data.Clear();
        data.AddRange(sorted);
    }
}

// Usage
var data = new List<int> { 64, 34, 25, 12, 22, 11, 90 };
var sorter = new DataSorter<int>(new BubbleSortStrategy<int>());

sorter.Sort(data); // Sorting using: Bubble Sort

// Swap strategy at runtime — same context, different algorithm
sorter.SetStrategy(new QuickSortStrategy<int>());
sorter.Sort(data); // Sorting using: Quick Sort
```

---

## 3. Real-World: Payment Processing

The most classic enterprise use of Strategy:

```csharp
// The Strategy interface for payments
public interface IPaymentStrategy
{
    string MethodName { get; }
    bool CanHandle(PaymentRequest request); // Optional: self-selection
    Task<PaymentResult> ProcessAsync(PaymentRequest request, CancellationToken ct);
}

public record PaymentRequest(
    decimal Amount,
    string Currency,
    string Method,
    Dictionary<string, string> Metadata // Method-specific data
);

public record PaymentResult(
    bool Success,
    string? TransactionId,
    string? ErrorMessage,
    DateTime ProcessedAt
);

// --- Strategy A: Credit Card ---
public class CreditCardPaymentStrategy : IPaymentStrategy
{
    private readonly ICreditCardGateway _gateway;

    public CreditCardPaymentStrategy(ICreditCardGateway gateway) => _gateway = gateway;

    public string MethodName => "CreditCard";
    public bool CanHandle(PaymentRequest r) => r.Method == "CreditCard";

    public async Task<PaymentResult> ProcessAsync(PaymentRequest request, CancellationToken ct)
    {
        var cardNumber = request.Metadata["cardNumber"];
        var cvv        = request.Metadata["cvv"];
        var expiry     = request.Metadata["expiry"];

        var gatewayResult = await _gateway.ChargeAsync(cardNumber, cvv, expiry, request.Amount, ct);

        return gatewayResult.Approved
            ? new PaymentResult(true, gatewayResult.AuthCode, null, DateTime.UtcNow)
            : new PaymentResult(false, null, gatewayResult.DeclineReason, DateTime.UtcNow);
    }
}

// --- Strategy B: PayPal ---
public class PayPalPaymentStrategy : IPaymentStrategy
{
    private readonly IPayPalClient _payPal;

    public PayPalPaymentStrategy(IPayPalClient payPal) => _payPal = payPal;

    public string MethodName => "PayPal";
    public bool CanHandle(PaymentRequest r) => r.Method == "PayPal";

    public async Task<PaymentResult> ProcessAsync(PaymentRequest request, CancellationToken ct)
    {
        var token = request.Metadata["paypalToken"];
        var order = await _payPal.CaptureOrderAsync(token, request.Amount, ct);

        return order.Status == "COMPLETED"
            ? new PaymentResult(true, order.Id, null, DateTime.UtcNow)
            : new PaymentResult(false, null, $"PayPal order status: {order.Status}", DateTime.UtcNow);
    }
}

// --- Strategy C: Crypto ---
public class CryptoPaymentStrategy : IPaymentStrategy
{
    private readonly IBlockchainService _blockchain;

    public CryptoPaymentStrategy(IBlockchainService blockchain) => _blockchain = blockchain;

    public string MethodName => "Crypto";
    public bool CanHandle(PaymentRequest r) => r.Method is "Bitcoin" or "Ethereum" or "Crypto";

    public async Task<PaymentResult> ProcessAsync(PaymentRequest request, CancellationToken ct)
    {
        var walletAddress = request.Metadata["walletAddress"];
        var txHash = await _blockchain.VerifyAndConfirmPaymentAsync(
            walletAddress, request.Amount, request.Currency, ct);

        return txHash is not null
            ? new PaymentResult(true, txHash, null, DateTime.UtcNow)
            : new PaymentResult(false, null, "Transaction not confirmed on blockchain.", DateTime.UtcNow);
    }
}

// --- The Context: PaymentProcessor ---
// Uses the strategy pattern — knows nothing about specific payment methods
public class PaymentProcessor
{
    private readonly IEnumerable<IPaymentStrategy> _strategies;

    // DI injects ALL registered IPaymentStrategy implementations
    public PaymentProcessor(IEnumerable<IPaymentStrategy> strategies)
        => _strategies = strategies;

    public async Task<PaymentResult> ProcessAsync(PaymentRequest request, CancellationToken ct)
    {
        var strategy = _strategies.FirstOrDefault(s => s.CanHandle(request))
            ?? throw new UnsupportedPaymentMethodException($"No handler for '{request.Method}'");

        return await strategy.ProcessAsync(request, ct);
    }
}

// --- DI Registration (Program.cs) ---
// Adding a NEW payment method = ONLY add this line:
builder.Services.AddScoped<IPaymentStrategy, CreditCardPaymentStrategy>();
builder.Services.AddScoped<IPaymentStrategy, PayPalPaymentStrategy>();
builder.Services.AddScoped<IPaymentStrategy, CryptoPaymentStrategy>();
builder.Services.AddScoped<PaymentProcessor>();
// No other files change. Open/Closed Principle achieved.
```

---

## 4. Real-World: Shipping Cost Calculators

```csharp
public interface IShippingStrategy
{
    string CarrierName { get; }
    decimal CalculateCost(ShipmentDetails shipment);
    TimeSpan EstimatedDelivery(ShipmentDetails shipment);
}

public record ShipmentDetails(
    decimal WeightKg,
    Address From,
    Address To,
    bool IsFragile,
    bool RequiresSignature
);

// Strategy A: Standard Post
public class StandardPostStrategy : IShippingStrategy
{
    public string CarrierName => "Standard Post";

    public decimal CalculateCost(ShipmentDetails s)
    {
        var baseCost = s.WeightKg * 2.5m;
        if (s.IsFragile)       baseCost += 3m;
        if (s.RequiresSignature) baseCost += 2m;
        return Math.Round(baseCost, 2);
    }

    public TimeSpan EstimatedDelivery(ShipmentDetails s)
        => TimeSpan.FromDays(7);
}

// Strategy B: Express Courier
public class ExpressCourierStrategy : IShippingStrategy
{
    public string CarrierName => "Express Courier";

    public decimal CalculateCost(ShipmentDetails s)
    {
        var baseCost = s.WeightKg * 8m + 10m; // Premium base
        if (s.IsFragile)       baseCost += 5m;
        if (s.RequiresSignature) baseCost += 1m;
        return Math.Round(baseCost, 2);
    }

    public TimeSpan EstimatedDelivery(ShipmentDetails s)
        => TimeSpan.FromDays(1);
}

// Strategy C: Same-Day Delivery
public class SameDayDeliveryStrategy : IShippingStrategy
{
    public string CarrierName => "Same-Day Delivery";

    public bool IsAvailable(ShipmentDetails s)
        => s.From.City == s.To.City; // Only within same city

    public decimal CalculateCost(ShipmentDetails s)
        => s.WeightKg * 15m + 25m;

    public TimeSpan EstimatedDelivery(ShipmentDetails s)
        => TimeSpan.FromHours(4);
}

// Context: Shipping Calculator that shows all options
public class ShippingCalculator
{
    private readonly IEnumerable<IShippingStrategy> _strategies;

    public ShippingCalculator(IEnumerable<IShippingStrategy> strategies)
        => _strategies = strategies;

    public IEnumerable<ShippingOption> GetAllOptions(ShipmentDetails shipment)
    {
        return _strategies
            .Select(s => new ShippingOption(
                s.CarrierName,
                s.CalculateCost(shipment),
                s.EstimatedDelivery(shipment)))
            .OrderBy(o => o.Cost);
    }
}

public record ShippingOption(string Carrier, decimal Cost, TimeSpan Delivery);

// Usage
var shipment = new ShipmentDetails(
    WeightKg: 2.5m,
    From: new Address { City = "New York" },
    To: new Address { City = "New York" }, // Same city
    IsFragile: true,
    RequiresSignature: false
);

var calculator = new ShippingCalculator(new IShippingStrategy[]
{
    new StandardPostStrategy(),
    new ExpressCourierStrategy(),
    new SameDayDeliveryStrategy()
});

foreach (var option in calculator.GetAllOptions(shipment))
    Console.WriteLine($"{option.Carrier}: ${option.Cost} | {option.Delivery.TotalDays} day(s)");

// Standard Post:        $9.25  | 7 day(s)
// Express Courier:      $35.00 | 1 day(s)
// Same-Day Delivery:    $62.50 | 0.17 day(s)  (4 hours)
```

---

## 5. Real-World: Report Export (PDF, CSV, Excel)

```csharp
public interface IReportExportStrategy
{
    string Format { get; }
    string ContentType { get; }
    Task<byte[]> ExportAsync(ReportData data, CancellationToken ct);
}

// Strategy A: CSV Export
public class CsvExportStrategy : IReportExportStrategy
{
    public string Format => "CSV";
    public string ContentType => "text/csv";

    public Task<byte[]> ExportAsync(ReportData data, CancellationToken ct)
    {
        var sb = new StringBuilder();

        // Header row
        sb.AppendLine(string.Join(",", data.Columns.Select(EscapeCsv)));

        // Data rows
        foreach (var row in data.Rows)
            sb.AppendLine(string.Join(",", row.Select(v => EscapeCsv(v?.ToString() ?? ""))));

        return Task.FromResult(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static string EscapeCsv(string value)
        => value.Contains(',') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}

// Strategy B: JSON Export
public class JsonExportStrategy : IReportExportStrategy
{
    public string Format => "JSON";
    public string ContentType => "application/json";

    public Task<byte[]> ExportAsync(ReportData data, CancellationToken ct)
    {
        var records = data.Rows.Select(row =>
            data.Columns.Zip(row, (col, val) => new { col, val })
                        .ToDictionary(x => x.col, x => x.val));

        var json = JsonSerializer.Serialize(records, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        return Task.FromResult(Encoding.UTF8.GetBytes(json));
    }
}

// Strategy C: Excel Export (using ClosedXML)
public class ExcelExportStrategy : IReportExportStrategy
{
    public string Format => "Excel";
    public string ContentType => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public async Task<byte[]> ExportAsync(ReportData data, CancellationToken ct)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Report");

        // Header row — bold, colored
        for (int col = 0; col < data.Columns.Count; col++)
        {
            var cell = sheet.Cell(1, col + 1);
            cell.Value = data.Columns[col];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.DarkBlue;
            cell.Style.Font.FontColor = XLColor.White;
        }

        // Data rows
        for (int row = 0; row < data.Rows.Count; row++)
            for (int col = 0; col < data.Rows[row].Count; col++)
                sheet.Cell(row + 2, col + 1).Value = data.Rows[row][col]?.ToString() ?? "";

        // Auto-fit columns
        sheet.ColumnsUsed().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}

// Controller using the export strategy
[ApiController]
[Route("api/reports")]
public class ReportsController : ControllerBase
{
    private readonly IEnumerable<IReportExportStrategy> _exportStrategies;
    private readonly IReportService _reportService;

    public ReportsController(
        IEnumerable<IReportExportStrategy> exportStrategies,
        IReportService reportService)
    {
        _exportStrategies = exportStrategies;
        _reportService    = reportService;
    }

    [HttpGet("{reportId}/export")]
    public async Task<IActionResult> Export(int reportId, [FromQuery] string format, CancellationToken ct)
    {
        var strategy = _exportStrategies.FirstOrDefault(
            s => s.Format.Equals(format, StringComparison.OrdinalIgnoreCase))
            ?? throw new BadRequestException($"Unsupported format: {format}. Supported: CSV, JSON, Excel");

        var data   = await _reportService.GetReportDataAsync(reportId, ct);
        var bytes  = await strategy.ExportAsync(data, ct);
        var filename = $"report-{reportId}.{format.ToLower()}";

        return File(bytes, strategy.ContentType, filename);
    }
}
```

---

## 6. Real-World: Sorting and Filtering Strategies

Strategies work powerfully with generic constraints for collection operations:

```csharp
// Comparison Strategy
public interface IProductSortStrategy
{
    string Name { get; }
    IOrderedEnumerable<Product> Sort(IEnumerable<Product> products);
}

public class SortByPriceAscending : IProductSortStrategy
{
    public string Name => "price_asc";
    public IOrderedEnumerable<Product> Sort(IEnumerable<Product> products)
        => products.OrderBy(p => p.Price);
}

public class SortByPriceDescending : IProductSortStrategy
{
    public string Name => "price_desc";
    public IOrderedEnumerable<Product> Sort(IEnumerable<Product> products)
        => products.OrderByDescending(p => p.Price);
}

public class SortByRating : IProductSortStrategy
{
    public string Name => "rating";
    public IOrderedEnumerable<Product> Sort(IEnumerable<Product> products)
        => products.OrderByDescending(p => p.AverageRating);
}

public class SortByRelevance : IProductSortStrategy
{
    private readonly string _searchQuery;
    public string Name => "relevance";

    public SortByRelevance(string searchQuery) => _searchQuery = searchQuery;

    public IOrderedEnumerable<Product> Sort(IEnumerable<Product> products)
    {
        // Score products by how closely they match the query
        return products.OrderByDescending(p =>
            p.Name.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ? 10 :
            p.Description.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ? 5 : 0);
    }
}

// Using Func<T,TKey> as a lightweight strategy (no interface needed for simple cases)
public class ProductCatalog
{
    private List<Product> _products = new();

    // Accept a strategy as a delegate — simplest form of Strategy pattern
    public IEnumerable<Product> GetSorted(Func<IEnumerable<Product>, IOrderedEnumerable<Product>> sortStrategy)
        => sortStrategy(_products);
}

// Usage
var catalog = new ProductCatalog();
var byPrice = catalog.GetSorted(products => products.OrderBy(p => p.Price));
var byName  = catalog.GetSorted(products => products.OrderBy(p => p.Name));
```

---

## 7. Strategy with Dependency Injection — The Factory Pattern

In production, you combine Strategy with a Factory (or use DI directly) to resolve strategies at runtime:

```csharp
// Strategy Resolver — acts as a factory for strategies
public interface IStrategyResolver<T>
{
    T Resolve(string key);
    IEnumerable<T> GetAll();
}

public class PaymentStrategyResolver : IStrategyResolver<IPaymentStrategy>
{
    private readonly IEnumerable<IPaymentStrategy> _strategies;

    public PaymentStrategyResolver(IEnumerable<IPaymentStrategy> strategies)
        => _strategies = strategies;

    public IPaymentStrategy Resolve(string method)
    {
        return _strategies.FirstOrDefault(s =>
            s.MethodName.Equals(method, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No payment strategy for method: {method}");
    }

    public IEnumerable<IPaymentStrategy> GetAll() => _strategies;
}

// Registration — clean and extensible
builder.Services.AddScoped<IPaymentStrategy, CreditCardPaymentStrategy>();
builder.Services.AddScoped<IPaymentStrategy, PayPalPaymentStrategy>();
builder.Services.AddScoped<IPaymentStrategy, CryptoPaymentStrategy>();
builder.Services.AddScoped<IStrategyResolver<IPaymentStrategy>, PaymentStrategyResolver>();

// Usage in a service
public class CheckoutService
{
    private readonly IStrategyResolver<IPaymentStrategy> _paymentResolver;

    public CheckoutService(IStrategyResolver<IPaymentStrategy> paymentResolver)
        => _paymentResolver = paymentResolver;

    public async Task<OrderResult> CheckoutAsync(CheckoutRequest request, CancellationToken ct)
    {
        var strategy = _paymentResolver.Resolve(request.PaymentMethod);
        var result   = await strategy.ProcessAsync(request.PaymentDetails, ct);
        // ...
        return new OrderResult(result.TransactionId);
    }

    // Show available payment methods to the customer
    public IEnumerable<string> GetAvailablePaymentMethods()
        => _paymentResolver.GetAll().Select(s => s.MethodName);
}
```

---

## 8. Strategy vs. Command vs. Template Method

| Aspect | Strategy | Command | Template Method |
|---|---|---|---|
| **Core Idea** | Swap ALGORITHM at runtime | Encapsulate ACTION as object | Define algorithm SKELETON in base, fill steps in subclasses |
| **What changes?** | How something is done | What is done (and when) | Which steps of a fixed process |
| **Reversible?** | No | Yes (has Undo) | No |
| **Runtime swap?** | Yes | Yes (queue different commands) | No (set at compile time via subclassing) |
| **Return value?** | Yes (always has output) | Optional | Depends |
| **Structure** | Interface + implementations | Interface with Execute/Undo | Abstract class with abstract methods |

### Quick Example Comparison

```csharp
// STRATEGY: Different algorithms for the same task — swappable
public interface ICompressionStrategy
{
    byte[] Compress(byte[] data);
}
// GZip, Deflate, Brotli are all interchangeable strategies

// COMMAND: An action packaged as an object — for queuing/undo
public class CompressFileCommand : ICommand
{
    public void Execute() => _file.Compress(_strategy);
    public void Undo()    => _file.Decompress();
}

// TEMPLATE METHOD: Fixed skeleton, customizable steps — via inheritance
public abstract class DataProcessor
{
    // The skeleton — NEVER overridden
    public void Process()
    {
        ReadData();
        TransformData(); // <-- subclasses override this step
        WriteData();
    }

    protected abstract void TransformData(); // The "hook"

    private void ReadData()  => Console.WriteLine("Reading...");
    private void WriteData() => Console.WriteLine("Writing...");
}
```

---

## 9. Summary

- The **Strategy pattern** encapsulates interchangeable algorithms behind a common interface.
- The **key insight**: the context class (PaymentProcessor, ShippingCalculator, etc.) depends only on the **interface**, never on concrete implementations.
- This achieves the **Open/Closed Principle**: add new strategies (new payment method, new export format) without modifying any existing code.
- **Two DI approaches**:
  - `IEnumerable<IStrategy>` injection — inject all, pick the right one at runtime.
  - Named registrations with a factory/resolver — explicit strategy selection by key.
- Use **`Func<T>`** as a lightweight strategy when you don't need a full interface (simple sorting, filtering).
- Combine with **Chain of Responsibility** when you need the strategy to try multiple implementations in order.

---

*Next Chapter →* [Chapter 6: Pattern Comparison Master Guide](book_ch6_pattern_comparison.md)
*Previous Chapter →* [Chapter 4: Chain of Responsibility](book_ch4_chain_of_responsibility.md)
