# Database Migrations

## Overview

GrapeSeed uses **EF Core Code-First Migrations** to manage database schema changes.
This folder is the home for all migration files, organized by service.

---

## Folder Structure

```
migrations/
├── TenantService/
│   └── {timestamp}_{MigrationName}.cs     ← Shared schema (tenants, outbox_messages)
├── IdentityService/
│   └── {timestamp}_{MigrationName}.cs     ← Per-tenant schema (students, refresh_tokens)
├── VideoService/
│   └── {timestamp}_{MigrationName}.cs     ← Per-tenant schema (videos)
└── RecommendationService/
    └── {timestamp}_{MigrationName}.cs     ← Per-tenant schema (watch_history)
```

---

## How to Create a New Migration

Navigate to the service directory and run:

```bash
# Example: add a new column to the Tenants table
cd src/Services/TenantService

dotnet ef migrations add AddTenantDisplayName \
  --context TenantDbContext \
  --output-dir ../../database/migrations/TenantService
```

## How to Apply Migrations

### Local development (shared schema only)

```bash
cd src/Services/TenantService
dotnet ef database update --context TenantDbContext
```

### Per-tenant schemas (all existing tenants)

Run the `MigrationRunner` utility, which iterates all tenant schemas:

```bash
dotnet run --project src/Tools/MigrationRunner -- \
  --service IdentityService \
  --connection-string "Host=localhost;Database=grapeseed;..."
```

---

## 📖 CONCEPT: Migration Strategy for Multi-Tenant Systems

### Forward-Compatible Changes (safe to deploy anytime)
- Adding a nullable column
- Adding a new table
- Adding an index

### Backward-Incompatible Changes (require a rolling deployment strategy)
- Dropping a column (old code still reads it → crash)
- Renaming a column
- Changing a column's data type

For backward-incompatible changes, follow the Expand-Contract pattern:
1. **Expand**: Add the new column/table. Both old and new code work.
2. **Migrate**: Run a data migration script.
3. **Contract**: Remove the old column after all services are updated.

---

## Rollback Strategy

EF Core migrations are **not automatically reversible**. Always:
1. Take a database backup before applying migrations in production.
2. Test migrations on a copy of a production-sized tenant schema first.
3. For each migration, write a corresponding `Down()` method that reverses it.

```csharp
// Example: Down() method for adding a column
protected override void Down(MigrationBuilder migrationBuilder)
{
    migrationBuilder.DropColumn(
        name: "display_name",
        schema: "shared",
        table: "tenants");
}
```
