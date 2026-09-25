# Shmesh POS

Touch- and keyboard-friendly point of sale for a small canteen / concession stand.
WPF on .NET 9, MVVM, PostgreSQL through EF Core + Npgsql.

## Solution layout

```
ShmeshPOS.sln
src/ShmeshPOS/
  Models/            Entities (User, Category, Product, Sale, SaleItem, Payment, PaymentMethod, ReceiptSequence)
  Data/              PosDbContext, entity configurations, migrations, start-up initializer/seeder
  Security/          PinHasher (PBKDF2), roles → permissions
  Services/          Authentication, Session, Product catalog, Payment methods, Sale (transactional), receipt numbers
  ViewModels/        MVVM view models (CommunityToolkit.Mvvm)
  Views/             XAML screens and overlays
  Controls/          Reusable controls (numeric keypad)
  Resources/Styles/  Colors and control styles
  Infrastructure/    Options, keyboard routing
tests/ShmeshPOS.Tests/  Unit tests + PostgreSQL integration tests
scripts/            Database setup SQL
```

## First-time setup (development)

1. **Create the database and role.** Edit the password in `scripts/create-database.sql`, then run it as the
   `postgres` superuser (psql or pgAdmin).

2. **Store the connection string** outside source control. In Debug builds, user secrets work:

   ```powershell
   cd src/ShmeshPOS
   dotnet user-secrets set "ConnectionStrings:ShmeshPos" "Host=localhost;Database=shmesh_pos;Username=shmesh_pos;Password=<password>"
   dotnet user-secrets set "Seed:AdminPin" "<4-8 digit PIN>"
   ```

3. **Run:** `dotnet run --project src/ShmeshPOS`. On first start the app applies migrations, seeds payment
   methods, the administrator (from `Seed:AdminPin`) and a sample canteen catalog.

For a windowed session while developing: `$env:SHMESHPOS_Pos__FullScreen = "false"`.

## Configuration on a register

Settings are layered (later wins):

1. `appsettings.json` next to the exe: defaults, **no secrets**
2. `%ProgramData%\ShmeshPOS\appsettings.json`: per-machine settings, including the real connection string.
   Restrict this file's ACL to the POS Windows account and administrators.
3. User secrets (Debug builds only)
4. Environment variables prefixed `SHMESHPOS_`, e.g. `SHMESHPOS_ConnectionStrings__ShmeshPos`

Alternatively omit `Password` from the connection string and use a PostgreSQL
[password file](https://www.postgresql.org/docs/current/libpq-pgpass.html) (`%APPDATA%\postgresql\pgpass.conf`).

| Key | Default | Meaning |
|---|---|---|
| `ConnectionStrings:ShmeshPos` | localhost / shmesh_pos | PostgreSQL connection |
| `Pos:TerminalName` | machine name | Stored on each sale |
| `Pos:FullScreen` | `true` | Borderless kiosk window; Alt+F4 blocked. Admin exits via F3 → Exit |
| `Pos:Culture` | `en-US` | Currency formatting |
| `Pos:MaxFailedLoginAttempts` / `LoginLockoutSeconds` | 5 / 30 | PIN brute-force throttle |
| `Database:AutoMigrate` | `true` | Apply migrations at start-up |
| `Seed:AdminPin` | *(empty)* | PIN of the first administrator, used only while the users table is empty |
| `Seed:SampleData` | `true` | Sample categories/products when the catalog is empty |

Logs: `%ProgramData%\ShmeshPOS\logs\shmeshpos-YYYYMMDD.log` (60 days kept). PINs are never logged.

**Network:** PostgreSQL should listen only on the store's local network. Restrict `listen_addresses` and
`pg_hba.conf` to the register subnet, and never port-forward 5432 to the internet.

## Using the POS

| Key | Action |
|---|---|
| F2 | Focus search (clears it) |
| F3 | Menu (↑/↓ + Enter, or the item number; Esc closes) |
| F8 | Payment |
| Enter | In search: add exact code/SKU/barcode match, else top result. In payment: complete |
| Esc | Close overlay / clear search |
| Delete | Remove selected line (when the search box is empty) |
| + / − | Change selected line quantity (when the search box is empty) |
| ↑ / ↓ | Select cart line |

Barcode scanners in keyboard-wedge mode work out of the box: the search box keeps focus, and the scanner's
trailing Enter adds the matching product.

## Design notes

- **Money** is `decimal` everywhere (`numeric` in PostgreSQL). Prices are tax exclusive; tax is rounded per
  line, half away from zero. `SaleCalculator` is shared by the cart display and the sale service.
- **Completing a sale** is one PostgreSQL transaction: receipt number, sale, lines and payment. Any failure
  rolls back everything, including the receipt number.
- **Receipt numbers** (`2026-000001`) come from a per-year counter row updated with
  `INSERT … ON CONFLICT DO UPDATE … RETURNING` inside the sale transaction. The row lock serialises
  concurrent registers, and the numbers are unique and gap-free. They are independent of the sale's primary key.
- **Sale lines** snapshot product name, price and tax rate.
- **PINs** are hashed with PBKDF2-HMAC-SHA256 (150k iterations, random salt). Login is PIN-only, so each
  user's PIN must be unique (enforce this when user management is added). Short PINs are guessable offline
  if the database leaks. The real protection is keeping the database private.
- **Authorisation** checks permissions, not roles (`Security/RolePermissions.cs`), so new roles are a one-line change.
- **Catalog** is loaded into memory at each login; search never touches the database.

## Database migrations

```powershell
dotnet tool restore
cd src/ShmeshPOS
dotnet ef migrations add <Name> --output-dir Data/Migrations
dotnet ef migrations script --idempotent -o ../../scripts/migrate.sql   # for DBA-applied upgrades
```

## Tests

```powershell
dotnet test
# Integration tests (create/drop a throwaway database; role needs CREATEDB):
$env:SHMESHPOS_TEST_CONNECTION = "Host=localhost;Username=shmesh_pos;Password=<password>"
dotnet test
```

## Roadmap

Built so far: phases 1–3 (foundation, login, catalog, cart, F3 menu, F8 payment, transactional sale completion).
Next: user and product administration (phase 4), then reports, receipt printing, cash drawer, held sales,
returns and backups (phase 5). The F3 menu already lists these, and they report "not available yet".
