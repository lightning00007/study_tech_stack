namespace GrapeSeed.SharedKernel.Domain;

// =============================================================================
// 📖 CONCEPT: Value Object
// =============================================================================
// A Value Object is an object that is defined by its attributes (values) rather
// than by a unique identity. Two Value Objects with the same attributes are
// considered identical — like two $5 bills.
//
// Key characteristics:
//   1. Immutable: once created, its values never change. Instead, you create a
//      new instance with modified values.
//   2. Equality by structure: two Value Objects are equal if all their components
//      are equal (unlike entities, which are equal only if their IDs match).
//   3. No side effects: Value Object methods never change external state.
//
// Examples in GrapeSeed:
//   - Money(100m, "USD") — an amount of money in a currency
//   - Email("alice@school.com") — a validated email address
//   - S3Key("raw/school-a/vid-001/original.mp4") — an immutable S3 object path
// =============================================================================

/// <summary>
/// Base class for all value objects. Provides structural equality based on component values.
/// </summary>
public abstract class ValueObject
{
    /// <summary>
    /// Derived classes must return the sequence of values that define equality.
    /// Two ValueObjects are equal if and only if all components are equal.
    /// </summary>
    protected abstract IEnumerable<object> GetEqualityComponents();

    public override bool Equals(object? obj)
    {
        if (obj is null || obj.GetType() != GetType()) return false;
        return GetEqualityComponents()
            .SequenceEqual(((ValueObject)obj).GetEqualityComponents());
    }

    public override int GetHashCode() =>
        GetEqualityComponents()
            .Aggregate(default(HashCode), (hc, comp) => { hc.Add(comp); return hc; })
            .ToHashCode();

    public static bool operator ==(ValueObject? left, ValueObject? right) => Equals(left, right);
    public static bool operator !=(ValueObject? left, ValueObject? right) => !Equals(left, right);
}

// =============================================================================
// 📖 EXAMPLE: Money Value Object
// =============================================================================
// Money is a classic Value Object example. It is immutable and structurally equal.
// Notice: there is no setter, only init. Once created, you cannot change the amount
// or currency — you create a new Money instance instead.
// =============================================================================

/// <summary>
/// Represents a monetary amount in a specific currency.
/// Immutable — create a new instance instead of modifying this one.
/// </summary>
public sealed class Money : ValueObject
{
    public decimal Amount { get; }
    public string Currency { get; }  // ISO 4217 currency code, e.g., "USD", "EUR"

    private Money() { }  // Required by EF Core's owned entity mapping

    public Money(decimal amount, string currency)
    {
        if (amount < 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount cannot be negative.");
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
            throw new ArgumentException("Currency must be a 3-character ISO 4217 code.", nameof(currency));

        Amount = amount;
        Currency = currency.ToUpperInvariant();
    }

    // 📖 CONCEPT: Arithmetic methods return new instances (immutability)
    public Money Add(Money other)
    {
        if (Currency != other.Currency)
            throw new InvalidOperationException($"Cannot add {Currency} to {other.Currency}.");
        return new Money(Amount + other.Amount, Currency);
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Amount;
        yield return Currency;
    }

    public override string ToString() => $"{Amount:F2} {Currency}";
}

/// <summary>
/// Represents a validated email address as a Value Object.
/// Construction validates the format — if you hold an Email instance, you know it's valid.
/// </summary>
public sealed class Email : ValueObject
{
    public string Value { get; }

    public Email(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.Contains('@'))
            throw new ArgumentException($"'{value}' is not a valid email address.", nameof(value));
        Value = value.ToLowerInvariant().Trim();
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value;

    // Implicit conversion allows using Email where a string is expected
    public static implicit operator string(Email email) => email.Value;
}
