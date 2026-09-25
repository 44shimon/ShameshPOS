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

## Login and the main screen

- **Login:** tap your name under *Who's working?*, then type your PIN on the keypad (or keyboard) and press
  LOGIN / Enter. The PIN is checked against the chosen user only; five wrong PINs lock the register for 30 seconds.
- **Main screen:** the register shows up to **10 products as big buttons** (2 across, 5 down, in the right 35% of the screen). Choose them in
  Administration > Products by ticking **Show on main screen** (the 11th is refused); *Button Order* sets
  their order. Every other product is found by typing in the search box or scanning its barcode.

## Using the POS

| Key | Action |
|---|---|
| F2 | Focus search (clears it) |
| F3 | Menu (↑/↓ + Enter, or the item number; Esc closes) |
| F4 | Customer: attach a customer to the sale, or take a payment on their account |
| F8 | Payment |
| Enter | In search: add exact code/SKU/barcode match, else top result. In payment: complete |
| Esc | Close overlay / clear search |
| Delete | Remove selected line (when the search box is empty) |
| + / − | Change selected line quantity (when the search box is empty) |
| ↑ / ↓ | Select cart line |

## Customer accounts

Customers can have a charge account: purchases are charged to the account and paid off later.

- **At the register:** press **F4** (or "+ Add customer"), find the customer by name, account number or
  phone, and choose **Add to Sale**. At payment, **Charge to Account** is offered only when a customer is
  attached, and is refused if the charge would take the balance over the customer's credit limit.
- **Paying off:** F4 → **Take Payment** records cash/card/other paid towards the balance.
- **Credit limit:** the most a customer may owe. Each customer uses the store default
  (Administration → Settings, initially $50.00) unless they have their own limit. A limit of 0.00 stops charging.
- **Administration → Customers:** create/edit customers (account numbers are assigned automatically from 1001
  if left blank), set limits, close accounts, see the account history, and make reasoned balance adjustments.
- Every charge, payment and adjustment is a row in `customer_transactions` with the balance it left, the user
  and the terminal, so the balance always equals the sum of the history. Charges lock the customer row inside
  the sale transaction, so two registers can't both spend the same available credit.
- Account payments are not sales. End-of-day cash totals (phase 5) must include them from `customer_transactions`.

## Importing lists (Administration → Products / Customers → Import…)

Import items and customers from **Excel (.xlsx, .xls) or CSV**. The first row must hold column headings;
common names are recognised (e.g. *Item / Description / Product* for the name, *Retail* for price,
*Dept* for category, *PLU* for code, *UPC / EAN* for barcode, *First Name + Last Name*, *Acct*, *Mobile*,
*Owes*). "Save Template…" writes a ready-made CSV with the expected headings.

Nothing is written until you confirm the **preview**, which shows how each column was matched and what
every row will do: **New**, **Update** (with the exact changes), **Unchanged**, or **Problem** (with the
reason; these rows are skipped). The import then runs in one transaction.

- **Products** are matched by code, SKU or barcode, then by name. New products need a name and price;
  missing categories are created; products without a category go to "Uncategorized". Tax accepts
  `8.875`, `8.875%` or `0.08875`. If a code matches an existing product with a *different* name, the preview
  flags it as a **RENAME**. Check this when your codes may overlap the sample products (101–127).
- **Customers** are matched by account number, or by name when the file has none. Blank account numbers are
  assigned automatically. A *Balance* column becomes the **opening balance of new customers** (recorded in
  their history); existing customers' balances are never changed by an import.
- Importing the same file twice changes nothing the second time.
- Tip: format barcode/code columns as **Text** in Excel so leading zeros are kept.

## Administration (F3 → 8)

Administrators manage the register from **F3 → Administration**; the register (and any sale in progress)
is restored with **Esc / Back to Register**, and product changes appear immediately.

- **Products:** name, category, price, tax rate (%), product code, SKU, barcode (scan into the field),
  button order, available for sale. Code, SKU and barcode are unique across all products because they share
  the search/scan lookup. Products are disabled, never deleted, so sales history stays intact.
- **Categories:** name, tile color, button order, shown on register. Hiding a category hides its products.
- **Users:** display name, username, role, can log in, PIN (entered twice, masked). PINs must be unique
  (login is PIN-only) and easy PINs such as 1111 or 1234 are refused. You cannot demote or disable your own
  account, and the last active administrator cannot be removed.

Every change is permission-checked in the service layer and written to the log with who made it and what
changed (e.g. `Admin 1 updated product 1: Price 2.00 → 2.25`). PINs are never logged.

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

Built so far: phases 1–4 (foundation, login, catalog, cart, F3 menu, F8 payment, transactional sale completion,
product/category/user administration with roles).
Next (phase 5): reports and end of day, receipt printing, cash drawer, held sales, returns and backups. The F3
menu already lists these, and they report "not available yet".
