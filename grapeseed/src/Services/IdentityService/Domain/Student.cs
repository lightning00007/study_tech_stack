using GrapeSeed.SharedKernel.Domain;

namespace GrapeSeed.IdentityService.Domain;

// =============================================================================
// 📖 CONCEPT: Student Aggregate Root
// =============================================================================
// The Student entity in IdentityService is NOT the same as the "Student" concept
// in VideoService or RecommendationService. This is the bounded context principle:
// within IdentityService, a Student is simply a set of credentials.
//
// All that IdentityService needs to know about a student:
//   - Who are they? (Id, FullName, Email)
//   - Can they authenticate? (PasswordHash, IsActive)
//   - Which tenant do they belong to? (TenantId — for schema scoping)
//
// IdentityService doesn't care about watch history, video preferences, or
// anything else — that belongs to other bounded contexts.
// =============================================================================

/// <summary>Strongly-typed identifier for a Student.</summary>
public sealed record StudentId(Guid Value)
{
    public static StudentId New() => new(Guid.NewGuid());
    public static StudentId From(Guid value) => new(value);
}

/// <summary>
/// Represents a student's authentication identity within a specific tenant.
/// This is the IdentityService's view of a student — credentials only.
/// </summary>
public sealed class Student : AggregateRoot<StudentId>
{
    public string FullName { get; private set; } = string.Empty;
    public Email Email { get; private set; } = null!;

    /// <summary>
    /// Bcrypt hash of the student's password.
    /// 
    /// 📖 CONCEPT: Password Hashing with BCrypt
    /// We NEVER store plain-text passwords. BCrypt is the industry-standard
    /// algorithm for password hashing because:
    ///   1. It includes a random "salt" to prevent rainbow table attacks.
    ///   2. It has a configurable "work factor" (cost) that makes brute-force
    ///      attacks computationally expensive — even with powerful hardware.
    ///   3. The hash includes the salt and cost factor, so verification is simple:
    ///      BCrypt.Verify(inputPassword, storedHash)
    ///
    /// ⚠️ GOTCHA: Never use MD5, SHA-1, or SHA-256 for passwords. They are too
    /// fast. A GPU can compute billions of SHA-256 hashes per second. BCrypt
    /// is designed to be slow by design.
    /// </summary>
    public string PasswordHash { get; private set; } = string.Empty;

    /// <summary>
    /// Whether this student can log in. False if the tenant is suspended
    /// or if the student's account has been deactivated.
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// The tenant this student belongs to. Used for schema selection.
    /// Every database query for this student is scoped to TenantId's schema.
    /// </summary>
    public Guid TenantId { get; private set; }

    public DateTime CreatedAt { get; private init; }
    public DateTime? LastLoginAt { get; private set; }

    private Student() { }

    /// <summary>
    /// Creates a new Student with a hashed password.
    /// </summary>
    public static Student Create(
        string fullName,
        Email email,
        string plainTextPassword,
        Guid tenantId)
    {
        if (string.IsNullOrWhiteSpace(plainTextPassword) || plainTextPassword.Length < 8)
            throw new ArgumentException("Password must be at least 8 characters.", nameof(plainTextPassword));

        return new Student
        {
            Id = StudentId.New(),
            FullName = fullName.Trim(),
            Email = email,
            // 📖 CONCEPT: BCrypt.HashPassword includes a random salt automatically.
            // The work factor (12) means each hash attempt takes ~250ms — manageable
            // for login but prohibitively slow for brute-force attacks.
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(plainTextPassword, workFactor: 12),
            IsActive = true,
            TenantId = tenantId,
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Verifies the provided password against the stored hash.
    /// Returns true if the password matches, false otherwise.
    /// </summary>
    public bool VerifyPassword(string plainTextPassword)
    {
        // 📖 CONCEPT: BCrypt.Verify is time-constant — it takes the same time
        // whether the password is correct or not. This prevents timing attacks
        // where an attacker deduces if they're "close" to the right password
        // by measuring response time.
        return BCrypt.Net.BCrypt.Verify(plainTextPassword, PasswordHash);
    }

    /// <summary>Records a successful login attempt.</summary>
    public void RecordLogin()
    {
        LastLoginAt = DateTime.UtcNow;
    }

    /// <summary>Deactivates the student account (e.g., when tenant is suspended).</summary>
    public void Deactivate()
    {
        IsActive = false;
    }
}
