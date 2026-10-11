namespace ShmeshPOS.Security;

public enum Permission
{
    Sell,
    TakePayment,
    ViewOwnTransactions,
    ManageProducts,
    ManageUsers,
    ManageSettings,
    ManageCustomers,
    UseCustomerAccounts,
    HoldSales,
    ProcessReturns,
    OpenDrawer,

    /// <summary>Open the register's shift with a float and close it with a cash count.</summary>
    RunShifts,

    /// <summary>Take a percentage or amount off an item or the whole sale.</summary>
    ApplyDiscounts,

    /// <summary>Record deliveries and stock counts.</summary>
    ManageStock,
    ViewReports,
    ViewSalesHistory,
    ExitApplication,
}
