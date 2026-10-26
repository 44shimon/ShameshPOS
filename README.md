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
scripts/            Database setup SQL, publish and installer build scripts, icon generator
installer/          Inno Setup script for ShmeshPOS-Setup-<version>.exe
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
| `Pos:StoreName` | `Shmesh POS` | Shown on the login and sale screens and printed on reports |
| `Pos:TerminalName` | machine name | Stored on each sale |
| `Pos:FullScreen` | `false` | `true`: borderless kiosk window, Alt+F4 blocked, admin exits via F3 → Exit. `false`: normal window |
| `Pos:Culture` | `en-US` | Currency formatting |
| `Pos:MaxFailedLoginAttempts` / `LoginLockoutSeconds` | 5 / 30 | PIN brute-force throttle |
| `Database:AutoMigrate` | `true` | Apply migrations at start-up |
| `Seed:AdminPin` | *(empty)* | PIN of the first administrator, used only while the users table is empty |
| `Seed:SampleData` | `false` | Sample categories/products when the catalog is empty (for demos and testing) |
| `Backup:Enabled` | `true` | Daily `pg_dump` backup from this computer; skipped with one warning if `pg_dump.exe` isn't installed |

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
| F6 | Discount on the selected item or the whole sale (administrators) |
| F8 | Payment |
| Enter | In search: add exact code/SKU/barcode match, else top result. In payment: complete |
| Esc | Close overlay / clear search |
| Delete | Remove selected line (when the search box is empty) |
| + / − | Change selected line quantity (when the search box is empty) |
| ↑ / ↓ | Select cart line |

## Hold, retrieve, returns and the cash drawer

- **Hold Sale (F3 → 2):** parks the current sale (items, prices and customer) with an optional note such as a
  name or table number, and clears the register for the next customer. Held sales are shared by all registers.
- **Retrieve Sale (F3 → 3):** lists held sales (note, items, total, who held it and where). Retrieve puts it
  back in the cart; each held sale can only be retrieved once, even if two registers try at the same moment.
  The register must be empty first.
- **Returns (F3 → 6, administrators):** scan or type the receipt number, choose how many of each item come
  back ("Return Everything" for a full refund), choose how the money goes back (the original payment method
  is offered first; *Charge to Account* credits the customer's account when the sale had one), add a reason,
  then **Refund**. Nothing can be returned twice: quantities are checked against earlier refunds while the
  sale is locked. Returning the last of a line gives back exactly what was charged, including tax.
  Refund numbers are the receipt number plus `-R1`, `-R2`, … A refund slip can be printed.
- **Reports** show refunds on the day they happen, **net sales** (sales − refunds), and take cash refunds out
  of the expected drawer cash.
- **Cash drawer:** if a drawer is plugged into the receipt printer (Administration → Settings, "A cash drawer is
  plugged into this printer"), it opens for cash sales and cash refunds, and from **F3 → 5 Open Drawer**
  ("no sale"). Every opening is logged with who and why. "Open Drawer Now" in Settings tests the cable.
  The signal is the standard ESC/POS pulse (Epson, Star, Bixolon and compatibles).

## Shifts, discounts, split payments and stock

- **Shifts (F3 → 4):** each register runs its own shift. After login a cashier or administrator is offered
  **Open Shift**: count the opening float into the drawer and enter it. Payment needs an open shift; pressing
  Pay without one opens it first. **Close Shift** (the cart must be empty) asks for the cash counted in the
  drawer and shows **expected** (float + cash sales after change + cash paid onto accounts − cash refunds),
  **counted**, and **over/short**, with a printable shift report. The top bar shows "Shift since …".
- **Discounts (F6 or the Discount button, administrators):** a percentage or an amount off the selected item,
  or off the whole sale. Discounts come off before tax; a whole-sale discount is spread over the lines to the
  cent, so returns give back exactly what was paid. They show under each line and above the subtotal, print on
  the receipt and appear in Reports. Held sales keep their discounts; a cashier retrieving one gets full prices.
- **Split payments:** in the payment screen, enter less than is due to take that amount as one part (e.g. $20
  cash), then pay the rest by another method. Only cash can give change. Nothing is saved until the parts cover
  the total; all parts are saved in the sale's single transaction.
- **Stock (Administration → Products):** tick **Track stock** and save, then use **Receive Delivery** (adds to
  what is on hand) or **Set Counted Qty** (replaces it with a shelf count; the difference is recorded). Sales
  take stock out and returns put it back. Set a **low-stock alert** level; low items show in red in the list,
  the count of them shows above it, and typing `low` in the search box lists them. Every change is kept in the
  product's **stock history**. Stock can go below zero if more is sold than was counted; sales are never blocked.

## Reports (F3 → 7, administrators)

- Pick **Today / Yesterday / This Week / This Month** or any From–To dates.
- **Summary:** total sales, number of sales, average sale, items sold, tax; totals by payment method and
  by cashier; top items; customer-account charges and payments; and the **cash expected in the drawer**
  (cash sales + cash paid onto accounts; add your opening float). A one-day period is the **end-of-day
  report**, shown as it will print; **Print Report** sends it to the receipt printer.
- **Sales:** every sale in the period with time, cashier, customer and payment; click one to see its receipt
  and **Reprint Receipt**.
- **Shifts:** every shift opened in the period, per register, with float, expected and counted cash and
  over/short (short in red). Click one to see its report and **Print Shift Report**. An open shift shows its
  figures so far.
- Figures come from the saved sales (names and prices as they were when sold). Account payments are not
  sales, so they appear under Customer accounts and in the cash drawer, not in total sales.

## Receipts and printing

- After each sale the Sale Complete message has **Print Receipt** (Enter/Esc still mean Next Customer).
  With **automatic printing** on, the receipt prints straight away and the button reprints it.
- Administration → Settings: receipt **header** (store name, address, phone) and **footer**, shared by all
  registers; this register's **receipt printer** (any installed Windows printer; install the printer's
  Windows driver first, e.g. Epson TM-series or Star TSP) and **automatic printing**; **Print Test Receipt**.
- Receipts are 42-column text for 80 mm paper, printed as one continuous page so roll printers cut once.

## Backups and restore

- The POS makes a full backup (`pg_dump`, custom format) **once a day** while it runs, to
  `%ProgramData%\ShmeshPOS\backups\shmesh_pos_YYYYMMDD_HHMMSS.backup`, and deletes backups older than 30 days.
  Administration → Settings shows the last backup and has **Back Up Now**.
- `pg_dump.exe` is found automatically under `C:\Program Files\PostgreSQL\<version>\bin`; install the
  PostgreSQL command-line tools on the computer that should make backups (normally the server). A computer
  without them logs one warning at start-up and makes no automatic backups. The installer turns the daily backup
  on only when the database is on the same computer; elsewhere set `"Backup": { "Enabled": true }` yourself.
  Settings: `Backup:Enabled`, `Backup:Folder`, `Backup:KeepDays`, `Backup:PgDumpPath`.
- **Copy backups off the machine** (USB drive, another PC, cloud folder); a backup on the same disk doesn't
  survive a disk failure.
- **Restore** (administrator, with every register closed):

  ```powershell
  # Installer's database server: the tools are in C:\Program Files\ShmeshPOS\pgsql\bin, and the port and
  # superuser password are in C:\ProgramData\ShmeshPOS\database-admin.txt (add -p <port> if it isn't 5432).
  # into a new database first, to check it:
  & "C:\Program Files\PostgreSQL\18\bin\createdb.exe" -U postgres -O shmesh_pos shmesh_pos_restored
  & "C:\Program Files\PostgreSQL\18\bin\pg_restore.exe" -U postgres -d shmesh_pos_restored --no-owner --role=shmesh_pos "C:\ProgramData\ShmeshPOS\backups\shmesh_pos_20260925_055959.backup"
  # then point the registers' connection string at shmesh_pos_restored (or rename the databases).
  ```

## Installing on a register

The installer contains everything a clean Windows 10/11 (64-bit) computer needs: the self-contained app (no .NET
install), and for the database server PostgreSQL 18 and the Microsoft Visual C++ runtime.

1. **Build the installer** on the development PC: `.\scripts\build-installer.ps1`. It runs the tests, publishes the
   app, downloads PostgreSQL and the Visual C++ runtime once into `publish\deps` (checked against pinned SHA-256
   hashes) and creates `publish\installer\ShmeshPOS-Setup-<version>.exe`. It needs Inno Setup 6:
   `winget install --id JRSoftware.InnoSetup -e --source winget --scope user`.
   To try it on a clean Windows first, run `.\scripts\test-in-sandbox.ps1`: it opens Windows Sandbox with the
   installer on its desktop (turn Sandbox on once as administrator with
   `Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All`, then restart).
2. **Database server first** (the only computer for a single register, or the main computer): run the installer
   as an administrator and choose **Database server**. It installs PostgreSQL as the Windows service
   `ShmeshPOS-Database` (data in `%ProgramData%\ShmeshPOS\pgdata`, port 5432 or the next free one), creates the
   database and its login with random passwords, and connects Shmesh POS to it. Tick **Let other registers on the
   store network connect** if there will be more registers: it opens the port in Windows Firewall (private and
   domain networks only) and allows logins from the local subnet. The server name, port and passwords are saved in
   `%ProgramData%\ShmeshPOS\database-admin.txt`, which only administrators can open.
3. **Other registers:** run the installer and choose **Register only**, then enter the server name, port and
   register password from `database-admin.txt`.

   On both, the installer also asks for:
   - the **register name** (printed on receipts, e.g. `REGISTER-1`) and the store name;
   - **new database only:** a 4–8 digit PIN for the first administrator (leave blank on the other registers);
   - **full screen** (kiosk mode); for demo/training databases only, **sample products**; and whether this computer
     makes the **daily backup** (ticked on the database server, which has `pg_dump`);
   - the **Windows account** that runs the POS. The settings file holds the database password, so only that
     account, administrators and SYSTEM can read it. Enter `Users` to allow every local user.

   It installs to `C:\Program Files\ShmeshPOS`, adds Start menu (and optionally desktop and start-at-logon)
   shortcuts, and writes `%ProgramData%\ShmeshPOS\appsettings.json`. Database setup is logged to
   `%ProgramData%\ShmeshPOS\logs\setup-database.log`.
4. **Start** Shmesh POS. The first start creates the tables. Log in with the PIN from the installer, change it in
   Administration → Users, add real users, products and customers (or import them), and choose the 10
   main-screen items.
5. **Printer:** install the receipt printer's Windows driver, then pick it in Administration → Settings.

**Updating:** run the new installer. It closes Shmesh POS, stops and updates the database service, keeps the
settings and the database (unless you tick "Change settings"; even then the database and its password are kept),
and database changes are applied automatically at the next start.

**Uninstalling** (Settings → Apps) removes the program and the `ShmeshPOS-Database` service but keeps
`%ProgramData%\ShmeshPOS` (settings, logs, backups and the database itself). Installing again as Database server
reuses that database.

**Using your own PostgreSQL server** instead: install PostgreSQL, run `scripts/create-database.sql` with a strong
password, allow the registers in `pg_hba.conf`, and install every computer as **Register only**.

**Without the installer:** `.\scripts\publish.ps1` creates `publish\ShmeshPOS\`; copy it to the register, copy
`register-appsettings.example.json` to `%ProgramData%\ShmeshPOS\appsettings.json`, fill it in and restrict its
permissions by hand.

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
- **Stock** changes only through `stock_movements` rows (sale, refund, delivery, count), written in the same
  transaction as the sale or refund. Product rows are locked `FOR UPDATE` in id order, so two registers selling
  the last item can't lose an update or deadlock.
- **Shift close** saves the expected cash at the moment of closing, so a reprinted shift report never changes.

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

All five phases are built: foundation, selling, payment, administration, and reports/receipts/backups/
deployment, plus customer accounts, spreadsheet import and the 10-item main screen.
Phase 6 added hold/retrieve, returns and refunds, and the cash drawer. Phase 7 added shifts with opening
float and closing cash count, discounts, split payments, and stock tracking.
