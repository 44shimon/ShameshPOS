using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShmeshPOS.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMainScreenItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "show_on_main_screen",
                table: "products",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_products_show_on_main_screen",
                table: "products",
                column: "show_on_main_screen",
                filter: "show_on_main_screen");

            // Existing stores: start the main screen with the first ten active products in register order,
            // so the register isn't empty after upgrading. Administrators change the selection afterwards.
            migrationBuilder.Sql("""
                UPDATE products SET show_on_main_screen = TRUE
                WHERE id IN (
                    SELECT p.id FROM products p JOIN categories c ON c.id = p.category_id
                    WHERE p.is_active AND c.is_active
                    ORDER BY c.sort_order, p.sort_order, p.name
                    LIMIT 10);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_products_show_on_main_screen",
                table: "products");

            migrationBuilder.DropColumn(
                name: "show_on_main_screen",
                table: "products");
        }
    }
}
