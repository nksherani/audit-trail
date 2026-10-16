# Audit Trail Demo — CRUD foundation

A small .NET 10 controller-based Web API using EF Core 10 and SQL Server.
Audit tracking is left for the next stage.

## Run

Requires the .NET 10 SDK and SQL Server. The default connection uses the local
default SQL Server instance, Windows authentication, and database `AuditTrailDemo`.
Your SQL login needs permission to create the database, or configure an existing
empty database with schema permissions.

Edit `ConnectionStrings:DefaultConnection` in `src/AuditTrail.Api/appsettings.json`
for a different server. Keep SQL passwords out of source control; the environment
variable `ConnectionStrings__DefaultConnection` overrides this setting.
`TrustServerCertificate=True` is for local development; use a trusted server
certificate when deploying.

From the solution folder:

```powershell
dotnet restore
dotnet tool restore
dotnet ef database update --project src/AuditTrail.Api
dotnet run --project src/AuditTrail.Api --launch-profile http
```

The API runs at `http://localhost:5063`. Its development OpenAPI document is at
`http://localhost:5063/openapi/v1.json`. Use `src/AuditTrail.Api/AuditTrail.Api.http`
to exercise the endpoints. Replace its ID variables with IDs returned by POST.
Migrations are applied explicitly, rather than automatically on startup.

With the API running, `pwsh -File scripts/Smoke-Test.ps1` checks all three CRUD
flows, validation, and relationships against SQL Server and removes its records.

After changing the model:

```powershell
dotnet ef migrations add YourMigrationName --project src/AuditTrail.Api
dotnet ef database update --project src/AuditTrail.Api
```

## Model and endpoints

Each product belongs to one category and one supplier.

| Resource | Fields | Route |
| --- | --- | --- |
| Category | Id, Name, Description | `/api/categories` |
| Supplier | Id, Name, Email | `/api/suppliers` |
| Product | Id, Name, Price, StockQuantity, CategoryId, SupplierId | `/api/products` |

Every route supports GET (list), GET `/{id}`, POST, PUT `/{id}`, and DELETE `/{id}`.
POST returns 201 and a Location header; PUT and DELETE return 204. Missing resources
return 404 and invalid requests return 400. Product reads include category and
supplier names. Deleting a category or supplier that still has products returns
409; delete or reassign the products first.

Request DTOs use data annotations. Price and stock cannot be negative, and products
must reference existing categories and suppliers. SQL Server also enforces foreign
keys and nonnegative price/stock check constraints.

## Project layout

- `Entities/`: three plain EF entities.
- `Data/AppDbContext.cs`: DbSets, relationships, and database configuration.
- `Contracts/`: request and response DTOs.
- `Controllers/`: CRUD actions using tracked EF operations and SaveChangesAsync.
- `Migrations/`: initial SQL Server schema and model snapshot.

This is an unauthenticated local demo with no seeded data. No audit entities,
interceptors, or SaveChanges overrides have been added.

## Application-generated GUID keys

All entity primary keys are `Guid` values initialized with `Guid.NewGuid()` in C#.
EF uses `ValueGeneratedNever()` for these keys; the final SQL Server schema has
no identity columns or GUID default generators. Product foreign keys, DTOs, and
route parameters also use GUIDs. Supply category/supplier IDs returned by POST
when creating a product; primary keys are not accepted in request DTOs.

`UseApplicationGeneratedGuids` upgrades existing integer keys without deleting
rows, assigning GUIDs to historical rows once and remapping product references.
Existing external references to integer IDs must be updated. This migration
cannot restore the original integer IDs on rollback; restore a backup to revert.
