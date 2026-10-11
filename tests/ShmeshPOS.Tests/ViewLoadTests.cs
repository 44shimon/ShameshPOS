using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using ShmeshPOS.Views;
using ShmeshPOS.Views.Admin;

namespace ShmeshPOS.Tests;

/// <summary>
/// Loads every screen's XAML against the application's resources. XAML problems such as a missing
/// StaticResource or a bad StringFormat only show up at run time, so this catches them before a register does.
/// </summary>
public class ViewLoadTests
{
    [Fact]
    public void Every_view_loads_with_the_application_resources()
    {
        var failures = new List<string>();
        ExceptionDispatchInfo? crash = null;

        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();

                var views = typeof(App).Assembly.GetTypes()
                    .Where(t => t.IsSubclassOf(typeof(UserControl)) && !t.IsAbstract
                                && (t.Namespace == typeof(PosView).Namespace || t.Namespace == typeof(ProductsAdminView).Namespace)
                                && t.GetConstructor(Type.EmptyTypes) is not null)
                    .ToList();
                Assert.Contains(typeof(ShiftView), views);
                Assert.Contains(typeof(DiscountView), views);

                foreach (var type in views)
                {
                    try
                    {
                        Activator.CreateInstance(type);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{type.Name}: {(ex.InnerException ?? ex).Message}");
                    }
                }
                app.Shutdown();
            }
            catch (Exception ex)
            {
                crash = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        crash?.Throw();
        Assert.Empty(failures);
    }
}
